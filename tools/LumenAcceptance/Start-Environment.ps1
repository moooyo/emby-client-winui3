param(
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$BootstrapExecutable,
    [Parameter(Mandatory)][string]$MediaDirectory,
    [string]$ArtworkDirectory,
    [ValidateRange(1024, 65535)][int]$Port = 18984,
    [ValidateSet('Dark', 'Light')][string]$Theme = 'Dark',
    [string]$RunDirectory,
    [switch]$FailFirstPlaybackInfo,
    [switch]$DesignCatalog,
    [string]$SubtitleFile,
    [string]$BifFile,
    [switch]$EmbeddedPgs,
    [switch]$AllowLongEmbeddedPgs,
    [ValidateRange(0, 10000)][int]$LargeLibraryItems = 0,
    [ValidateRange(0, 1000)][int]$ImageDelayMilliseconds = 0,
    [ValidatePattern('^[A-Za-z0-9_-]+:[1-9][0-9]{0,4}$')][string[]]$ItemDetailFailure = @(),
    [ValidatePattern('^[A-Za-z0-9_-]+:[1-9][0-9]{0,4}:[1-9][0-9]{0,4}$')][string[]]$ItemDetailDelay = @(),
    [ValidateRange(0, 30000)][int]$FirstMediaDelayMilliseconds = 0,
    [ValidateRange(0, 30000)][int]$StopDelayMilliseconds = 0,
    [ValidateRange(0, 30000)][int]$LogoutDelayMilliseconds = 0
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
$largeLibraryMode = $LargeLibraryItems -gt 0
if ($largeLibraryMode -and ($DesignCatalog -or $ArtworkDirectory -or $SubtitleFile -or $BifFile -or $EmbeddedPgs -or $AllowLongEmbeddedPgs)) {
    throw 'Large-library acceptance requires its own catalog without design, artwork, or subtitle options.'
}
if ($AllowLongEmbeddedPgs -and -not $EmbeddedPgs) {
    throw 'Long-PGS acceptance requires the separate -EmbeddedPgs opt-in.'
}
$artworkPath = $null
if (-not $largeLibraryMode) {
    if (-not $ArtworkDirectory) { throw 'Rich catalog acceptance requires an explicit artwork directory.' }
    $artworkPath = Get-LumenAbsolutePath $ArtworkDirectory
    if (-not (Test-Path -LiteralPath $artworkPath -PathType Container)) { throw 'The external artwork directory is unavailable.' }
}
$expectedFixtureServerId = if ($largeLibraryMode) { "synthetic-large-server-$LargeLibraryItems" }
    elseif ($DesignCatalog) { 'synthetic-lumen-design-server-0001' } else { 'synthetic-lumen-server-0001' }
$metadataPath = Join-Path $mediaPath 'fixture-h264-aac.json'
$videoPath = Join-Path $mediaPath $(if ($EmbeddedPgs) { 'fixture-h264-aac-pgs.mkv' } else { 'fixture-h264-aac.mp4' })
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$metadataHash = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
if ($metadata.Synthetic -ne $true -or $metadata.FileLength -ne (Get-Item -LiteralPath $videoPath).Length -or
    $metadata.DurationTicks -le 0) { throw 'The media must be an existing measured synthetic fixture.' }
$pgsMediaHash = $null
$pgsProbeHash = $null
if ($EmbeddedPgs) {
    if ($SubtitleFile) { throw 'The embedded PGS and external WebVTT cases require separate fixture instances.' }
    $durationMatches = if ($AllowLongEmbeddedPgs) {
        $metadata.DurationTicks -ge 990000000 -and $metadata.DurationTicks -le 1010000000 -and
            $metadata.Width -le 1920 -and $metadata.Height -le 1080
    }
    else { $metadata.DurationTicks -ge 590000000 -and $metadata.DurationTicks -le 610000000 }
    if (-not $durationMatches) { throw 'The measured PGS duration or dimensions are outside the explicitly selected bounded case.' }
    $pgsProbePath = Join-Path $mediaPath 'fixture-h264-aac.ffprobe.json'
    if ($metadata.FileName -cne 'fixture-h264-aac-pgs.mkv' -or $metadata.Container -cne 'mkv' -or
        -not (Test-Path -LiteralPath $pgsProbePath -PathType Leaf) -or
        (Get-Item -LiteralPath $pgsProbePath).Length -gt 64 * 1024 -or
        (Get-Item -LiteralPath $videoPath).Length -gt 64 * 1024 * 1024) {
        throw 'The explicit PGS case requires its bounded measured MKV and FFprobe evidence.'
    }
    $pgsMediaHash = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash
    $pgsProbeHash = (Get-FileHash -LiteralPath $pgsProbePath -Algorithm SHA256).Hash
    if ($pgsMediaHash -ine $metadata.FileSha256 -or $pgsProbeHash -ine $metadata.ProbeSha256) {
        throw 'The explicit PGS case changed after its measured metadata was prepared.'
    }
}
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
$bifPath = $null
$bifHash = $null
$bifBytes = 0
if ($BifFile) {
    $bifPath = Get-LumenAbsolutePath $BifFile
    if (-not (Test-Path -LiteralPath $bifPath -PathType Leaf) -or
        [IO.Path]::GetExtension($bifPath) -ine '.bif') { throw 'The explicit thumbnail input must be an existing .bif file.' }
    $bifStream = [IO.File]::OpenRead($bifPath)
    try {
        if ($bifStream.Length -lt 72 -or $bifStream.Length -gt 32 * 1024 * 1024) {
            throw 'The thumbnail input must fit the supported 32 MiB BIF bound.'
        }
        $bifData = [byte[]]::new([int]$bifStream.Length)
        $bifStream.ReadExactly($bifData, 0, $bifData.Length)
        if ($bifStream.ReadByte() -ne -1) { throw 'The BIF file changed beyond the bounded startup snapshot.' }
        $bifHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bifData))
        $bifBytes = $bifData.Length
    }
    finally { $bifStream.Dispose() }
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
    $fixtureArguments = @('--port', [string]$Port, '--media-dir', ('"' + $mediaPath + '"'))
    if ($largeLibraryMode) { $fixtureArguments += @('--large-library-items', [string]$LargeLibraryItems) }
    else {
        $fixtureArguments += @($(if ($DesignCatalog) { '--lumen-design-catalog' } else { '--lumen-catalog' }),
            '--artwork-directory', ('"' + $artworkPath + '"'))
    }
    if ($FailFirstPlaybackInfo) { $fixtureArguments += '--fail-first-playback-info' }
    if ($ImageDelayMilliseconds -gt 0) { $fixtureArguments += @('--image-delay-ms', [string]$ImageDelayMilliseconds) }
    foreach ($control in $ItemDetailFailure) { $fixtureArguments += @('--item-detail-failure', $control) }
    foreach ($control in $ItemDetailDelay) { $fixtureArguments += @('--item-detail-delay', $control) }
    if ($FirstMediaDelayMilliseconds -gt 0) { $fixtureArguments += @('--first-media-delay-ms', [string]$FirstMediaDelayMilliseconds) }
    if ($StopDelayMilliseconds -gt 0) { $fixtureArguments += @('--stop-delay-ms', [string]$StopDelayMilliseconds) }
    if ($LogoutDelayMilliseconds -gt 0) { $fixtureArguments += @('--logout-delay-ms', [string]$LogoutDelayMilliseconds) }
    if ($subtitlePath) { $fixtureArguments += @('--subtitle-file', ('"' + $subtitlePath + '"')) }
    if ($bifPath) { $fixtureArguments += @('--bif-file', ('"' + $bifPath + '"')) }
    if ($EmbeddedPgs) { $fixtureArguments += '--embedded-pgs' }
    if ($AllowLongEmbeddedPgs) { $fixtureArguments += '--allow-long-embedded-pgs' }
    $fixtureProcess = Start-Process -FilePath $runtimeExecutable -WorkingDirectory $runtimeDirectory `
        -ArgumentList $fixtureArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runPath 'server.stdout.log') -RedirectStandardError (Join-Path $runPath 'server.stderr.log')
    $ownership = [ordered]@{
        Scope = 'OwnedSyntheticLumenAcceptance'
        Synthetic = $true
        DesignCatalog = [bool]$DesignCatalog
        LargeLibraryItems = $LargeLibraryItems
        ImageDelayMilliseconds = $ImageDelayMilliseconds
        ItemDetailFailure = $ItemDetailFailure
        ItemDetailDelay = $ItemDetailDelay
        FirstMediaDelayMilliseconds = $FirstMediaDelayMilliseconds
        StopDelayMilliseconds = $StopDelayMilliseconds
        LogoutDelayMilliseconds = $LogoutDelayMilliseconds
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
        MetadataSha256 = $metadataHash
        MediaDurationTicks = [long]$metadata.DurationTicks
        EmbeddedPgsEnabled = [bool]$EmbeddedPgs
        LongEmbeddedPgsEnabled = [bool]$AllowLongEmbeddedPgs
        EmbeddedPgsMediaSha256 = $pgsMediaHash
        EmbeddedPgsProbeSha256 = $pgsProbeHash
        EmbeddedPgsNativeSubtitleIndex = $(if ($EmbeddedPgs) { $metadata.EmbeddedSubtitle.Index } else { $null })
        EmbeddedPgsSourceUnchanged = $null
        ExternalSubtitleEnabled = [bool]$subtitlePath
        SubtitleSourcePath = $subtitlePath
        SubtitleSourceSha256 = $subtitleHash
        SubtitleSourceBytes = $subtitleBytes
        SubtitleSourceUnchanged = $null
        ExternalBifEnabled = [bool]$bifPath
        BifSourcePath = $bifPath
        BifSourceSha256 = $bifHash
        BifSourceBytes = $bifBytes
        BifSourceUnchanged = $null
        BifTotalImageCount = $null
        BifAvailableImageCount = $null
        BifSelectedImageCount = $null
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
            $observedBif = $null -ne $stats.PSObject.Properties['ExternalBifConfigured'] -and $stats.ExternalBifConfigured -eq $true
            $observedEmbeddedPgs = $null -ne $stats.PSObject.Properties['EmbeddedPgsConfigured'] -and $stats.EmbeddedPgsConfigured -eq $true
            $observedLongEmbeddedPgs = $null -ne $stats.PSObject.Properties['LongEmbeddedPgsConfigured'] -and $stats.LongEmbeddedPgsConfigured -eq $true
            $subtitleSnapshotMatches = -not $subtitlePath -or
                ($null -ne $stats.PSObject.Properties['ExternalSubtitleSha256'] -and $stats.ExternalSubtitleSha256 -ceq $subtitleHash)
            $bifSnapshotMatches = -not $bifPath -or
                ($null -ne $stats.PSObject.Properties['ExternalBifSha256'] -and $stats.ExternalBifSha256 -ceq $bifHash -and
                    $null -ne $stats.PSObject.Properties['ExternalBifBytes'] -and $stats.ExternalBifBytes -eq $bifBytes -and
                    $null -ne $stats.PSObject.Properties['ExternalBifTotalImageCount'] -and $stats.ExternalBifTotalImageCount -gt 0 -and
                    $null -ne $stats.PSObject.Properties['ExternalBifAvailableImageCount'] -and $stats.ExternalBifAvailableImageCount -gt 0 -and
                    $null -ne $stats.PSObject.Properties['ExternalBifSelectedImageCount'] -and $stats.ExternalBifSelectedImageCount -gt 0 -and
                    $stats.ExternalBifSelectedImageCount -le 12)
            $pgsSnapshotMatches = -not $EmbeddedPgs -or
                ($null -ne $stats.PSObject.Properties['MediaContainer'] -and $stats.MediaContainer -ceq 'mkv' -and
                    $null -ne $stats.PSObject.Properties['EmbeddedPgsMediaSha256'] -and $stats.EmbeddedPgsMediaSha256 -ieq $pgsMediaHash -and
                    $null -ne $stats.PSObject.Properties['EmbeddedPgsProbeSha256'] -and $stats.EmbeddedPgsProbeSha256 -ieq $pgsProbeHash -and
                    $null -ne $stats.PSObject.Properties['EmbeddedPgsNativeSubtitleIndex'] -and
                    $stats.EmbeddedPgsNativeSubtitleIndex -eq $metadata.EmbeddedSubtitle.Index -and
                    (-not $AllowLongEmbeddedPgs -or ($null -ne $stats.PSObject.Properties['MediaDurationTicks'] -and
                        $stats.MediaDurationTicks -eq $metadata.DurationTicks)))
            $catalogMatches = $stats.LumenCatalog -eq (-not $largeLibraryMode) -and
                $null -ne $stats.PSObject.Properties['LargeLibraryItems'] -and $stats.LargeLibraryItems -eq $LargeLibraryItems -and
                $stats.ServerId -ceq $expectedFixtureServerId
            $boundaryConfiguration = $stats.BoundaryControls.Configuration
            $delaySettingsMatch = $stats.ImageDelayMilliseconds -eq $ImageDelayMilliseconds -and
                $boundaryConfiguration.FirstMediaDelayMilliseconds -eq $FirstMediaDelayMilliseconds -and
                $boundaryConfiguration.StopDelayMilliseconds -eq $StopDelayMilliseconds -and
                $boundaryConfiguration.LogoutDelayMilliseconds -eq $LogoutDelayMilliseconds
            if ($stats.Synthetic -eq $true -and $catalogMatches -and $delaySettingsMatch -and
                $observedDesign -eq [bool]$DesignCatalog -and $observedSubtitle -eq [bool]$subtitlePath -and
                $observedBif -eq [bool]$bifPath -and $observedEmbeddedPgs -eq [bool]$EmbeddedPgs -and
                $observedLongEmbeddedPgs -eq [bool]$AllowLongEmbeddedPgs -and
                $subtitleSnapshotMatches -and $bifSnapshotMatches -and $pgsSnapshotMatches) { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 150
    }
    if (-not $ready) { throw 'The owned synthetic fixture did not become ready. Inspect this run logs.' }
    if ($bifPath) {
        $ownership.BifTotalImageCount = $stats.ExternalBifTotalImageCount
        $ownership.BifAvailableImageCount = $stats.ExternalBifAvailableImageCount
        $ownership.BifSelectedImageCount = $stats.ExternalBifSelectedImageCount
    }
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or
        $listeners[0].OwningProcess -ne $fixtureProcess.Id) {
        throw 'The observed listener does not match the owned IPv4 loopback fixture.'
    }
    $bootstrapArguments = @('--workspace-root', ('"' + $workspaceRoot + '"'), '--run-directory', ('"' + $runPath + '"'),
        '--server-url', ("http://127.0.0.1:$Port"), '--theme', $Theme)
    if ($DesignCatalog) { $bootstrapArguments += @('--design-catalog', 'true') }
    if ($largeLibraryMode) { $bootstrapArguments += @('--large-library-items', [string]$LargeLibraryItems) }
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
    if ($bifPath) {
        $ownership.BifSourceUnchanged = (Get-FileHash -LiteralPath $bifPath -Algorithm SHA256).Hash -ceq $bifHash
        if (-not $ownership.BifSourceUnchanged) { throw 'The explicit BIF source changed during environment startup.' }
    }
    else { $ownership.BifSourceUnchanged = $null }
    if ($EmbeddedPgs) {
        $ownership.EmbeddedPgsSourceUnchanged = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash -ieq $pgsMediaHash -and
            (Get-FileHash -LiteralPath $pgsProbePath -Algorithm SHA256).Hash -ieq $pgsProbeHash -and
            (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash -ieq $metadataHash
        if (-not $ownership.EmbeddedPgsSourceUnchanged) { throw 'The explicit PGS inputs changed during environment startup.' }
    }
    $ownership | ConvertTo-Json | Set-Content -LiteralPath $ownershipPath -Encoding utf8
    [pscustomobject]@{
        Ready = $true
        Synthetic = $true
        LargeLibraryItems = $LargeLibraryItems
        ServerUrl = "http://127.0.0.1:$Port"
        RunDirectory = $runPath
        ProfileDirectory = $profile.ProfileDirectory
        FixtureProcessId = $fixtureProcess.Id
        OwnershipReceipt = $ownershipPath
        ExternalSubtitleEnabled = [bool]$subtitlePath
        SubtitleSourceSha256 = $subtitleHash
        ExternalBifEnabled = [bool]$bifPath
        BifSourceSha256 = $bifHash
        BifTotalImageCount = $ownership.BifTotalImageCount
        BifAvailableImageCount = $ownership.BifAvailableImageCount
        BifSelectedImageCount = $ownership.BifSelectedImageCount
        EmbeddedPgsEnabled = [bool]$EmbeddedPgs
        LongEmbeddedPgsEnabled = [bool]$AllowLongEmbeddedPgs
        MediaDurationTicks = [long]$metadata.DurationTicks
        EmbeddedPgsNativeSubtitleIndex = $ownership.EmbeddedPgsNativeSubtitleIndex
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
