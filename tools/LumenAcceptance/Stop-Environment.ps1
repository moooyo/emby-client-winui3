param([Parameter(Mandatory)][string]$RunDirectory, [switch]$ValidateOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnvironmentHelpers.ps1')
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runPath = Get-LumenAbsolutePath $RunDirectory
Assert-LumenRunDirectory -RunDirectory $runPath -WorkspaceRoot $workspaceRoot
$ownershipPath = Join-Path $runPath 'ownership.json'
$ownership = Get-Content -LiteralPath $ownershipPath -Raw | ConvertFrom-Json
$expectedExecutable = Join-Path $runPath 'server/EmbyClient.FixtureServer.exe'
$parsedRunId = [Guid]::Empty
if ($ownership.Scope -ne 'OwnedSyntheticLumenAcceptance' -or $ownership.Synthetic -ne $true -or
    $ownership.WorkspaceRoot -ne $workspaceRoot -or $ownership.RunDirectory -ne $runPath -or
    $ownership.FixtureExecutable -ne $expectedExecutable -or $ownership.Port -lt 1024 -or $ownership.Port -gt 65535 -or
    -not [Guid]::TryParse([string]$ownership.RunId, [ref]$parsedRunId)) {
    throw 'The fixture ownership receipt does not match this run. No process was stopped.'
}
$process = Get-LumenOwnedProcess $ownership
if ($ValidateOnly) {
    [pscustomobject]@{ Validated = $true; Running = ($null -ne $process); RunDirectory = $runPath; Port = $ownership.Port }
    return
}
if ($null -ne $process) {
    Stop-Process -InputObject $process -ErrorAction Stop
    if (-not $process.WaitForExit(10000)) { throw 'The owned fixture did not exit within the cleanup deadline.' }
}
$listeners = @(Get-NetTCPConnection -LocalPort ([int]$ownership.Port) -State Listen -ErrorAction SilentlyContinue)
$cleanup = [ordered]@{
    Scope = 'OwnedSyntheticLumenAcceptance'
    RunId = $ownership.RunId
    CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    OwnedFixtureStopped = $true
    PortReleased = ($listeners.Count -eq 0)
    EvidenceRetained = $true
}
$cleanup | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'cleanup.json') -Encoding utf8
if ($listeners.Count -ne 0) { throw 'The recorded port now has a listener. No other process was stopped.' }
[pscustomobject]$cleanup
