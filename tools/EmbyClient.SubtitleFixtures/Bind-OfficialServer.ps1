#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $ManifestPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifestPath = [IO.Path]::GetFullPath($ManifestPath)
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $manifestPath.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($manifestPath) -ne 'manifest.json') {
    throw 'Only this tool''s generated manifest is accepted.'
}
$outputDirectory = Split-Path -Parent $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$staging = Get-Content -LiteralPath (Join-Path $outputDirectory 'staging-receipt.json') -Raw | ConvertFrom-Json
if ($manifest.Synthetic -ne $true -or $manifest.BindingStatus -ne 'UnboundGeneratedFilesOnly' -or
    (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $staging.ManifestSha256) {
    throw 'The original generation/staging identity changed.'
}
$boundPath = Join-Path $outputDirectory 'bound-manifest.json'
if (Test-Path -LiteralPath $boundPath) { throw 'An existing bound manifest will not be overwritten.' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/emby-validation'))
$runtime = (Get-Content -LiteralPath (Join-Path $artifactRoot 'wsl-runtime-root.txt') -Raw).Trim()
if ($runtime -ne $staging.RuntimeDirectory -or $runtime -notmatch '^/tmp/emby-client-validation-([0-9a-f-]{36})$') {
    throw 'The staged media belongs to a different runtime.'
}
$runId = $Matches[1]
$apiRoot = 'http://127.0.0.1:19096/emby'
$headers = @{ 'X-Emby-Authorization' = ('MediaBrowser Client="Complex Subtitle Fixture Binding", Device="Owned fixture binding", DeviceId="complex-binding-' + [Guid]::NewGuid().ToString('N') + '", Version="1.0.0"') }
function Request([string] $Method, [string] $Path, [object] $Body = $null) {
    $parameters = @{ Method = $Method; Uri = "$apiRoot/$Path"; Headers = $headers; TimeoutSec = 30 }
    if ($null -ne $Body) { $parameters.ContentType = 'application/json'; $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 12 -Compress }
    return Invoke-RestMethod @parameters
}
function Authenticate([string] $FileName) {
    $credential = Get-Content -LiteralPath (Join-Path $artifactRoot $FileName) -Raw | ConvertFrom-Json
    if ($credential.ServerUrl -ne 'http://127.0.0.1:19096') { throw 'Only the owned loopback credential is accepted.' }
    try {
        $response = Request Post 'Users/AuthenticateByName' @{ Username = $credential.Username; Pw = $credential.Password }
        $headers['X-Emby-Token'] = $response.AccessToken
        return $response.User.Id
    }
    finally { $credential = $null; $response = $null }
}
function Logout {
    try { $null = Request Post 'Sessions/Logout' }
    finally { $null = $headers.Remove('X-Emby-Token') }
}
function OptionalProperty([object] $Value, [string] $Name) {
    $property = $Value.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
$info = Request Get 'System/Info/Public'
if ($info.Id -ne 'cf4feb10df224135877fc61204a28212' -or $info.Version -ne $manifest.RequiredServerVersion -or $info.ServerName -ne "Official Emby validation $runId") {
    throw 'The endpoint is not the expected resumed official validation server.'
}
$namespaceState = (wsl --distribution Debian --exec cat "$runtime/state.json") | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $namespaceState.status -ne 'ready' -or $namespaceState.mode -ne 'server' -or
    $namespaceState.outer.bound_endpoint.address -ne '127.0.0.1' -or $namespaceState.outer.bound_endpoint.port -ne 19096 -or
    $namespaceState.inner.server_network_namespace -ne $namespaceState.inner.network.network_namespace -or
    $namespaceState.inner.network.network_namespace -eq $namespaceState.outer.network_namespace -or
    (@($namespaceState.inner.network.interfaces) -join ',') -ne 'lo') {
    throw 'The active server isolation does not match the owned runner.'
}
$libraryName = 'Official Complex Subtitle Validation ' + [IO.Path]::GetFileName($outputDirectory)
$adminId = Authenticate 'official-admin-credentials.json'
try {
    $before = Request Get 'Library/VirtualFolders/Query'
    if (@($before.Items | Where-Object { $_.Name -eq $libraryName }).Count -gt 0) { throw 'This generated library already exists and will not be replaced.' }
    $options = @{
        PathInfos = @(@{ Path = $staging.MediaDirectory }); ContentType = 'movies'
        EnableRealtimeMonitor = $false; EnableInternetProviders = $false; EnableChapterImageExtraction = $false
        ExtractChapterImagesDuringLibraryScan = $false; EnableMarkerDetectionDuringLibraryScan = $false
        DownloadImagesInAdvance = $false; SaveLocalMetadata = $false; MetadataSavers = @(); SubtitleDownloadLanguages = @()
        TypeOptions = @(@{ Type = 'Movie'; MetadataFetchers = @(); MetadataFetcherOrder = @(); ImageFetchers = @(); ImageFetcherOrder = @() })
    }
    $null = Request Post ('Library/VirtualFolders?name=' + [Uri]::EscapeDataString($libraryName) + '&collectionType=movies&refreshLibrary=true') @{ LibraryOptions = $options }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $folders = Request Get 'Library/VirtualFolders/Query'
        $matches = @($folders.Items | Where-Object { $_.Name -eq $libraryName })
        if ($matches.Count -eq 1) {
            $libraryId = $matches[0].ItemId
            $items = Request Get "Users/$adminId/Items?ParentId=$libraryId&Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams,Path&Limit=10"
            if (@($items.Items).Count -eq 2) { break }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($matches.Count -ne 1 -or @($items.Items).Count -ne 2) { throw 'Both complex subtitle movies were not indexed before the deadline.' }
    foreach ($oldFolder in $before.Items) {
        if (@($folders.Items | Where-Object { $_.ItemId -eq $oldFolder.ItemId -and $_.Name -eq $oldFolder.Name }).Count -ne 1) {
            throw 'An existing library identity changed during the independent addition.'
        }
    }
}
finally { Logout }

$playbackUserId = Authenticate 'official-user-credentials.json'
$cases = [Collections.Generic.List[object]]::new()
try {
    foreach ($case in $manifest.Cases) {
        $match = @($items.Items | Where-Object { $_.Name -eq $case.ExpectedItemName })
        if ($match.Count -ne 1) { throw 'A unique expected generated movie name was not found.' }
        $item = Request Get "Users/$playbackUserId/Items/$($match[0].Id)"
        $sources = @($item.MediaSources)
        if ($sources.Count -ne 1) { throw 'Each generated case must expose exactly one real media source.' }
        $source = $sources[0]
        $relative = $case.MediaFileName.Replace('\', '/').Substring('media/'.Length)
        if ($source.Path -ne "$($staging.MediaDirectory)/$relative") { throw 'The actual API media path does not match the staged file.' }
        $subtitles = @($source.MediaStreams | Where-Object { $_.Type -eq 'Subtitle' })
        if ($subtitles.Count -ne 1) { throw 'The generated source must expose exactly one embedded subtitle.' }
        $subtitle = $subtitles[0]
        $expectedText = $case.CaseId -eq 'ass-styled'
        if ($subtitle.Codec -ne $case.ExpectedSubtitleCodec -or $subtitle.IsExternal -ne $false -or
            $subtitle.IsTextSubtitleStream -ne $expectedText -or $subtitle.Index -ne $case.ObservedFileSubtitleStreamIndex) {
            throw 'The actual embedded subtitle metadata does not match the generated case.'
        }
        if ([Math]::Abs([long]$source.RunTimeTicks - [long]$case.RunTimeTicks) -gt 1000000) { throw 'The API runtime differs materially from the generated source.' }
        $case.ItemId = $item.Id; $case.MediaSourceId = $source.Id; $case.SubtitleStreamIndex = [int]$subtitle.Index
        $case.RunTimeTicks = [long]$source.RunTimeTicks
        $attachments = @(OptionalProperty $source 'MediaAttachments') | Where-Object { $null -ne $_ }
        $safeAttachments = @($attachments | ForEach-Object {
            [ordered]@{ Index = OptionalProperty $_ 'Index'; Codec = OptionalProperty $_ 'Codec'; MimeType = OptionalProperty $_ 'MimeType'; Name = OptionalProperty $_ 'Name'; Filename = OptionalProperty $_ 'Filename'; PropertyNames = @($_.PSObject.Properties.Name) }
        })
        $cases.Add([ordered]@{
            CaseId = $case.CaseId; ItemId = $item.Id; MediaSourceId = $source.Id; ItemName = $item.Name; RunTimeTicks = $source.RunTimeTicks
            SubtitleStreamIndex = $subtitle.Index; Codec = $subtitle.Codec; IsExternal = $subtitle.IsExternal
            IsTextSubtitleStream = $subtitle.IsTextSubtitleStream; MediaAttachmentsFieldPresent = $null -ne $source.PSObject.Properties['MediaAttachments']
            MediaAttachments = $safeAttachments
            ItemAttachmentFieldNames = @($item.PSObject.Properties.Name | Where-Object { $_ -match 'Attachment' })
            SourceAttachmentFieldNames = @($source.PSObject.Properties.Name | Where-Object { $_ -match 'Attachment' })
            AttachmentStreamCount = @($source.MediaStreams | Where-Object { $_.Type -eq 'Attachment' }).Count
            AttachmentStreams = @($source.MediaStreams | Where-Object { $_.Type -eq 'Attachment' } | ForEach-Object {
                [ordered]@{ Index = $_.Index; Type = $_.Type; Codec = $_.Codec; DisplayTitle = OptionalProperty $_ 'DisplayTitle'; Title = OptionalProperty $_ 'Title'; MimeType = OptionalProperty $_ 'MimeType'; AttachmentSize = OptionalProperty $_ 'AttachmentSize'; IsExternal = OptionalProperty $_ 'IsExternal'; PropertyNames = @($_.PSObject.Properties.Name) }
            })
        })
    }
}
finally { Logout }
$manifest.ServerId = $info.Id; $manifest.ServerVersion = $info.Version; $manifest.BindingStatus = 'BoundToOwnedOfficialServer'
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $boundPath -Encoding utf8
$receipt = [ordered]@{
    RecordedUtc = [DateTime]::UtcNow.ToString('O'); ServerId = $info.Id; ServerVersion = $info.Version
    LibraryId = $libraryId; LibraryName = $libraryName; PreservedLibraryCount = @($before.Items).Count
    RuntimeDirectory = $runtime; MediaDirectory = $staging.MediaDirectory
    UnboundManifestSha256 = $staging.ManifestSha256; BoundManifestSha256 = (Get-FileHash -LiteralPath $boundPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Cases = @($cases.ToArray()); PlaybackCreated = $false
}
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputDirectory 'binding-receipt.json') -Encoding utf8
$receipt | ConvertTo-Json -Depth 12
