param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [string]$RunDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnvironmentHelpers.ps1')
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceDirectory = Get-LumenAbsolutePath $PublishDirectory
$profileRoot = Get-LumenAbsolutePath $ProfileDirectory
$runId = [Guid]::NewGuid().ToString('D')
$artifactRoot = Join-Path $workspaceRoot 'artifacts/lumen-acceptance'
$runPath = if ($RunDirectory) { Get-LumenAbsolutePath $RunDirectory }
    else { Join-Path $artifactRoot ('app-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $runId.Substring(0, 8)) }
Assert-LumenRunDirectory -RunDirectory $runPath -WorkspaceRoot $workspaceRoot
Assert-LumenRunDirectory -RunDirectory $profileRoot -WorkspaceRoot $workspaceRoot
if (Test-Path -LiteralPath $runPath) { throw 'Select a fresh frozen application run directory.' }
if (-not (Test-LumenChildPath -Path $sourceDirectory -Parent $workspaceRoot) -or
    -not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw 'The application payload must have been built inside this workspace.'
}
Assert-LumenExecutable -Executable (Join-Path $sourceDirectory 'EmbyClient.App.exe') `
    -WorkspaceRoot $workspaceRoot -ExpectedName 'EmbyClient.App.exe'
$hasApplicationResources = (Test-Path -LiteralPath (Join-Path $sourceDirectory 'resources.pri') -PathType Leaf) -or
    (Test-Path -LiteralPath (Join-Path $sourceDirectory 'EmbyClient.App.pri') -PathType Leaf)
if (-not $hasApplicationResources -or
    -not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'Microsoft.ui.xaml.dll') -PathType Leaf)) {
    throw 'Supply the complete self-contained WinUI application payload.'
}
$bootstrapPath = Join-Path (Split-Path -Parent $profileRoot) 'bootstrap.json'
if (-not (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $profileRoot 'settings.json') -PathType Leaf)) {
    throw 'Supply a previously prepared task-owned acceptance profile.'
}
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw | ConvertFrom-Json
if ($bootstrap.ProductionRestoreVerified -ne $true -or $bootstrap.ProfileDirectory -ne $profileRoot) {
    throw 'The profile does not match its production-restore receipt.'
}
$payloadDirectory = Join-Path $runPath 'candidate'
if (Test-LumenChildPath -Path $payloadDirectory -Parent $sourceDirectory) {
    throw 'The frozen candidate directory cannot be inside its source payload.'
}
$sourceItems = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Force)
if (@($sourceItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0 -or
    ((Get-Item -LiteralPath $sourceDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'A frozen application payload cannot contain reparse points.'
}
$sourceFiles = @($sourceItems | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
$inputs = @($sourceFiles | ForEach-Object {
    [ordered]@{ File = [IO.Path]::GetRelativePath($sourceDirectory, $_.FullName); Length = $_.Length;
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
foreach ($inputFile in $inputs) {
    $destination = Join-Path $payloadDirectory $inputFile.File
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $inputFile.File) -Destination $destination
}
$afterFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Force -File)
if ($afterFiles.Count -ne $inputs.Count) { throw 'The source payload changed while freezing the application.' }
foreach ($inputFile in $inputs) {
    $source = Join-Path $sourceDirectory $inputFile.File
    $destination = Join-Path $payloadDirectory $inputFile.File
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $inputFile.Sha256 -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $inputFile.Sha256) {
        throw 'The source payload changed or the frozen copy differs. No application was launched.'
    }
}
$snapshot = [ordered]@{
    Scope = 'Frozen task-owned application output; not native acceptance by itself.'
    RunId = $runId
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    SourceDirectory = $sourceDirectory
    CandidateDirectory = $payloadDirectory
    Files = $inputs
}
$snapshotPath = Join-Path $runPath 'app-inputs.json'
$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $snapshotPath -Encoding utf8
$executable = Join-Path $payloadDirectory 'EmbyClient.App.exe'
$executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
if (-not (Get-Command Start-Process).Parameters.ContainsKey('Environment')) {
    throw 'This helper requires PowerShell 7.4 or later for a child-only environment override.'
}
# Start-Process overrides only this child; global environment and ordinary saved accounts are untouched.
$childEnvironment = @{ EMBY_CLIENT_DATA_ROOT = $profileRoot }
$process = Start-Process -FilePath $executable -WorkingDirectory $payloadDirectory -WindowStyle Hidden `
    -Environment $childEnvironment -PassThru
$ownership = [ordered]@{
    Scope = 'OwnedLumenAcceptanceApplication'
    RunId = $runId
    WorkspaceRoot = $workspaceRoot
    RunDirectory = $runPath
    ProfileDirectory = $profileRoot
    ProcessId = $process.Id
    StartedAtUtc = $process.StartTime.ToUniversalTime().ToString('O')
    StartedUtcTicks = $process.StartTime.ToUniversalTime().Ticks
    Executable = $executable
    ExecutableSha256 = $executableHash
    CandidateDirectory = $payloadDirectory
    SnapshotReceipt = $snapshotPath
    DesktopActionsPerformed = $false
}
try {
    $ownership | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runPath 'app-ownership.json') -Encoding utf8
}
catch {
    Write-Warning ("Owned app PID {0} started from its frozen candidate; ownership receipt could not be written. No process was stopped." -f $process.Id)
    [pscustomobject]$ownership
    throw
}
$process.Refresh()
if ($process.HasExited) { throw 'The frozen application exited before handoff. Its evidence was retained.' }
[pscustomobject]$ownership
