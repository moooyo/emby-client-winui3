param(
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$BootstrapExecutable,
    [Parameter(Mandatory)][string]$MediaDirectory,
    [Parameter(Mandatory)][string]$ArtworkDirectory,
    [ValidateRange(1024, 65535)][int]$Port = 18984,
    [ValidateSet('Dark', 'Light')][string]$Theme = 'Dark',
    [string]$RunDirectory,
    [switch]$FailFirstPlaybackInfo,
    [switch]$DesignCatalog,
    [string]$SubtitleFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnvironmentHelpers.ps1')
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [Guid]::NewGuid().ToString('D')
$artifactRoot = Join-Path $workspaceRoot 'artifacts/lumen-acceptance'
$runPath = if ($RunDirectory) { Get-LumenAbsolutePath $RunDirectory }
    else { Join-Path $artifactRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $runId.Substring(0, 8)) }
Assert-LumenRunDirectory -RunDirectory $runPath -WorkspaceRoot $workspaceRoot
if (Test-Path -LiteralPath $runPath) { throw 'Select a fresh environment run directory.' }
$fixtureSource = Get-LumenAbsolutePath $FixtureExecutable
$bootstrapSource = Get-LumenAbsolutePath $BootstrapExecutable
Assert-LumenExecutable -Executable $fixtureSource -WorkspaceRoot $workspaceRoot -ExpectedName 'EmbyClient.FixtureServer.exe'
Assert-LumenExecutable -Executable $bootstrapSource -WorkspaceRoot $workspaceRoot -ExpectedName 'EmbyClient.LumenAcceptance.exe'
$mediaPath = Get-LumenAbsolutePath $MediaDirectory
$artworkPath = Get-LumenAbsolutePath $ArtworkDirectory
if (-not (Test-Path -LiteralPath $artworkPath -PathType Container)) { throw 'The external artwork directory is unavailable.' }
$metadataPath = Join-Path $mediaPath 'fixture-h264-aac.json'
$videoPath = Join-Path $mediaPath 'fixture-h264-aac.mp4'
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.Synthetic -ne $true -or $metadata.FileLength -ne (Get-Item -LiteralPath $videoPath).Length -or
    $metadata.DurationTicks -le 0) { throw 'The media must be an existing measured synthetic fixture.' }
$subtitlePath = $null
$subtitleHash = $null
$subtitleBytes = 0
if ($SubtitleFile) {
    $subtitlePath = Get-LumenAbsolutePath $SubtitleFile
    if (-not (Test-Path -LiteralPath $subtitlePath -PathType Leaf) -or
        [IO.Path]::GetExtension($subtitlePath) -ine '.vtt') { throw 'The explicit external subtitle must be an existing .vtt file.' }
    $subtitleStream = [IO.File]::OpenRead($subtitlePath)
    try {
        if ($subtitleStream.Length -lt 8 -or $subtitleStream.Length -gt 4 * 1024 * 1024) {
            throw 'The external subtitle must be nonempty and no larger than 4 MiB.'
        }
        $subtitleData = [byte[]]::new([int]$subtitleStream.Length)
        $subtitleStream.ReadExactly($subtitleData, 0, $subtitleData.Length)
        if ($subtitleStream.ReadByte() -ne -1) { throw 'The subtitle changed beyond the bounded startup snapshot.' }
        $subtitleText = [Text.UTF8Encoding]::new($false, $true).GetString($subtitleData).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
        $subtitleHeader = $subtitleText.Split("`n", 2)[0]
        if (-not ($subtitleHeader -ceq 'WEBVTT' -or $subtitleHeader.StartsWith('WEBVTT ', [StringComparison]::Ordinal) -or
            $subtitleHeader.StartsWith("WEBVTT`t", [StringComparison]::Ordinal)) -or
            -not $subtitleText.Contains("`n`n", [StringComparison]::Ordinal) -or
            -not $subtitleText.Contains(' --> ', [StringComparison]::Ordinal)) {
            throw 'The external subtitle must be strict UTF-8 WebVTT with a header, separator, and timed cue.'
        }
        $subtitleHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($subtitleData))
        $subtitleBytes = $subtitleData.Length
    }
    finally { $subtitleStream.Dispose() }
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw 'The selected port already has a listener. No existing service was stopped.'
}
$runtimeDirectory = Join-Path $runPath 'server'
$sourceDirectory = Split-Path -Parent $fixtureSource
if (Test-LumenChildPath -Path $runtimeDirectory -Parent $sourceDirectory) {
    throw 'The copied runtime must not be a child of the source payload directory.'
}
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDirectory | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $runtimeDirectory -Recurse
}
$runtimeExecutable = Join-Path $runtimeDirectory 'EmbyClient.FixtureServer.exe'
$ownershipPath = Join-Path $runPath 'ownership.json'
$fixtureProcess = $null
try {
    $fixtureArguments = @('--port', [string]$Port, '--media-dir', ('"' + $mediaPath + '"'),
        $(if ($DesignCatalog) { '--lumen-design-catalog' } else { '--lumen-catalog' }),
        '--artwork-directory', ('"' + $artworkPath + '"'))
    if ($FailFirstPlaybackInfo) { $fixtureArguments += '--fail-first-playback-info' }
    if ($subtitlePath) { $fixtureArguments += @('--subtitle-file', ('"' + $subtitlePath + '"')) }
    $fixtureProcess = Start-Process -FilePath $runtimeExecutable -WorkingDirectory $runtimeDirectory `
        -ArgumentList $fixtureArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runPath 'server.stdout.log') -RedirectStandardError (Join-Path $runPath 'server.stderr.log')
    $ownership = [ordered]@{
        Scope = 'OwnedSyntheticLumenAcceptance'
        Synthetic = $true
        DesignCatalog = [bool]$DesignCatalog
        RunId = $runId
        WorkspaceRoot = $workspaceRoot
        RunDirectory = $runPath
        Port = $Port
        FixtureProcessId = $fixtureProcess.Id
        FixtureStartedAtUtc = $fixtureProcess.StartTime.ToUniversalTime().ToString('O')
        FixtureExecutable = $runtimeExecutable
        FixtureExecutableSha256 = (Get-FileHash -LiteralPath $runtimeExecutable -Algorithm SHA256).Hash
        SourceFixtureSha256 = (Get-FileHash -LiteralPath $fixtureSource -Algorithm SHA256).Hash
        BootstrapExecutableSha256 = (Get-FileHash -LiteralPath $bootstrapSource -Algorithm SHA256).Hash
        MediaSha256 = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash
        MetadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
        ExternalSubtitleEnabled = [bool]$subtitlePath
        SubtitleSourcePath = $subtitlePath
        SubtitleSourceSha256 = $subtitleHash
        SubtitleSourceBytes = $subtitleBytes
        SubtitleSourceUnchanged = $null
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $ownership | ConvertTo-Json | Set-Content -LiteralPath $ownershipPath -Encoding utf8
    $ready = $false
    $readinessDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $readinessDeadline) {
        $fixtureProcess.Refresh()
        if ($fixtureProcess.HasExited) { break }
        try {
            $stats = Invoke-RestMethod -Uri ("http://127.0.0.1:$Port/_fixture/stats") -TimeoutSec 1 -NoProxy
            $observedDesign = $null -ne $stats.PSObject.Properties['LumenDesignCatalog'] -and $stats.LumenDesignCatalog -eq $true
            $observedSubtitle = $null -ne $stats.PSObject.Properties['ExternalSubtitleConfigured'] -and $stats.ExternalSubtitleConfigured -eq $true
            $subtitleSnapshotMatches = -not $subtitlePath -or
                ($null -ne $stats.PSObject.Properties['ExternalSubtitleSha256'] -and $stats.ExternalSubtitleSha256 -ceq $subtitleHash)
            if ($stats.Synthetic -eq $true -and $stats.LumenCatalog -eq $true -and
                $observedDesign -eq [bool]$DesignCatalog -and $observedSubtitle -eq [bool]$subtitlePath -and
                $subtitleSnapshotMatches) { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 150
    }
    if (-not $ready) { throw 'The owned rich fixture did not become ready. Inspect this run logs.' }
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or
        $listeners[0].OwningProcess -ne $fixtureProcess.Id) {
        throw 'The observed listener does not match the owned IPv4 loopback fixture.'
    }
    $bootstrapArguments = @('--workspace-root', ('"' + $workspaceRoot + '"'), '--run-directory', ('"' + $runPath + '"'),
        '--server-url', ("http://127.0.0.1:$Port"), '--theme', $Theme)
    if ($DesignCatalog) { $bootstrapArguments += @('--design-catalog', 'true') }
    $bootstrapProcess = Start-Process -FilePath $bootstrapSource -WorkingDirectory (Split-Path -Parent $bootstrapSource) `
        -ArgumentList $bootstrapArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runPath 'bootstrap.stdout.log') -RedirectStandardError (Join-Path $runPath 'bootstrap.stderr.log')
    if (-not $bootstrapProcess.WaitForExit(90000)) {
        $bootstrapProcess.Kill()
        $bootstrapProcess.WaitForExit(5000) | Out-Null
        throw 'The owned bootstrap exceeded its outer deadline.'
    }
    if ($bootstrapProcess.ExitCode -ne 0) { throw 'The isolated profile bootstrap failed. Inspect this run sanitized logs.' }
    $profile = Get-Content -LiteralPath (Join-Path $runPath 'bootstrap.json') -Raw | ConvertFrom-Json
    if ($profile.ProductionRestoreVerified -ne $true) { throw 'The normal saved-account restore was not verified.' }
    if ($subtitlePath) {
        $ownership.SubtitleSourceUnchanged = (Get-FileHash -LiteralPath $subtitlePath -Algorithm SHA256).Hash -ceq $subtitleHash
        if (-not $ownership.SubtitleSourceUnchanged) { throw 'The explicit subtitle source changed during environment startup.' }
    }
    else { $ownership.SubtitleSourceUnchanged = $null }
    $ownership | ConvertTo-Json | Set-Content -LiteralPath $ownershipPath -Encoding utf8
    [pscustomobject]@{
        Ready = $true
        Synthetic = $true
        ServerUrl = "http://127.0.0.1:$Port"
        RunDirectory = $runPath
        ProfileDirectory = $profile.ProfileDirectory
        FixtureProcessId = $fixtureProcess.Id
        OwnershipReceipt = $ownershipPath
        ExternalSubtitleEnabled = [bool]$subtitlePath
        SubtitleSourceSha256 = $subtitleHash
    }
}
catch {
    if (Test-Path -LiteralPath $ownershipPath) {
        try { & (Join-Path $PSScriptRoot 'Stop-Environment.ps1') -RunDirectory $runPath | Out-Null }
        catch { Write-Warning 'Owned fixture cleanup was not confirmed. Retain and inspect the ownership receipt.' }
    }
    elseif ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        $fixtureProcess.Kill()
        $fixtureProcess.WaitForExit(5000) | Out-Null
    }
    throw
}
