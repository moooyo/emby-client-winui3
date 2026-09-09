#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/emby-validation'))
$runtimeDirectory = (Get-Content -LiteralPath (Join-Path $artifactRoot 'wsl-runtime-root.txt') -Raw).Trim()
$runId = $runtimeDirectory.Substring('/tmp/emby-client-validation-'.Length)
$credential = Get-Content -LiteralPath (Join-Path $artifactRoot 'official-admin-credentials.json') -Raw | ConvertFrom-Json
if ($credential.ServerUrl -ne 'http://127.0.0.1:19096') {
    throw 'Only the owned loopback validation server is accepted.'
}
$apiRoot = "$($credential.ServerUrl)/emby"
$headers = @{ 'X-Emby-Authorization' = ('MediaBrowser Client="Emby Validation TV Setup", Device="Validation TV setup", DeviceId="tv-setup-' + [Guid]::NewGuid().ToString('N') + '", Version="1.0.0"') }
$info = Invoke-RestMethod -Uri "$apiRoot/System/Info/Public" -Headers $headers -TimeoutSec 10
if ($info.Version -ne '4.9.5.0' -or $info.ServerName -ne "Official Emby validation $runId") {
    throw 'The endpoint does not match this owned official server generation.'
}
$authentication = Invoke-RestMethod -Method Post -Uri "$apiRoot/Users/AuthenticateByName" -Headers $headers -ContentType 'application/json' -Body (@{ Username = $credential.Username; Pw = $credential.Password } | ConvertTo-Json -Compress) -TimeoutSec 10
$headers['X-Emby-Token'] = $authentication.AccessToken
try {
    $folders = Invoke-RestMethod -Uri "$apiRoot/Library/VirtualFolders/Query" -Headers $headers -TimeoutSec 10
    if (@($folders.Items | Where-Object { $_.Name -eq 'Official Validation TV' }).Count -gt 0) {
        throw 'The episode validation library already exists; it will not be replaced.'
    }
    $typeOptions = @('Series', 'Season', 'Episode') | ForEach-Object {
        @{ Type = $_; MetadataFetchers = @(); MetadataFetcherOrder = @(); ImageFetchers = @(); ImageFetcherOrder = @() }
    }
    $options = @{
        PathInfos = @(@{ Path = "$runtimeDirectory/tv-media" })
        ContentType = 'tvshows'
        EnableRealtimeMonitor = $false
        EnableInternetProviders = $false
        EnableChapterImageExtraction = $false
        ExtractChapterImagesDuringLibraryScan = $false
        EnableMarkerDetectionDuringLibraryScan = $false
        DownloadImagesInAdvance = $false
        SaveLocalMetadata = $false
        MetadataSavers = @()
        SubtitleDownloadLanguages = @()
        TypeOptions = @($typeOptions)
    }
    $null = Invoke-RestMethod -Method Post -Uri "$apiRoot/Library/VirtualFolders?name=Official%20Validation%20TV&collectionType=tvshows&refreshLibrary=true" -Headers $headers -ContentType 'application/json' -Body (@{ LibraryOptions = $options } | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 10
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $seasons = [pscustomobject]@{ Items = @() }
    $episodes = [pscustomobject]@{ Items = @() }
    do {
        $seriesItems = Invoke-RestMethod -Uri "$apiRoot/Users/$($authentication.User.Id)/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm=Validation%20Series&Limit=10" -Headers $headers -TimeoutSec 10
        if (@($seriesItems.Items).Count -eq 1) {
            $series = $seriesItems.Items[0]
            $seasons = Invoke-RestMethod -Uri "$apiRoot/Shows/$($series.Id)/Seasons?UserId=$($authentication.User.Id)" -Headers $headers -TimeoutSec 10
            $episodes = Invoke-RestMethod -Uri "$apiRoot/Shows/$($series.Id)/Episodes?UserId=$($authentication.User.Id)&Fields=MediaSources,MediaStreams" -Headers $headers -TimeoutSec 10
            if (@($seasons.Items).Count -eq 1 -and @($episodes.Items).Count -eq 2) {
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    if (@($seriesItems.Items).Count -ne 1 -or @($seasons.Items).Count -ne 1 -or @($episodes.Items).Count -ne 2) {
        throw 'The expected series, season, and two episodes were not indexed before the deadline.'
    }
    $evidence = [ordered]@{
        RecordedUtc = [DateTime]::UtcNow.ToString('O')
        ServerVersion = $info.Version
        ServerId = $info.Id
        SeriesId = $series.Id
        SeriesName = $series.Name
        SeasonId = $seasons.Items[0].Id
        Episodes = @($episodes.Items | Sort-Object IndexNumber | ForEach-Object {
            [ordered]@{
                ItemId = $_.Id
                Name = $_.Name
                SeasonNumber = $_.ParentIndexNumber
                EpisodeNumber = $_.IndexNumber
                RunTimeTicks = $_.RunTimeTicks
                MediaSourceIds = @($_.MediaSources | ForEach-Object { $_.Id })
            }
        })
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot 'episode-api-evidence.json') -Encoding utf8
    $evidence | ConvertTo-Json -Depth 8
}
finally {
    try {
        $null = Invoke-RestMethod -Method Post -Uri "$apiRoot/Sessions/Logout" -Headers $headers -TimeoutSec 10
    }
    finally {
        $headers.Remove('X-Emby-Token')
        $authentication = $null
        $credential = $null
    }
}
