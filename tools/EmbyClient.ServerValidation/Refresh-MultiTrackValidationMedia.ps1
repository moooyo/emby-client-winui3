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
$headers = @{ 'X-Emby-Authorization' = ('MediaBrowser Client="Emby Validation Media Setup", Device="Validation media setup", DeviceId="media-setup-' + [Guid]::NewGuid().ToString('N') + '", Version="1.0.0"') }
$info = Invoke-RestMethod -Uri "$apiRoot/System/Info/Public" -Headers $headers -TimeoutSec 10
if ($info.Version -ne '4.9.5.0' -or $info.ServerName -ne "Official Emby validation $runId") {
    throw 'The endpoint does not match this owned official server generation.'
}
$authentication = Invoke-RestMethod -Method Post -Uri "$apiRoot/Users/AuthenticateByName" -Headers $headers -ContentType 'application/json' -Body (@{ Username = $credential.Username; Pw = $credential.Password } | ConvertTo-Json -Compress) -TimeoutSec 10
$headers['X-Emby-Token'] = $authentication.AccessToken
try {
    $null = Invoke-RestMethod -Method Post -Uri "$apiRoot/Library/Refresh" -Headers $headers -TimeoutSec 10
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $items = Invoke-RestMethod -Uri "$apiRoot/Users/$($authentication.User.Id)/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams&Limit=100" -Headers $headers -TimeoutSec 10
        $matches = @($items.Items | Where-Object { $_.Name -like 'Track Validation*' })
        if ($matches.Count -eq 1) {
            # Emby's collection response includes only the default source; item
            # details expose every version grouped under the primary item ID.
            $matches[0] = Invoke-RestMethod -Uri "$apiRoot/Users/$($authentication.User.Id)/Items/$($matches[0].Id)?Fields=MediaSources,MediaStreams" -Headers $headers -TimeoutSec 10
        }
        if ($matches.Count -eq 1 -and @($matches[0].MediaSources).Count -eq 2) {
            break
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    $movies = foreach ($item in $matches) {
        $sources = foreach ($source in $item.MediaSources) {
            $video = @($source.MediaStreams | Where-Object { $_.Type -eq 'Video' })[0]
            $audio = @($source.MediaStreams | Where-Object { $_.Type -eq 'Audio' })
            [ordered]@{
                MediaSourceId = $source.Id
                Name = $source.Name
                Container = $source.Container
                RunTimeTicks = $source.RunTimeTicks
                Width = $video.Width
                Height = $video.Height
                AudioTracks = @($audio | ForEach-Object {
                    [ordered]@{ Index = $_.Index; Codec = $_.Codec; Language = $_.Language; DisplayTitle = $_.DisplayTitle; Channels = $_.Channels; IsDefault = $_.IsDefault }
                })
            }
        }
        [ordered]@{ ItemId = $item.Id; Name = $item.Name; MediaSources = @($sources) }
    }
    $fixture = @($items.Items | Where-Object { $_.Name -eq 'Fixture' })
    $evidence = [ordered]@{
        RecordedUtc = [DateTime]::UtcNow.ToString('O')
        ServerVersion = $info.Version
        ServerId = $info.Id
        OriginalFixturePresent = $fixture.Count -eq 1
        OriginalFixtureItemId = if ($fixture.Count -eq 1) { $fixture[0].Id } else { $null }
        MultiVersionGrouped = $matches.Count -eq 1 -and @($matches[0].MediaSources).Count -eq 2
        SourceEvidenceEndpoint = 'User item details'
        Movies = @($movies)
    }
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifactRoot 'multitrack-api-evidence.json') -Encoding utf8
    $evidence | ConvertTo-Json -Depth 10
    if (-not $evidence.OriginalFixturePresent -or $matches.Count -eq 0) {
        throw 'The expected generated movies were not found after the scan.'
    }
    foreach ($movie in $movies) {
        foreach ($source in $movie.MediaSources) {
            if ($source.AudioTracks.Count -ne 2 -or @($source.AudioTracks | Where-Object { $_.Codec -ne 'aac' }).Count -ne 0) {
                throw 'Each generated version must expose exactly two AAC streams through the real API.'
            }
        }
    }
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
