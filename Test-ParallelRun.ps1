#Requires -Version 5.1
# Tests for Invoke-ParallelRun (ParallelRun.dll). Run it in a NEW window:
#   powershell -ExecutionPolicy Bypass -File .\Test-ParallelRun.ps1
#   powershell -ExecutionPolicy Bypass -File .\Test-ParallelRun.ps1 -SkipSlow
#
# It builds the DLL into a throwaway folder under $env:TEMP and loads it from
# there, so the DLL in this folder is never locked. Local only - nothing here
# touches Exchange or another server. Needs FullLanguage mode.
# -SkipSlow skips T20 (a stuck item that takes ~35 seconds to give up on).
param([switch]$SkipSlow)

$script:fail = 0
function Check($name, $got, $want) {
    if ("$got" -eq "$want") { Write-Host "  PASS  $name" }
    else { Write-Host "  FAIL  $name  got=[$got] want=[$want]"; $script:fail++ }
}

# first output of each result, in input order
function Out1($results) { ($results | Sort-Object Index | ForEach-Object { $_.Output[0] }) -join ',' }

$sandbox = Join-Path $env:TEMP ("pr_test_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputFolder $sandbox | Out-Null
Import-Module (Join-Path $sandbox 'ParallelRun.dll')

Write-Host 'T1 every item comes back once, with its output'
$r = 1..20 | Invoke-ParallelRun { $_ * 2 } -Quiet
Check 'count'       $r.Count 20
Check 'all OK'      @($r | Where-Object Status -eq 'OK').Count 20
Check 'sum'         ($r.Output | Measure-Object -Sum).Sum 420
Check 'index 1..20' (($r | Sort-Object Index | ForEach-Object Index) -join ',') ((1..20) -join ',')
Check 'input kept'  ($r | Where-Object Index -eq 7).Input 7
Check 'type'        $r[0].GetType().Name 'ParallelRunResult'

Write-Host 'T2 -MaxThreads is the real limit'
$sync = [hashtable]::Synchronized(@{ Now = 0; Max = 0 })
$null = 1..12 | Invoke-ParallelRun {
    [System.Threading.Monitor]::Enter($sync.SyncRoot)
    try { $sync.Now++; if ($sync.Now -gt $sync.Max) { $sync.Max = $sync.Now } }
    finally { [System.Threading.Monitor]::Exit($sync.SyncRoot) }
    Start-Sleep -Milliseconds 300
    [System.Threading.Monitor]::Enter($sync.SyncRoot)
    try { $sync.Now-- } finally { [System.Threading.Monitor]::Exit($sync.SyncRoot) }
} -MaxThreads 3 -CopyLocalVariables -Force -Quiet
Check 'max at once = 3' $sync.Max 3

Write-Host 'T3 -MaxThreads 1 runs one at a time'
$sync.Max = 0; $sync.Now = 0
$null = 1..4 | Invoke-ParallelRun {
    [System.Threading.Monitor]::Enter($sync.SyncRoot)
    try { $sync.Now++; if ($sync.Now -gt $sync.Max) { $sync.Max = $sync.Now } }
    finally { [System.Threading.Monitor]::Exit($sync.SyncRoot) }
    Start-Sleep -Milliseconds 100
    [System.Threading.Monitor]::Enter($sync.SyncRoot)
    try { $sync.Now-- } finally { [System.Threading.Monitor]::Exit($sync.SyncRoot) }
} -MaxThreads 1 -CopyLocalVariables -Force -Quiet
Check 'max at once = 1' $sync.Max 1

Write-Host 'T4 a throw only fails its own item'
$r = 1..6 | Invoke-ParallelRun { if ($_ -eq 4) { throw "boom $_" }; $_ } -Force -Quiet
$bad = @($r | Where-Object Status -eq 'FAILED')
Check 'one failed'  $bad.Count 1
Check 'which'       $bad[0].Input 4
Check 'message'     $bad[0].Error 'boom 4'
Check 'others OK'   @($r | Where-Object Status -eq 'OK').Count 5
Check 'record kept' ($bad[0].ErrorRecords.Count -ge 1) True

Write-Host 'T5 a non-terminating error also marks the item FAILED'
$missing = Join-Path $sandbox 'nope.txt'
$r = $missing, $sandbox | Invoke-ParallelRun { Get-Item -LiteralPath $_ } -Force -Quiet
Check 'missing FAILED' ($r | Where-Object Index -eq 1).Status 'FAILED'
Check 'error text'     (($r | Where-Object Index -eq 1).Error -like '*Cannot find path*') True
Check 'folder OK'      ($r | Where-Object Index -eq 2).Status 'OK'

Write-Host 'T6 -TimeoutSeconds stops a slow item and marks it FAILED'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = 1, 2 | Invoke-ParallelRun { if ($_ -eq 2) { Start-Sleep -Seconds 20 }; $_ } -TimeoutSeconds 2 -Force -Quiet
$sw.Stop()
Check 'slow FAILED'      ($r | Where-Object Index -eq 2).Status 'FAILED'
Check 'timeout text'     ($r | Where-Object Index -eq 2).Error 'Timed out after 2 seconds.'
Check 'fast OK'          ($r | Where-Object Index -eq 1).Status 'OK'
Check 'did not wait 20s' ($sw.Elapsed.TotalSeconds -lt 8) True

Write-Host 'T7 item 1 runs alone first; if it fails the rest never start'
$ran = [hashtable]::Synchronized(@{ Count = 0 })
$err = $null
try {
    $null = 1..5 | Invoke-ParallelRun {
        [System.Threading.Monitor]::Enter($ran.SyncRoot)
        try { $ran.Count++ } finally { [System.Threading.Monitor]::Exit($ran.SyncRoot) }
        if ($_ -eq 1) { throw 'first is bad' }
    } -CopyLocalVariables -Quiet
} catch { $err = $_ }
Check 'stopped with error' ($null -ne $err) True
Check 'says why'           ("$err" -like '*Item 1 failed*first is bad*') True
Check 'rest never ran'     $ran.Count 1
$ran.Count = 0
$r = 1..5 | Invoke-ParallelRun {
    [System.Threading.Monitor]::Enter($ran.SyncRoot)
    try { $ran.Count++ } finally { [System.Threading.Monitor]::Exit($ran.SyncRoot) }
    if ($_ -eq 1) { throw 'first is bad' }
} -CopyLocalVariables -Force -Quiet
Check '-Force runs all' $ran.Count 5
Check '-Force 1 FAILED' @($r | Where-Object Status -eq 'FAILED').Count 1

Write-Host 'T8 -RawOutput: plain output, failures are real PowerShell errors'
$ev = $null
$out = 1..5 | Invoke-ParallelRun { if ($_ -eq 3) { throw 'raw boom' }; $_ * 10 } -RawOutput -Force -Quiet -ErrorVariable ev -ErrorAction SilentlyContinue
Check 'flat output'  ((@($out) | Sort-Object) -join ',') '10,20,40,50'
Check 'one error'    @($ev).Count 1
Check 'error target' $ev[0].TargetObject 3
Check 'error text'   ("$($ev[0])" -like 'Item 3 (3) FAILED: raw boom') True

Write-Host 'T9 your own functions are copied, also the ones they call'
function Get-Double { param($n) $n * 2 }
function Get-Quad { param($n) Get-Double (Get-Double $n) }
$r = 1..4 | Invoke-ParallelRun { Get-Quad $_ } -Force -Quiet
Check 'nested functions' (Out1 $r) '4,8,12,16'

Write-Host 'T10 variables: only with -CopyLocalVariables'
$secret = 'abc'
$r = 1 | Invoke-ParallelRun { "[$secret]" } -Quiet
Check 'not copied by default' $r.Output[0] '[]'
$r = 1 | Invoke-ParallelRun { "[$secret]" } -CopyLocalVariables -Quiet
Check 'copied' $r.Output[0] '[abc]'

Write-Host 'T11 -LogPath writes one line per item'
$logFile = Join-Path $sandbox 'logs\run.log'
$null = 1..3 | Invoke-ParallelRun { if ($_ -eq 2) { throw 'x' }; $_ } -LogPath $logFile -Force -Quiet
$lines = Get-Content -LiteralPath $logFile
Check 'item lines'  @($lines | Where-Object { $_ -notlike '#*' }).Count 3
Check 'one FAILED'  @($lines | Where-Object { $_ -match "`tFAILED`t" }).Count 1
Check 'start + end' @($lines | Where-Object { $_ -match '^# .* (START|END) ' }).Count 2

Write-Host 'T12 -InputObject without the pipe'
$r = Invoke-ParallelRun -InputObject @(5, 6, 7) { $_ + 1 } -Quiet
Check 'all items' (Out1 $r) '6,7,8'

Write-Host 'T13 an array item stays one item'
$pairs = @(@(1, 2, 3), @(4, 5))
$r = $pairs | Invoke-ParallelRun { $_.Count } -Quiet
Check 'not unrolled' (Out1 $r) '3,2'

Write-Host 'T14 objects keep their properties'
$objs = 1..3 | ForEach-Object { [pscustomobject]@{ Name = "u$_"; Size = $_ } }
$r = $objs | Invoke-ParallelRun { $_.Name + ':' + $_.Size } -Quiet
Check 'properties' (Out1 $r) 'u1:1,u2:2,u3:3'

Write-Host 'T15 command mode (the Exchange way, tested on local threads)'
$files = 1..3 | ForEach-Object { $p = Join-Path $sandbox "f$_.txt"; Set-Content -LiteralPath $p -Value ('x' * $_) -NoNewline; $p }
$r = $files | Invoke-ParallelRun -TestCommandMode { Get-Item -LiteralPath $_ | Select-Object -Property Name,Length } -Quiet
Check 'pipeline of 2'  (($r | Sort-Object Index | ForEach-Object { $_.Output[0].Name }) -join ',') 'f1.txt,f2.txt,f3.txt'
Check 'a,b array'      (($r | Sort-Object Index | ForEach-Object { $_.Output[0].Length }) -join ',') '1,2,3'
$sel = @{ Property = 'Name' }
$r = $files | Invoke-ParallelRun -TestCommandMode { Get-Item -LiteralPath $_ | Select-Object @sel } -Quiet
Check 'splat'          (($r | Sort-Object Index | ForEach-Object { $_.Output[0].Name }) -join ',') 'f1.txt,f2.txt,f3.txt'
$suffix = '!'
$r = 1..3 | Invoke-ParallelRun -TestCommandMode { Write-Output -InputObject ("$_" + $suffix) } -Quiet
Check 'local variable' (Out1 $r) '1!,2!,3!'
$r = 1 | Invoke-ParallelRun -TestCommandMode { Get-ChildItem $sandbox -Filter 'f*.txt' -File } -Quiet
Check 'positional + switch at end' $r.Output.Count 3
$r = 1 | Invoke-ParallelRun -TestCommandMode { Get-ChildItem -File -Path $sandbox -Filter 'f*.txt' } -Quiet
Check 'switch before a param' $r.Output.Count 3
$r = 'f2.txt' | Invoke-ParallelRun -TestCommandMode { Get-ChildItem -LiteralPath $sandbox -Filter $_ } -Quiet
Check '$_ as value' $r.Output[0].Name 'f2.txt'

Write-Host 'T16 no input = no output, no error'
$r = @() | Invoke-ParallelRun { $_ } -Quiet
Check 'nothing' @($r).Count 0

Write-Host 'T17 progress bar on (no -Quiet) does not break anything'
$r = 1..6 | Invoke-ParallelRun { Start-Sleep -Milliseconds 200; $_ } -MaxThreads 2
Check 'count' @($r).Count 6

Write-Host 'T18 Duration / StartTime / EndTime'
$r = 1 | Invoke-ParallelRun { Start-Sleep -Milliseconds 300 } -Quiet
Check 'duration >= 300ms'   ($r.Duration.TotalMilliseconds -ge 290) True
Check 'end after start'     ($r.EndTime -gt $r.StartTime) True

Write-Host 'T19 $null as an item'
$r = $null | Invoke-ParallelRun { "[$_]" } -Quiet
Check 'null item OK'  $r.Status 'OK'
Check 'null item out' $r.Output[0] '[]'

Write-Host 'T21 a variable set by one item is not seen by the next item on the same thread'
$r = 1..4 | Invoke-ParallelRun { $before = $last; $last = $_; "[$before]" } -MaxThreads 1 -Force -Quiet
Check 'no leak' (Out1 $r) '[],[],[],[]'
$shared = [hashtable]::Synchronized(@{ Hits = 0 })
$null = 1..4 | Invoke-ParallelRun { $shared.Hits++ } -MaxThreads 1 -CopyLocalVariables -Force -Quiet
Check 'copied variables still work' $shared.Hits 4

Write-Host 'T22 -RefreshMinutes: refresh runs while nothing else is running, every item still done'
$busy = [hashtable]::Synchronized(@{ Now = 0 })
$seen = [hashtable]::Synchronized(@{ Refreshes = 0; BusyDuring = 0 })
$logFile2 = Join-Path $sandbox 'refresh.log'
$r = 1..40 | Invoke-ParallelRun {
    [System.Threading.Monitor]::Enter($busy.SyncRoot)
    try { $busy.Now++ } finally { [System.Threading.Monitor]::Exit($busy.SyncRoot) }
    Start-Sleep -Milliseconds 250
    [System.Threading.Monitor]::Enter($busy.SyncRoot)
    try { $busy.Now-- } finally { [System.Threading.Monitor]::Exit($busy.SyncRoot) }
    $_
} -MaxThreads 3 -CopyLocalVariables -Force -Quiet -RefreshMinutes 0.02 -LogPath $logFile2 -RefreshScript {
    $seen.Refreshes++
    if ($busy.Now -gt $seen.BusyDuring) { $seen.BusyDuring = $busy.Now }
}
Check 'all 40 OK'            @($r | Where-Object Status -eq 'OK').Count 40
Check 'refresh ran'          ($seen.Refreshes -ge 1) True
Check 'nothing ran meanwhile' $seen.BusyDuring 0
Check 'log has REFRESH'      (@(Get-Content -LiteralPath $logFile2 | Where-Object { $_ -match '^# .* REFRESH \d' }).Count -eq $seen.Refreshes) True

Write-Host 'T23 a refresh that throws stops the run (no retry)'
$err = $null
try {
    $null = 1..40 | Invoke-ParallelRun { Start-Sleep -Milliseconds 200 } -MaxThreads 2 -Force -Quiet -RefreshMinutes 0.02 -RefreshScript { throw 'login failed' }
} catch { $err = $_ }
Check 'stopped'  ($null -ne $err) True
Check 'says why' ("$err" -like '*refresh*not started*login failed*') True

Write-Host 'T24 a refresh error that does not throw = warning, run goes on; no script = threads rebuilt only'
$wv = $null
$r = 1..20 | Invoke-ParallelRun { Start-Sleep -Milliseconds 200; $_ } -MaxThreads 2 -Force -Quiet -RefreshMinutes 0.02 `
        -RefreshScript { Write-Error 'soft problem' } -WarningVariable wv -WarningAction SilentlyContinue
Check 'all 20 OK'    @($r | Where-Object Status -eq 'OK').Count 20
Check 'warning shown' (@($wv | Where-Object { "$_" -like 'Refresh *soft problem*' }).Count -ge 1) True
$r = 1..20 | Invoke-ParallelRun { Start-Sleep -Milliseconds 200; $_ } -MaxThreads 2 -Force -Quiet -RefreshMinutes 0.02
Check 'minutes only, all OK' @($r | Where-Object Status -eq 'OK').Count 20

if (-not $SkipSlow) {
    Write-Host 'T20 a stuck item that ignores Stop is given up after the timeout + 30s (slow)'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $wv = $null
    $r = 1 | Invoke-ParallelRun { [System.Threading.Thread]::Sleep(60000) } -TimeoutSeconds 1 -Quiet -WarningVariable wv -WarningAction SilentlyContinue
    $sw.Stop()
    Check 'FAILED'        $r.Status 'FAILED'
    Check 'says so'       ($r.Error -like '*did not stop*') True
    Check 'about 31s'     ($sw.Elapsed.TotalSeconds -ge 30 -and $sw.Elapsed.TotalSeconds -lt 45) True
    Check 'warning'       @($wv).Count 1
}

# the loaded DLL stays locked until this window closes; the rest is removed
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
if ($script:fail -eq 0) { Write-Host 'ALL PASSED' -ForegroundColor Green }
else { Write-Host "$script:fail FAILED" -ForegroundColor Red; exit 1 }
