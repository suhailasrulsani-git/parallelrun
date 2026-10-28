# ParallelRun

`Invoke-ParallelRun` runs a script block for many items at the same time, in Windows PowerShell 5.1.

It's a single C# file that you compile on your own machine with `Add-Type`. There's nothing to install and no internet needed.

```powershell
1..100 | Invoke-ParallelRun { Test-Connection $_ -Count 1 -Quiet } -MaxThreads 8
```

## Why

- `-MaxThreads` is always the real limit, including for Exchange remote commands.
- You get one result object per item: `Index`, `Input`, `Status` (OK / FAILED), `Output`, `Error`, `Duration`.
- `-TimeoutSeconds` stops a stuck item and marks it FAILED.
- A progress bar shows done / total, failed, running, elapsed time and ETA.
- `-RefreshMinutes` / `-RefreshScript` pause every N minutes so you can reconnect (for example Exchange Online), then carry on.
- Item 1 runs alone first. If it fails, the rest are not started (add `-Force` to skip this check).

The idea is the same as santhse's Invoke-All. ParallelRun was written from scratch.

## Requirements

- Windows PowerShell 5.1
- FullLanguage mode (`Add-Type` is blocked in Constrained Language Mode)

## Build and load

Run this in a **new** PowerShell window. Once a window loads the DLL, the file stays locked until that window closes.

```powershell
.\build.ps1                          # ParallelRun.dll next to this file
.\build.ps1 -OutputFolder D:\tools   # or somewhere else

Import-Module .\ParallelRun.dll
```

## Examples

```powershell
# Result objects (default)
$r = Get-Content .\servers.txt | Invoke-ParallelRun { Test-Connection $_ -Count 1 -Quiet } -MaxThreads 10
$r | Where-Object Status -eq 'FAILED'

# Stop items that take longer than 60 seconds
$items | Invoke-ParallelRun { Get-Something $_ } -TimeoutSeconds 60

# Plain output, failed items become normal PowerShell errors
$items | Invoke-ParallelRun { Get-Something $_ } -RawOutput -ErrorVariable errs

# Exchange (Import-PSSession): the session is found automatically
$mbxs | Invoke-ParallelRun { Get-MailboxStatistics -Identity $_.PrimarySmtpAddress } -MaxThreads 5

# Long run: reconnect every 2 hours
$mbxs | Invoke-ParallelRun { Get-EXOMailboxStatistics -Identity $_.UserPrincipalName } `
    -RefreshMinutes 120 -RefreshScript { Connect-ExchangeOnline -ShowBanner:$false }
```

## Parameters

| Parameter | What it does |
|---|---|
| `-ScriptBlock` | The `{ }` to run for each item. Use `$_` for the item. |
| `-InputObject` | The items. Usually piped in. |
| `-MaxThreads` | How many run at once. Default = number of CPU cores (max 128). |
| `-TimeoutSeconds` | Stop an item after this many seconds and mark it FAILED. 0 = no limit. |
| `-RawOutput` | Plain output instead of result objects. |
| `-Force` | Skip the "run item 1 alone first" check. |
| `-Quiet` | No progress bar. |
| `-LogPath` | Optional log file, one tab-separated line per item. |
| `-ModulesToLoad` / `-PSSnapInsToLoad` | Load these in every thread (local runs only). |
| `-CopyLocalVariables` | Make your variables visible inside the `{ }` (local runs only). |
| `-Session` | Run on this remote session's server. |
| `-RefreshMinutes` | Every N minutes: let running items finish, run `-RefreshScript`, start fresh threads. |
| `-RefreshScript` | What to run at each refresh. Runs once, in your own window. |

Failed items are never retried.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File .\Test-ParallelRun.ps1             # all checks
powershell -ExecutionPolicy Bypass -File .\Test-ParallelRun.ps1 -SkipSlow   # skip the 35 s timeout test
powershell -ExecutionPolicy Bypass -File .\Test-ParallelRunSoak.ps1         # ~15 min memory / CPU check
```

Both test scripts build the DLL into a temp folder, so the DLL here is never locked. They run locally only.
