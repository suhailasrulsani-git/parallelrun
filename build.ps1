<#
    build.ps1

    Compiles ParallelRun.cs into ParallelRun.dll. Nothing to install - it uses
    Add-Type, which is in every Windows PowerShell 5.1.

        .\build.ps1                          # DLL next to this file
        .\build.ps1 -OutputFolder D:\tools   # DLL somewhere else

    Run it in a NEW PowerShell window. Once a window has loaded the DLL, the
    file is locked until that window closes, so it cannot be rebuilt in place.
    Needs FullLanguage mode (Add-Type is blocked in Constrained Language Mode).
#>
[CmdletBinding()]
param(
    [string]$OutputFolder = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
    throw "This window is in $($ExecutionContext.SessionState.LanguageMode) mode. Add-Type needs FullLanguage - build on a machine in FullLanguage mode."
}

$source = Join-Path $PSScriptRoot 'ParallelRun.cs'
$dll    = Join-Path $OutputFolder 'ParallelRun.dll'

if (-not (Test-Path -LiteralPath $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
}

# Fails with "used by another process" if some window still has it loaded.
if (Test-Path -LiteralPath $dll) {
    Remove-Item -LiteralPath $dll -Force
}

Add-Type -Path $source -OutputAssembly $dll -OutputType Library

Write-Host "Built: $dll" -ForegroundColor Green
Write-Host "Load it with: Import-Module '$dll'"
