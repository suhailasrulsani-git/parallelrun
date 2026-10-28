#Requires -Version 5.1
# Long run of Invoke-ParallelRun, to see if memory / CPU / threads grow over time.
#   powershell -ExecutionPolicy Bypass -File .\Test-ParallelRunSoak.ps1                  # about 15 min
#   powershell -ExecutionPolicy Bypass -File .\Test-ParallelRunSoak.ps1 -Items 1000      # quick look
#
# Builds the DLL into a folder under $env:TEMP, starts the run in a separate
# powershell.exe and reads that process every -SampleSeconds: memory, CPU,
# threads, handles. Local only. Every 100th item throws, every 250th writes an
# error, every 997th sleeps past the 5 s timeout - so the FAILED and timeout
# paths run too. Results go straight to a CSV (not kept in memory).
# Files left in the TEMP folder it prints: samples.csv, items.csv, run.log.
param(
    [int]$Items = 12000,
    [int]$MaxThreads = 4,
    [int]$SampleSeconds = 30
)

$sandbox = Join-Path $env:TEMP ("pr_soak_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputFolder $sandbox | Out-Null

$dll      = Join-Path $sandbox 'ParallelRun.dll'
$runner   = Join-Path $sandbox 'run.ps1'
$itemsCsv = Join-Path $sandbox 'items.csv'
$logFile  = Join-Path $sandbox 'run.log'
$samplesCsv = Join-Path $sandbox 'samples.csv'

@'
param($Dll, $Items, $MaxThreads, $Out, $Log)
Import-Module $Dll
1..$Items | Invoke-ParallelRun {
    $data = foreach ($n in 1..500) { [pscustomobject]@{ N = $n; S = "x$n" } }
    if ($_ % 100 -eq 0) { throw "planned failure $_" }
    if ($_ % 250 -eq 0) { Write-Error "planned error $_" }
    if ($_ % 997 -eq 0) { Start-Sleep -Seconds 10 }
    Start-Sleep -Milliseconds 200
    [pscustomobject]@{ Item = $_; Rows = $data.Count }
} -MaxThreads $MaxThreads -TimeoutSeconds 5 -Force -Quiet -LogPath $Log |
    Select-Object Index, Status, Duration, Error |
    Export-Csv -LiteralPath $Out -NoTypeInformation
'@ | Set-Content -LiteralPath $runner -Encoding UTF8

$argList = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dll "{1}" -Items {2} -MaxThreads {3} -Out "{4}" -Log "{5}"' -f `
    $runner, $dll, $Items, $MaxThreads, $itemsCsv, $logFile
$proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -PassThru -WindowStyle Hidden

Write-Host "Soak run: $Items items, MaxThreads $MaxThreads, PID $($proc.Id). Folder: $sandbox"
$cores = [Environment]::ProcessorCount
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$lastCpu = 0
$lastTime = 0
$samples = New-Object System.Collections.Generic.List[object]

while (-not $proc.HasExited) {
    Start-Sleep -Seconds $SampleSeconds
    try {
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $cpu = $proc.TotalProcessorTime.TotalSeconds
        $now = $clock.Elapsed.TotalSeconds
        $done = 0
        if (Test-Path -LiteralPath $logFile) { $done = @(Get-Content -LiteralPath $logFile | Where-Object { $_ -notlike '#*' }).Count }
        $s = [pscustomobject]@{
            Minute    = [math]::Round($now / 60, 1)
            Done      = $done
            PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
            WorkSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
            Threads   = $proc.Threads.Count
            Handles   = $proc.HandleCount
            CpuPct    = [math]::Round(($cpu - $lastCpu) / ($now - $lastTime) / $cores * 100, 1)
        }
        $lastCpu = $cpu
        $lastTime = $now
        $samples.Add($s)
        Write-Host ("  {0,5} min  done {1,6}  private {2,7} MB  threads {3,3}  handles {4,5}  cpu {5,5}%" -f `
            $s.Minute, $s.Done, $s.PrivateMB, $s.Threads, $s.Handles, $s.CpuPct)
    } catch { }
}
$clock.Stop()
$samples | Export-Csv -LiteralPath $samplesCsv -NoTypeInformation

Write-Host ''
Write-Host ("Run time: {0:hh\:mm\:ss}  Exit code: {1}" -f $clock.Elapsed, $proc.ExitCode)
if (Test-Path -LiteralPath $itemsCsv) {
    $rows = Import-Csv -LiteralPath $itemsCsv
    $timedOut = @($rows | Where-Object { $_.Error -like 'Timed out*' }).Count
    Write-Host ("Items: {0}  OK: {1}  FAILED: {2}  (timed out: {3})" -f $rows.Count,
        @($rows | Where-Object Status -eq 'OK').Count, @($rows | Where-Object Status -eq 'FAILED').Count, $timedOut)
}
if ($samples.Count -ge 3) {
    # skip the first sample (warm-up); compare the next one with the last one
    $first = $samples[1]
    $last  = $samples[$samples.Count - 1]
    $max   = ($samples | Measure-Object PrivateMB -Maximum).Maximum
    Write-Host ("Private MB: {0} -> {1} (max {2})   Threads: {3} -> {4}   Handles: {5} -> {6}" -f `
        $first.PrivateMB, $last.PrivateMB, $max, $first.Threads, $last.Threads, $first.Handles, $last.Handles)
}
Write-Host "Files: $sandbox"
