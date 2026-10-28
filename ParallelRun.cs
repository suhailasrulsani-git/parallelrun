// ParallelRun.cs
//
// Our own parallel runner for Windows PowerShell 5.1 - one cmdlet,
// Invoke-ParallelRun. Written from scratch on 20 Oct 2026. Same idea as
// santhse's Invoke-All (Invokeall DLL), plus:
//   * -MaxThreads is always the real limit (also for Exchange remote commands)
//   * one result object per item: Index, Input, Status OK/FAILED, Output,
//     Error, Duration (or -RawOutput for plain output + real PowerShell errors)
//   * -TimeoutSeconds per item (stopped and marked FAILED, never retried)
//   * progress bar with done/total, failed, running, elapsed and ETA
//   * -RefreshMinutes / -RefreshScript: every N minutes, let running items
//     finish, run your reconnect once, start fresh threads, carry on
//
// Build:  .\build.ps1            (compiles this file to ParallelRun.dll)
// Load:   Import-Module .\ParallelRun.dll
// Run:    $mbxs | Invoke-ParallelRun { Get-MailboxStatistics -Identity $_.PrimarySmtpAddress } -MaxThreads 5
//
// Plain C# 5 on purpose: Add-Type in Windows PowerShell 5.1 uses the old
// .NET Framework compiler, so no $"..." strings, no ?. and no => members.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("ParallelRun")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace ParallelRun
{
    // What comes back for each input item (unless -RawOutput).
    // In PowerShell it behaves like a [pscustomobject] with these properties.
    public class ParallelRunResult
    {
        public int Index { get; set; }                   // position in the input, 1 = first
        public object Input { get; set; }                // the item ($_)
        public string Status { get; set; }               // OK or FAILED
        public PSObject[] Output { get; set; }           // what the { } returned
        public string Error { get; set; }                // every error message, joined with " | "
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public ErrorRecord[] ErrorRecords { get; set; }  // the full errors, same as $Error entries
    }

    // One item while it runs. "internal" = not visible from PowerShell.
    internal class RunJob
    {
        public int Index;
        public object Input;
        public PowerShell Shell;                         // like [powershell]::Create()
        public IAsyncResult Handle;                      // what BeginInvoke() gave back
        public PSDataCollection<PSObject> Output;
        public DateTime StartTime;
        public Stopwatch Clock;
        public bool TimedOut;
        public TimeSpan StopSentAt;
        public bool Abandoned;
    }

    // One argument of a command in the { }, worked out once before the run.
    internal class ArgPart
    {
        public string ParamName;                         // null = positional value
        public bool IsSwitch;                            // like -Archive (no value)
        public bool IsSplat;                             // like @params
        public object Constant;                          // fixed value, like -ResultSize Unlimited
        public ScriptBlock Script;                       // value per item, like $_.PrimarySmtpAddress
    }

    internal class CommandStep
    {
        public string Name;
        public List<ArgPart> Args = new List<ArgPart>();
    }

    [Cmdlet(VerbsLifecycle.Invoke, "ParallelRun")]
    [OutputType(typeof(ParallelRunResult))]
    public class InvokeParallelRunCommand : PSCmdlet
    {
        // ------------------------------------------------------------------
        // Parameters (each [Parameter] property = one -Parameter)
        // ------------------------------------------------------------------

        // The { } to run for each item. Use $_ for the item.
        [Parameter(Mandatory = true, Position = 0)]
        public ScriptBlock ScriptBlock { get; set; }

        // The items. Normally piped in.
        [Parameter(Mandatory = true, ValueFromPipeline = true)]
        [AllowNull]
        public object InputObject { get; set; }

        // How many items run at the same time. Always respected.
        [Parameter]
        [ValidateRange(1, 128)]
        public int MaxThreads { get; set; }

        // Stop an item after this many seconds and mark it FAILED. 0 = no limit.
        [Parameter]
        [ValidateRange(0, 86400)]
        public int TimeoutSeconds { get; set; }

        // Plain output like Invoke-All (no result objects). Each failed item
        // becomes a normal PowerShell error (-ErrorVariable, $Error).
        [Parameter]
        public SwitchParameter RawOutput { get; set; }

        // Skip the "run item 1 alone first" check.
        [Parameter]
        public SwitchParameter Force { get; set; }

        // No progress bar.
        [Parameter]
        public SwitchParameter Quiet { get; set; }

        // Optional log file, one tab-separated line per item. Off by default.
        [Parameter]
        public string LogPath { get; set; }

        // Extra modules / snap-ins for every thread (local runs only).
        [Parameter]
        public string[] ModulesToLoad { get; set; }

        [Parameter]
        public string[] PSSnapInsToLoad { get; set; }

        // Make your variables visible inside the { } (local runs only).
        [Parameter]
        public SwitchParameter CopyLocalVariables { get; set; }

        // Run on this remote session's server. Not needed for Import-PSSession
        // commands (Exchange) - their session is found automatically.
        [Parameter]
        public PSSession Session { get; set; }

        // Every this many minutes: no new items start, the running ones finish,
        // -RefreshScript runs once, the threads are rebuilt fresh, then it carries on.
        // 0 = never. Decimals are fine (0.5 = 30 seconds).
        [Parameter]
        [ValidateRange(0.0, 10080.0)]
        public double RefreshMinutes { get; set; }

        // What to run at each refresh, e.g. your Exchange Online reconnect. Runs in
        // your own window (not a thread), while nothing else is running.
        [Parameter]
        public ScriptBlock RefreshScript { get; set; }

        // Test only: run a one-command { } the remote way, on local threads.
        [Parameter(DontShow = true)]
        public SwitchParameter TestCommandMode { get; set; }

        public InvokeParallelRunCommand()
        {
            MaxThreads = Math.Min(Environment.ProcessorCount, 128);
        }

        // ------------------------------------------------------------------
        // State for one run
        // ------------------------------------------------------------------

        private const int StopGraceSeconds = 30;     // after a timeout, how long Stop gets before we give up
        private const int ProgressId = 8151;

        private readonly List<object> items = new List<object>();
        private readonly List<RunJob> running = new List<RunJob>();
        private readonly List<RunJob> abandoned = new List<RunJob>();
        // Background threads drop finished items here; the main thread picks them up.
        private readonly ConcurrentQueue<RunJob> finished = new ConcurrentQueue<RunJob>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private volatile bool stopping;

        private RunspacePool pool;                   // like [runspacefactory]::CreateRunspacePool()
        private InitialSessionState iss;             // what every local thread starts with
        private PSSession remoteSession;
        private RunspaceConnectionInfo remoteConnection;   // copied once, reused at each refresh
        private Stopwatch refreshClock;
        private int refreshCount;
        private List<CommandStep> steps;             // set = command mode (remote)
        private string scriptText;                   // set = script mode
        private string modeName;

        private StreamWriter log;
        private Stopwatch runClock;
        private long lastProgressMs = -1000;
        private int doneCount;
        private int failedCount;
        private bool firstItemFailed;
        private string firstItemError;

        // ------------------------------------------------------------------
        // begin / process / end - same three blocks as an advanced function
        // ------------------------------------------------------------------

        protected override void BeginProcessing()
        {
            ScriptBlockAst ast = (ScriptBlockAst)ScriptBlock.Ast;
            PipelineAst pipe = GetSinglePipeline(ast);

            // Does the { } call a command that came from Import-PSSession (Exchange)?
            PSModuleInfo remoteModule = null;
            string remoteCommand = null;
            foreach (Ast found in ast.FindAll(IsCommand, false))
            {
                CommandInfo info = Resolve(((CommandAst)found).GetCommandName());
                if (info != null && info.Module != null && IsImplicitRemoting(info.Module))
                {
                    remoteModule = info.Module;
                    remoteCommand = info.Name;
                    break;
                }
            }

            if (Session != null || remoteModule != null)
            {
                if (Session != null)
                {
                    if (!IsOpen(Session))
                        Fail("The session given to -Session is not open (" + Session.Name + "). Reconnect, then run again.",
                             "ParallelRunSessionClosed", ErrorCategory.ConnectionError, Session);
                    remoteSession = Session;
                }
                else
                {
                    remoteSession = FindSession(remoteModule, remoteCommand);
                }

                if (pipe != null)
                {
                    steps = BuildSteps(pipe);
                    modeName = "Remote command on " + remoteSession.ComputerName;
                }
                else if (Session == null)
                {
                    Fail("'" + remoteCommand + "' runs through a remote session (Import-PSSession). That kind of session takes " +
                         "one command or one pipeline only, so the { } must look like: { Get-MailboxStatistics -Identity $_.PrimarySmtpAddress }",
                         "ParallelRunNotOneCommand", ErrorCategory.InvalidArgument, ScriptBlock);
                }
                else
                {
                    scriptText = MakeScriptText(ast);
                    modeName = "Remote script on " + remoteSession.ComputerName;
                }

                if (ModulesToLoad != null || PSSnapInsToLoad != null || CopyLocalVariables)
                    WriteWarning("-ModulesToLoad, -PSSnapInsToLoad and -CopyLocalVariables are for local runs only. Ignored on a remote session.");
            }
            else
            {
                iss = BuildLocalSessionState(ast);
                if (TestCommandMode && pipe != null)
                {
                    steps = BuildSteps(pipe);
                    modeName = "Local command (test)";
                }
                else
                {
                    scriptText = MakeScriptText(ast);
                    modeName = "Local script";
                }
            }

            if (RefreshScript != null && RefreshMinutes <= 0)
                WriteWarning("-RefreshScript is set but -RefreshMinutes is 0, so it will never run. Add e.g. -RefreshMinutes 120.");

            WriteVerbose("Mode: " + modeName + ". MaxThreads: " + MaxThreads +
                         ". Timeout: " + (TimeoutSeconds > 0 ? TimeoutSeconds + "s" : "none") +
                         ". Refresh: " + (RefreshMinutes > 0 ? "every " + RefreshMinutes + " min" : "none") + ".");
        }

        protected override void ProcessRecord()
        {
            // Piped in: one call per item. Given with -InputObject: one call with the whole list.
            if (MyInvocation.ExpectingInput)
            {
                items.Add(InputObject);
                return;
            }
            IEnumerable list = LanguagePrimitives.GetEnumerable(InputObject);
            if (list == null)
            {
                items.Add(InputObject);
                return;
            }
            foreach (object o in list) items.Add(o);
        }

        protected override void EndProcessing()
        {
            if (items.Count == 0) return;

            runClock = Stopwatch.StartNew();
            try
            {
                OpenLog();
                OpenPool();
                refreshClock = Stopwatch.StartNew();

                int start = 0;
                if (!Force && items.Count > 1)
                {
                    // Item 1 alone first. If it fails, the rest would most likely fail the same way.
                    RunRange(0, 1);
                    if (stopping) return;
                    if (firstItemFailed)
                    {
                        Fail("Item 1 failed, so the other " + (items.Count - 1) + " items were not started. " +
                             "Fix the { } or add -Force to run them anyway. Item 1 error: " + firstItemError,
                             "ParallelRunFirstItemFailed", ErrorCategory.InvalidOperation, items[0]);
                    }
                    start = 1;
                }
                RunRange(start, items.Count);

                if (!stopping)
                {
                    WriteVerbose("Finished " + doneCount + " items: " + (doneCount - failedCount) + " OK, " +
                                 failedCount + " FAILED, in " + FormatTime(runClock.Elapsed) + ".");
                }
            }
            finally
            {
                Cleanup();
            }
        }

        // Ctrl+C lands here (on another thread). Just raise the flag; the main loop does the rest.
        protected override void StopProcessing()
        {
            stopping = true;
            wake.Set();
        }

        // ------------------------------------------------------------------
        // The run loop
        // ------------------------------------------------------------------

        private void RunRange(int from, int to)
        {
            int next = from;
            while (!stopping)
            {
                // Refresh due? Then start nothing new; once the running ones are done, refresh.
                bool refreshDue = RefreshMinutes > 0 && next < to && refreshClock.Elapsed.TotalMinutes >= RefreshMinutes;
                if (refreshDue && running.Count == 0)
                {
                    Refresh(to - next);
                    continue;
                }

                // Fill the free slots. We hand out work ourselves, so never more than MaxThreads run.
                while (!refreshDue && next < to && running.Count < MaxThreads && !stopping)
                {
                    StartJob(next);
                    next++;
                }
                if (next >= to && running.Count == 0) break;

                wake.WaitOne(250);

                RunJob job;
                while (finished.TryDequeue(out job)) Finish(job);
                CheckTimeouts();
                UpdateProgress(false);
            }
        }

        private void StartJob(int i)
        {
            RunJob job = new RunJob();
            job.Index = i + 1;
            job.Input = items[i];
            job.Output = new PSDataCollection<PSObject>();
            job.StartTime = DateTime.Now;
            job.Clock = Stopwatch.StartNew();
            job.Shell = PowerShell.Create();
            job.Shell.RunspacePool = pool;

            PSDataCollection<object> input = new PSDataCollection<object>();
            try
            {
                if (steps != null)
                {
                    AddCommands(job.Shell, job.Input);       // remote way: $_ worked out here, values sent over
                }
                else
                {
                    // Local way: the item goes in as $_. "true" = own scope per item, so a
                    // variable set by one item is gone before the next item on that thread.
                    job.Shell.AddScript(scriptText, true);
                    input.Add(job.Input);
                }
                input.Complete();

                // Start it without waiting - like $ps.BeginInvoke(). OnJobDone runs when it ends.
                job.Handle = job.Shell.BeginInvoke<object, PSObject>(input, job.Output, null, OnJobDone, job);
                running.Add(job);
            }
            catch (Exception ex)
            {
                // Could not even start (e.g. an argument failed to work out). FAILED, no retry.
                job.Clock.Stop();
                job.Shell.Dispose();
                Report(MakeResult(job, ex, null));
            }
        }

        // Runs on a background thread. Only queue the job and wake the main thread.
        private void OnJobDone(IAsyncResult handle)
        {
            finished.Enqueue((RunJob)handle.AsyncState);
            wake.Set();
        }

        private void Finish(RunJob job)
        {
            Exception ex = null;
            try { job.Shell.EndInvoke(job.Handle); }
            catch (Exception e) { ex = e; }

            if (job.Abandoned)
            {
                // Already reported as timed out; it has finally ended, so just clean up.
                abandoned.Remove(job);
                job.Shell.Dispose();
                return;
            }

            job.Clock.Stop();
            running.Remove(job);
            Collection<ErrorRecord> errors = job.Shell.Streams.Error.ReadAll();
            ParallelRunResult result = MakeResult(job, ex, errors);
            job.Shell.Dispose();
            Report(result);
        }

        private void CheckTimeouts()
        {
            if (TimeoutSeconds <= 0) return;

            foreach (RunJob job in running.ToArray())
            {
                TimeSpan age = job.Clock.Elapsed;
                if (!job.TimedOut)
                {
                    if (age.TotalSeconds >= TimeoutSeconds)
                    {
                        // Like $ps.Stop(), without waiting. Finish() reports it when it has stopped.
                        job.TimedOut = true;
                        job.StopSentAt = age;
                        try { job.Shell.BeginStop(null, null); } catch { }
                    }
                }
                else if ((age - job.StopSentAt).TotalSeconds >= StopGraceSeconds)
                {
                    // It ignored Stop (stuck inside a .NET call). Report it and stop waiting for it.
                    job.Abandoned = true;
                    job.Clock.Stop();
                    running.Remove(job);
                    abandoned.Add(job);
                    ParallelRunResult result = MakeResult(job, null, null);
                    result.Error = "Timed out after " + TimeoutSeconds + " seconds and did not stop. Its thread is left running.";
                    Report(result);
                }
            }
        }

        private ParallelRunResult MakeResult(RunJob job, Exception ex, Collection<ErrorRecord> errors)
        {
            ParallelRunResult r = new ParallelRunResult();
            r.Index = job.Index;
            r.Input = job.Input;
            r.StartTime = job.StartTime;
            r.Duration = job.Clock.Elapsed;
            r.EndTime = job.StartTime + r.Duration;
            r.Output = job.Output == null ? new PSObject[0] : ToArray(job.Output.ReadAll());

            List<string> messages = new List<string>();
            List<ErrorRecord> records = new List<ErrorRecord>();
            if (job.TimedOut)
            {
                messages.Add("Timed out after " + TimeoutSeconds + " seconds.");
            }
            else if (ex != null)
            {
                messages.Add(ex.Message);
                IContainsErrorRecord withRecord = ex as IContainsErrorRecord;
                if (withRecord != null && withRecord.ErrorRecord != null) records.Add(withRecord.ErrorRecord);
                else records.Add(new ErrorRecord(ex, "ParallelRunItemFailed", ErrorCategory.NotSpecified, job.Input));
            }
            if (errors != null)
            {
                foreach (ErrorRecord e in errors)
                {
                    messages.Add(e.ToString());
                    records.Add(e);
                }
            }

            r.Status = messages.Count == 0 ? "OK" : "FAILED";
            r.Error = messages.Count == 0 ? null : string.Join(" | ", messages.ToArray());
            r.ErrorRecords = records.ToArray();
            return r;
        }

        // Send one item's result out. Main thread only (Write* must not be called from background threads).
        private void Report(ParallelRunResult r)
        {
            doneCount++;
            bool failed = r.Status != "OK";
            if (failed) failedCount++;
            if (r.Index == 1 && failed)
            {
                firstItemFailed = true;
                firstItemError = r.Error;
            }
            WriteLog(r);

            if (!RawOutput)
            {
                WriteObject(WithDefaultView(r));
                return;
            }

            foreach (PSObject o in r.Output) WriteObject(o);
            if (failed)
            {
                Exception inner = r.ErrorRecords.Length > 0 ? r.ErrorRecords[0].Exception : null;
                RuntimeException err = new RuntimeException("Item " + r.Index + " (" + Text(r.Input) + ") FAILED: " + r.Error, inner);
                ErrorCategory cat = r.Error.StartsWith("Timed out") ? ErrorCategory.OperationTimeout : ErrorCategory.NotSpecified;
                WriteError(new ErrorRecord(err, "ParallelRunItemFailed", cat, r.Input));
            }
        }

        // Show Status, Duration, Input, Error as a table by default. The rest are still there (Select-Object *).
        private static PSObject WithDefaultView(ParallelRunResult r)
        {
            PSObject o = new PSObject(r);
            PSPropertySet view = new PSPropertySet("DefaultDisplayPropertySet", new string[] { "Status", "Duration", "Input", "Error" });
            o.Members.Add(new PSMemberSet("PSStandardMembers", new PSMemberInfo[] { view }));
            return o;
        }

        private void UpdateProgress(bool final)
        {
            if (Quiet) return;
            long now = runClock.ElapsedMilliseconds;
            if (!final && now - lastProgressMs < 500) return;
            lastProgressMs = now;

            ProgressRecord p = new ProgressRecord(ProgressId, "Invoke-ParallelRun", "Finishing");
            if (final)
            {
                p.RecordType = ProgressRecordType.Completed;
                WriteProgress(p);
                return;
            }

            int total = items.Count;
            TimeSpan elapsed = runClock.Elapsed;
            string eta = "--:--:--";
            if (doneCount > 0 && doneCount < total)
            {
                // Average time per finished item so far x items left. Threads are already in that average.
                double left = elapsed.TotalSeconds / doneCount * (total - doneCount);
                p.SecondsRemaining = (int)left;
                eta = FormatTime(TimeSpan.FromSeconds(left));
            }
            p.StatusDescription = string.Format("Done {0}/{1}   Failed {2}   Running {3}   Elapsed {4}   ETA {5}",
                doneCount, total, failedCount, running.Count, FormatTime(elapsed), eta);
            p.PercentComplete = (int)(doneCount * 100L / total);
            WriteProgress(p);
        }

        // ------------------------------------------------------------------
        // Opening and closing
        // ------------------------------------------------------------------

        private void OpenPool()
        {
            try
            {
                if (remoteSession != null)
                {
                    // Same server, same account, same settings as your session - but our own MaxThreads.
                    // Copied once, so a refresh still works if your own session was replaced meanwhile.
                    if (remoteConnection == null) remoteConnection = remoteSession.Runspace.ConnectionInfo;
                    WSManConnectionInfo wsman = remoteConnection as WSManConnectionInfo;
                    RunspaceConnectionInfo info = wsman != null ? wsman.Copy() : remoteConnection;
                    pool = RunspaceFactory.CreateRunspacePool(1, MaxThreads, info, Host);
                }
                else
                {
                    pool = RunspaceFactory.CreateRunspacePool(1, MaxThreads, iss, Host);
                    pool.ThreadOptions = PSThreadOptions.ReuseThread;
                }
                pool.Open();
            }
            catch (Exception ex)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("Could not open the threads (" + modeName + "): " + ex.Message, ex),
                    "ParallelRunPoolOpenFailed", ErrorCategory.OpenError, null));
            }
        }

        private void Cleanup()
        {
            // Ctrl+C or an error: stop whatever is still running, give it 10 seconds.
            if (running.Count > 0)
            {
                foreach (RunJob job in running)
                {
                    try { job.Shell.BeginStop(null, null); } catch { }
                }
                Stopwatch wait = Stopwatch.StartNew();
                while (running.Count > 0 && wait.Elapsed.TotalSeconds < 10)
                {
                    wake.WaitOne(250);
                    RunJob job;
                    while (finished.TryDequeue(out job)) DropJob(job);
                }
            }
            RunJob late;
            while (finished.TryDequeue(out late)) DropJob(late);

            bool leftRunning = running.Count > 0 || abandoned.Count > 0;
            ClosePool();

            if (log != null)
            {
                try
                {
                    log.WriteLine("# " + Now() + " END   done=" + doneCount + " failed=" + failedCount +
                                  " time=" + FormatTime(runClock.Elapsed) + (stopping ? " (stopped with Ctrl+C)" : ""));
                    log.Dispose();
                }
                catch { }
                log = null;
            }

            if (stopping) return;   // the pipeline is closing; writing anything now would throw
            try
            {
                if (leftRunning)
                    WriteWarning((running.Count + abandoned.Count) + " timed-out item(s) did not stop. Their threads stay busy until this PowerShell window closes.");
                UpdateProgress(true);
            }
            catch { }
        }

        private void ClosePool()
        {
            if (pool == null) return;
            if (running.Count > 0 || abandoned.Count > 0)
            {
                try { pool.BeginClose(null, null); } catch { }   // do not wait for stuck threads
            }
            else
            {
                try { pool.Close(); } catch { }
                pool.Dispose();
            }
            pool = null;
        }

        // Runs only when nothing is running (RunRange makes sure). Your -RefreshScript runs
        // here in your own window, so e.g. Connect-ExchangeOnline affects the whole process -
        // that is why the threads must be idle first.
        private void Refresh(int itemsLeft)
        {
            refreshCount++;
            WriteVerbose("Refresh " + refreshCount + " after " + doneCount + " items (every " + RefreshMinutes + " min).");
            if (log != null) log.WriteLine("# " + Now() + " REFRESH " + refreshCount + " done=" + doneCount);
            if (!Quiet)
            {
                ProgressRecord p = new ProgressRecord(ProgressId, "Invoke-ParallelRun",
                    "Refreshing (" + refreshCount + ")   Done " + doneCount + "/" + items.Count + "   Failed " + failedCount);
                p.PercentComplete = (int)(doneCount * 100L / items.Count);
                WriteProgress(p);
            }

            if (RefreshScript != null)
            {
                Collection<PSObject> output = null;
                try
                {
                    // Like: & $RefreshScript 2>&1   (errors come back as objects so we can show them)
                    ScriptBlock caller = ScriptBlock.Create("param($__refresh) & $__refresh 2>&1");
                    output = caller.Invoke(RefreshScript);
                }
                catch (Exception ex)
                {
                    // One attempt only. Stop here rather than carry on with a broken login.
                    if (log != null) log.WriteLine("# " + Now() + " REFRESH FAILED: " + Text(ex.Message));
                    Fail("The refresh { } failed after " + doneCount + " items, so the other " + itemsLeft +
                         " items were not started. Error: " + ex.Message,
                         "ParallelRunRefreshFailed", ErrorCategory.ConnectionError, null);
                }
                foreach (PSObject o in output)
                {
                    ErrorRecord e = o == null ? null : o.BaseObject as ErrorRecord;
                    if (e != null) WriteWarning("Refresh " + refreshCount + ": " + e);
                }
            }

            // Fresh threads: modules load again and what the old threads held is let go.
            ClosePool();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            OpenPool();
            refreshClock.Restart();
        }

        // Clean up a job we no longer report on (used while closing).
        private void DropJob(RunJob job)
        {
            try { job.Shell.EndInvoke(job.Handle); } catch { }
            running.Remove(job);
            abandoned.Remove(job);
            job.Shell.Dispose();
        }

        private void OpenLog()
        {
            if (string.IsNullOrEmpty(LogPath)) return;
            string path = GetUnresolvedProviderPathFromPSPath(LogPath);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            log = new StreamWriter(path, true, new UTF8Encoding(false));
            log.AutoFlush = true;   // so you can watch it with Get-Content -Wait
            log.WriteLine("# " + Now() + " START items=" + items.Count + " threads=" + MaxThreads +
                          " timeout=" + TimeoutSeconds + " mode=" + modeName);
            log.WriteLine("# Time\tIndex\tStatus\tDuration\tInput\tError");
        }

        private void WriteLog(ParallelRunResult r)
        {
            if (log == null) return;
            log.WriteLine(Now() + "\t" + r.Index + "\t" + r.Status + "\t" + FormatTime(r.Duration) + "\t" +
                          Text(r.Input) + "\t" + Text(r.Error));
        }

        // ------------------------------------------------------------------
        // Local threads: what each one starts with
        // ------------------------------------------------------------------

        private InitialSessionState BuildLocalSessionState(ScriptBlockAst ast)
        {
            InitialSessionState state = InitialSessionState.CreateDefault();
            List<string> modules = new List<string>();
            List<string> snapIns = new List<string>();
            List<string> functions = new List<string>();
            if (ModulesToLoad != null) modules.AddRange(ModulesToLoad);
            if (PSSnapInsToLoad != null) snapIns.AddRange(PSSnapInsToLoad);

            // Every command in the { } (and inside your functions it calls): bring what it needs.
            CollectNeeds(ast, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), state, modules, snapIns, functions);

            if (modules.Count > 0) state.ImportPSModule(modules.ToArray());
            foreach (string name in snapIns)
            {
                try
                {
                    PSSnapInException warning;
                    state.ImportPSSnapIn(name, out warning);
                }
                catch (Exception ex)
                {
                    WriteWarning("Snap-in '" + name + "' could not be added: " + ex.Message);
                }
            }

            if (CopyLocalVariables)
            {
                Dictionary<string, bool> skip = BuiltInVariableNames();
                int copied = 0;
                foreach (PSObject o in InvokeCommand.InvokeScript("Get-Variable"))
                {
                    PSVariable v = o.BaseObject as PSVariable;
                    if (v == null || skip.ContainsKey(v.Name)) continue;
                    // The same object is shared by all threads (not a copy) - fine for reading.
                    state.Variables.Add(new SessionStateVariableEntry(v.Name, v.Value, v.Description));
                    copied++;
                }
                WriteVerbose("Copied " + copied + " variable(s) to the threads.");
            }

            if (modules.Count > 0) WriteVerbose("Modules: " + string.Join(", ", modules.ToArray()));
            if (snapIns.Count > 0) WriteVerbose("Snap-ins: " + string.Join(", ", snapIns.ToArray()));
            if (functions.Count > 0) WriteVerbose("Functions copied: " + string.Join(", ", functions.ToArray()));
            return state;
        }

        private void CollectNeeds(Ast root, Dictionary<string, bool> seen, InitialSessionState state,
                                  List<string> modules, List<string> snapIns, List<string> functions)
        {
            foreach (Ast found in root.FindAll(IsCommand, true))
            {
                string name = ((CommandAst)found).GetCommandName();
                if (string.IsNullOrEmpty(name) || seen.ContainsKey(name)) continue;
                seen[name] = true;

                CommandInfo info = Resolve(name);
                if (info == null) continue;

                CmdletInfo cmdlet = info as CmdletInfo;
                if (cmdlet != null && cmdlet.PSSnapIn != null)
                {
                    // e.g. the Exchange 2010 snap-in. Built-in ones are already there.
                    if (!cmdlet.PSSnapIn.IsDefault) AddOnce(snapIns, cmdlet.PSSnapIn.Name);
                    continue;
                }

                PSModuleInfo module = info.Module;
                if (module != null)
                {
                    if (IsImplicitRemoting(module)) continue;
                    if (!module.Name.StartsWith("Microsoft.PowerShell.", StringComparison.OrdinalIgnoreCase))
                        AddOnce(modules, ModuleToLoad(module));
                    continue;
                }

                // Your own function (not from a module): copy its code, then check what it calls.
                FunctionInfo function = info as FunctionInfo;
                if (function != null)
                {
                    state.Commands.Add(new SessionStateFunctionEntry(function.Name, function.Definition));
                    functions.Add(function.Name);
                    CollectNeeds(function.ScriptBlock.Ast, seen, state, modules, snapIns, functions);
                }
            }
        }

        private static string ModuleToLoad(PSModuleInfo module)
        {
            if (!string.IsNullOrEmpty(module.ModuleBase))
            {
                string manifest = Path.Combine(module.ModuleBase, module.Name + ".psd1");
                if (File.Exists(manifest)) return manifest;
            }
            return string.IsNullOrEmpty(module.Path) ? module.Name : module.Path;
        }

        // Variables every session has, or that belong to the caller's own scope. Never copied.
        private static Dictionary<string, bool> BuiltInVariableNames()
        {
            Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in new string[] { "_", "PSItem", "args", "input", "this", "MyInvocation", "PSBoundParameters",
                                                "PSCmdlet", "PSScriptRoot", "PSCommandPath", "foreach", "switch", "Matches",
                                                "LASTEXITCODE", "StackTrace", "Error", "sender", "EventArgs", "Event",
                                                "EventSubscriber", "PSDebugContext" })
                names[n] = true;

            // Plus whatever a brand-new session already has.
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Get-Variable");
                foreach (PSObject o in ps.Invoke())
                {
                    PSVariable v = o.BaseObject as PSVariable;
                    if (v != null) names[v.Name] = true;
                }
            }
            return names;
        }

        // ------------------------------------------------------------------
        // Remote (Exchange): find the session, and turn the { } into commands
        // ------------------------------------------------------------------

        // Import-PSSession modules are marked with PrivateData = @{ ImplicitRemoting = $true }.
        private static bool IsImplicitRemoting(PSModuleInfo module)
        {
            object data = module.PrivateData;
            PSObject wrapped = data as PSObject;
            if (wrapped != null) data = wrapped.BaseObject;
            IDictionary table = data as IDictionary;
            return table != null && table.Contains("ImplicitRemoting") && LanguagePrimitives.IsTrue(table["ImplicitRemoting"]);
        }

        private PSSession FindSession(PSModuleInfo module, string commandName)
        {
            // 1. The session the Import-PSSession module keeps in its $script:PSSession.
            try
            {
                if (module.SessionState != null)
                {
                    object value = module.SessionState.PSVariable.GetValue("PSSession");
                    PSObject wrapped = value as PSObject;
                    PSSession s = (wrapped != null ? wrapped.BaseObject : value) as PSSession;
                    if (IsOpen(s)) return s;
                }
            }
            catch { }

            // 2. An open session whose computer name is in the module description.
            try
            {
                foreach (PSObject o in InvokeCommand.InvokeScript("Get-PSSession"))
                {
                    PSSession s = o.BaseObject as PSSession;
                    if (IsOpen(s) && !string.IsNullOrEmpty(module.Description) && !string.IsNullOrEmpty(s.ComputerName) &&
                        module.Description.IndexOf(s.ComputerName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return s;
                }
            }
            catch { }

            Fail("'" + commandName + "' comes from a remote session (Import-PSSession), but no open session was found for it. " +
                 "Reconnect, or pass the session with -Session.", "ParallelRunNoSession", ErrorCategory.ConnectionError, commandName);
            return null;
        }

        private static bool IsOpen(PSSession s)
        {
            return s != null && s.Runspace != null && s.Runspace.RunspaceStateInfo.State == RunspaceState.Opened;
        }

        // The { } is exactly one command, or one pipeline of commands? (Exchange sessions allow only that.)
        private static PipelineAst GetSinglePipeline(ScriptBlockAst ast)
        {
            if (ast.ParamBlock != null || ast.BeginBlock != null || ast.ProcessBlock != null || ast.DynamicParamBlock != null) return null;
            NamedBlockAst end = ast.EndBlock;
            if (end == null || end.Statements.Count != 1) return null;
            if (end.Traps != null && end.Traps.Count > 0) return null;

            PipelineAst pipe = end.Statements[0] as PipelineAst;
            if (pipe == null) return null;
            foreach (CommandBaseAst element in pipe.PipelineElements)
            {
                CommandAst cmd = element as CommandAst;
                if (cmd == null || cmd.InvocationOperator != TokenKind.Unknown || cmd.Redirections.Count > 0 ||
                    string.IsNullOrEmpty(cmd.GetCommandName()))
                    return null;
            }
            return pipe;
        }

        // Read the command(s) once: names, parameters, and how to get each value.
        private List<CommandStep> BuildSteps(PipelineAst pipe)
        {
            List<CommandStep> list = new List<CommandStep>();
            foreach (CommandBaseAst element in pipe.PipelineElements)
            {
                CommandAst cmd = (CommandAst)element;
                string typedName = cmd.GetCommandName();
                CommandInfo info = Resolve(typedName);

                CommandStep step = new CommandStep();
                step.Name = (info != null && !(info is ApplicationInfo)) ? info.Name : typedName;

                ReadOnlyCollection<CommandElementAst> parts = cmd.CommandElements;
                for (int i = 1; i < parts.Count; i++)
                {
                    ArgPart arg = new ArgPart();
                    CommandParameterAst param = parts[i] as CommandParameterAst;
                    if (param != null)
                    {
                        ParameterMetadata meta = FindParameter(info, param.ParameterName);
                        arg.ParamName = meta != null ? meta.Name : param.ParameterName;
                        bool isSwitchType = meta != null && meta.ParameterType == typeof(SwitchParameter);

                        if (param.Argument != null)
                        {
                            SetValue(arg, param.Argument);                  // -Name:value
                        }
                        else if (!isSwitchType && i + 1 < parts.Count && !(parts[i + 1] is CommandParameterAst))
                        {
                            i++;
                            SetValue(arg, parts[i]);                        // -Name value
                        }
                        else
                        {
                            arg.IsSwitch = true;                            // -Name
                        }
                    }
                    else
                    {
                        VariableExpressionAst variable = parts[i] as VariableExpressionAst;
                        if (variable != null && variable.Splatted)
                        {
                            arg.IsSplat = true;                             // @params
                            arg.Script = MakeArgScript("$" + variable.VariablePath.UserPath);
                        }
                        else
                        {
                            SetValue(arg, parts[i]);                        // positional value
                        }
                    }
                    step.Args.Add(arg);
                }
                list.Add(step);
            }
            return list;
        }

        // Fixed values are kept as they are. Anything else ($_.X, "a$b", (Get-Date)) is
        // worked out per item, on this machine, with $_ set to the item.
        private static void SetValue(ArgPart arg, Ast valueAst)
        {
            ConstantExpressionAst constant = valueAst as ConstantExpressionAst;
            if (constant != null)
            {
                arg.Constant = constant.Value;
                return;
            }
            arg.Script = MakeArgScript(valueAst.Extent.Text);
        }

        // Read the text as a command argument, exactly as PowerShell read it in the { }
        // (so Name,Size is still an array of two strings, not a command called Name).
        private static ScriptBlock MakeArgScript(string text)
        {
            return ScriptBlock.Create("Microsoft.PowerShell.Utility\\Write-Output -NoEnumerate " + text);
        }

        // Build "Get-X -A value -B | Select-Object ..." for one item.
        // Same as $ps.AddCommand('Get-X').AddParameter('A', $value).AddParameter('B') in PowerShell.
        private void AddCommands(PowerShell shell, object item)
        {
            foreach (CommandStep step in steps)
            {
                shell.AddCommand(step.Name);
                foreach (ArgPart arg in step.Args)
                {
                    if (arg.IsSwitch)
                    {
                        shell.AddParameter(arg.ParamName);
                        continue;
                    }
                    object value = arg.Script == null ? arg.Constant : Evaluate(arg.Script, item);
                    if (arg.IsSplat)
                    {
                        IDictionary named = value as IDictionary;
                        IList positional = value as IList;
                        if (named != null) shell.AddParameters(named);
                        else if (positional != null) shell.AddParameters(positional);
                    }
                    else if (arg.ParamName == null)
                    {
                        shell.AddArgument(value);
                    }
                    else
                    {
                        shell.AddParameter(arg.ParamName, value);
                    }
                }
            }
        }

        private static object Evaluate(ScriptBlock script, object item)
        {
            // New list every time: InvokeWithContext takes $_ out of the list it is given.
            List<PSVariable> vars = new List<PSVariable>();
            vars.Add(new PSVariable("_", item));
            Collection<PSObject> result = script.InvokeWithContext((Dictionary<string, ScriptBlock>)null, vars);
            if (result.Count == 0 || result[0] == null) return null;
            PSObject first = result[0];
            return first.BaseObject is PSCustomObject ? (object)first : first.BaseObject;
        }

        private static ParameterMetadata FindParameter(CommandInfo info, string name)
        {
            if (info == null) return null;
            Dictionary<string, ParameterMetadata> all;
            try { all = info.Parameters; }
            catch { return null; }
            if (all == null) return null;

            // Exact name or alias first, then a unique short form (-Ident for -Identity).
            foreach (ParameterMetadata p in all.Values)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
                foreach (string alias in p.Aliases)
                    if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            ParameterMetadata hit = null;
            int hits = 0;
            foreach (ParameterMetadata p in all.Values)
            {
                if (p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    hit = p;
                    hits++;
                }
            }
            return hits == 1 ? hit : null;
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        // Wrap the { } in process { } so $_ is the item. Left alone if it already has its own blocks.
        private string MakeScriptText(ScriptBlockAst ast)
        {
            string body = ScriptBlock.ToString();
            bool ownBlocks = ast.ParamBlock != null || ast.BeginBlock != null || ast.ProcessBlock != null ||
                             ast.DynamicParamBlock != null || (ast.EndBlock != null && !ast.EndBlock.Unnamed);
            return ownBlocks ? body : "process {\n" + body + "\n}";
        }

        private CommandInfo Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            CommandInfo info;
            try { info = InvokeCommand.GetCommand(name, CommandTypes.All); }
            catch { return null; }
            AliasInfo alias = info as AliasInfo;
            if (alias != null) info = alias.ResolvedCommand;
            return info;
        }

        private static bool IsCommand(Ast a)
        {
            return a is CommandAst;
        }

        private void Fail(string message, string id, ErrorCategory category, object target)
        {
            ThrowTerminatingError(new ErrorRecord(new InvalidOperationException(message), id, category, target));
        }

        private static void AddOnce(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value);
        }

        private static PSObject[] ToArray(Collection<PSObject> c)
        {
            PSObject[] a = new PSObject[c.Count];
            c.CopyTo(a, 0);
            return a;
        }

        private static string Text(object o)
        {
            if (o == null) return "";
            return o.ToString().Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        private static string FormatTime(TimeSpan t)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
