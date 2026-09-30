param([Parameter(Mandatory)][int]$ClosedAppProcessId,
      [Parameter(Mandatory)][string]$FixtureRunDirectory,
      [string]$ReceiptDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnvironmentHelpers.ps1')
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRun = Get-LumenAbsolutePath $FixtureRunDirectory
Assert-LumenRunDirectory -RunDirectory $fixtureRun -WorkspaceRoot $workspace
if (Get-Process -Id $ClosedAppProcessId -ErrorAction SilentlyContinue) {
    throw 'The previously owned application is still present. No fixture state was changed.'
}
$ownership = Get-Content -LiteralPath (Join-Path $fixtureRun 'ownership.json') -Raw | ConvertFrom-Json
if ($ownership.Scope -ne 'OwnedSyntheticLumenAcceptance' -or $ownership.Synthetic -ne $true -or
    $ownership.Port -ne 18984 -or $ownership.RunDirectory -ne $fixtureRun -or
    $ownership.WorkspaceRoot -ne $workspace -or $null -eq (Get-LumenOwnedProcess $ownership)) {
    throw 'The exact owned rich fixture is unavailable. No fixture state was changed.'
}
$receiptRoot = if ($ReceiptDirectory) { Get-LumenAbsolutePath $ReceiptDirectory }
    else { Join-Path $workspace ('artifacts/lumen-acceptance/resume-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
Assert-LumenRunDirectory -RunDirectory $receiptRoot -WorkspaceRoot $workspace
if (Test-Path -LiteralPath $receiptRoot) { throw 'Select a new state-preparation receipt directory.' }
New-Item -ItemType Directory -Path $receiptRoot | Out-Null
$base = 'http://127.0.0.1:18984'
$userId = 'synthetic-user-demo'
$headers = @{ 'X-Emby-Authorization' = ('Emby Client="LumenFixtureStatePreparation", Device="Synthetic fixture setup", DeviceId="' + [Guid]::NewGuid().ToString('N') + '", Version="1.0"') }
$requestCount = 0
$clock = [Diagnostics.Stopwatch]::StartNew()
$startedAt = [DateTimeOffset]::UtcNow
$phase = 'Identity'
$authenticated = $false

function Invoke-Fixture {
    param([string]$Method, [string]$Path, [object]$Body = $null, [int]$ExpectedStatus = 200)
    if ($script:requestCount -ge 24 -or $script:clock.Elapsed.TotalSeconds -ge 40) {
        throw 'The bounded fixture preparation request budget was exhausted.'
    }
    $script:requestCount++
    $parameters = @{ Uri=($script:base + $Path); Method=$Method; Headers=$script:headers;
        NoProxy=$true; TimeoutSec=5; MaximumRedirection=0; SkipHttpErrorCheck=$true }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 8 -Compress; $parameters.ContentType='application/json' }
    $response = Invoke-WebRequest @parameters
    if ([int]$response.StatusCode -ne $ExpectedStatus -or
        $response.Headers['X-Synthetic-Fixture'] -ne 'EmbyClient development data; not Emby Server') {
        throw 'The fixed synthetic endpoint returned an unexpected status or identity header.'
    }
    if ($ExpectedStatus -eq 204 -or [string]::IsNullOrWhiteSpace($response.Content)) { return $null }
    $response.Content | ConvertFrom-Json
}

function Episode-State {
    param($Item)
    $lastProperty = $Item.UserData.PSObject.Properties['LastPlayedDate']
    $last = if ($null -eq $lastProperty) { $null } else { $lastProperty.Value }
    $lastUtc = if ($null -eq $last) { $null }
        elseif ($last -is [DateTime]) { $last.ToUniversalTime().ToString('O') }
        elseif ($last -is [DateTimeOffset]) { $last.UtcDateTime.ToString('O') }
        else { [DateTimeOffset]::Parse([string]$last).UtcDateTime.ToString('O') }
    [ordered]@{ Id=$Item.Id; SeriesId=$Item.SeriesId; Season=$Item.ParentIndexNumber; Episode=$Item.IndexNumber;
        Played=$Item.UserData.Played; PositionTicks=[long]$Item.UserData.PlaybackPositionTicks;
        PlayCount=$Item.UserData.PlayCount; IsFavorite=$Item.UserData.IsFavorite; LastPlayedAtUtc=$lastUtc }
}

function Read-Episodes {
    $result = Invoke-Fixture GET "/emby/Shows/2000/Episodes?UserId=$script:userId"
    if ($result.Items.Count -ne 12) { throw 'The intended series no longer has its expected twelve episodes.' }
    $map = @{}
    foreach ($item in $result.Items) { $map[$item.Id] = $item }
    $map
}

try {
    $stats = Invoke-Fixture GET '/_fixture/stats'
    $public = Invoke-Fixture GET '/emby/System/Info/Public'
    if ($stats.Synthetic -ne $true -or $stats.LumenCatalog -ne $true -or
        $public.Id -ne 'synthetic-lumen-server-0001' -or $public.Version -ne 'synthetic-1.0' -or
        $public.ServerName -ne 'SYNTHETIC Lumen Library') { throw 'The synthetic fixture allowlist did not match.' }
    $targetEventsBefore = @($stats.Events | Where-Object ItemId -in @('2115', '2116')) | ConvertTo-Json -Depth 8 -Compress
    Start-Sleep -Milliseconds 800
    $settledStats = Invoke-Fixture GET '/_fixture/stats'
    $targetEventsSettled = @($settledStats.Events | Where-Object ItemId -in @('2115', '2116')) | ConvertTo-Json -Depth 8 -Compress
    if ($targetEventsBefore -ne $targetEventsSettled -or (Get-Process -Id $ClosedAppProcessId -ErrorAction SilentlyContinue)) {
        throw 'The target series still received reports after close. No fixture state was changed.'
    }
    $phase = 'AuthenticateExistingSyntheticAccount'
    $authentication = Invoke-Fixture POST '/emby/Users/AuthenticateByName' @{ Username='demo'; Pw='demo' }
    if ($authentication.ServerId -ne $public.Id -or $authentication.User.Id -ne $userId -or
        [string]::IsNullOrWhiteSpace($authentication.AccessToken)) { throw 'The fixed synthetic account identity did not match.' }
    $headers['X-Emby-Token'] = $authentication.AccessToken
    $authentication = $null
    $authenticated = $true
    $current = Invoke-Fixture GET "/emby/Users/$userId"
    if ($current.Id -ne $userId -or $current.ServerId -ne $public.Id) { throw 'The authenticated fixture user did not match.' }
    $before = Read-Episodes
    $unchangedIds = @('2101','2102','2103','2104','2105','2106','2111','2112','2113','2114')
    foreach ($id in $unchangedIds) {
        if ($before[$id].UserData.Played -ne $true) { throw 'A preceding episode is unexpectedly unplayed. It was not modified.' }
    }
    if ($before['2115'].ParentIndexNumber -ne 2 -or $before['2115'].IndexNumber -ne 5 -or
        $before['2116'].ParentIndexNumber -ne 2 -or $before['2116'].IndexNumber -ne 6) { throw 'The target episode identities did not match.' }
    $phase = 'RestoreTwoOwnedEpisodes'
    Invoke-Fixture DELETE "/emby/Users/$userId/PlayedItems/2115" | Out-Null
    Invoke-Fixture DELETE "/emby/Users/$userId/PlayedItems/2116" | Out-Null
    Invoke-Fixture POST "/emby/Users/$userId/Items/2115/HideFromResume?Hide=false" | Out-Null
    $info = Invoke-Fixture POST '/emby/Items/2115/PlaybackInfo' @{ UserId=$userId; IsPlayback=$false; EnableDirectStream=$true; MediaSourceId='synthetic-mp4' }
    if ([string]::IsNullOrWhiteSpace($info.PlaySessionId) -or $info.MediaSources.Count -ne 1 -or
        $info.MediaSources[0].Id -ne 'synthetic-mp4') { throw 'The synthetic state-preparation session was not issued.' }
    $sessionId = $info.PlaySessionId
    $report = @{ ItemId='2115'; PlaySessionId=$sessionId; MediaSourceId='synthetic-mp4';
        PositionTicks=312000000L; PlayMethod='DirectStream'; IsPaused=$true; EventName='TimeUpdate' }
    Invoke-Fixture POST '/emby/Sessions/Playing' $report 204 | Out-Null
    Invoke-Fixture POST '/emby/Sessions/Playing/Progress' $report 204 | Out-Null
    $stop = @{ ItemId='2115'; PlaySessionId=$sessionId; MediaSourceId='synthetic-mp4'; PositionTicks=312000000L; Failed=$false }
    Invoke-Fixture POST '/emby/Sessions/Playing/Stopped' $stop 204 | Out-Null
    $phase = 'VerifyStateAndQueries'
    $after = Read-Episodes
    $resume = Invoke-Fixture GET "/emby/Users/$userId/Items/Resume?SeriesId=2000&ParentId=2000&Recursive=true&MediaTypes=Video&IncludeItemTypes=Episode&Limit=20"
    $next = Invoke-Fixture GET "/emby/Shows/NextUp?UserId=$userId&SeriesId=2000&Limit=20"
    $one = Episode-State $after['2115']
    $two = Episode-State $after['2116']
    if ($one.Played -ne $false -or $one.PositionTicks -ne 312000000L -or
        $two.Played -ne $false -or $two.PositionTicks -ne 0 -or
        $resume.Items.Count -ne 1 -or $resume.Items[0].Id -ne '2115' -or
        $next.Items.Count -ne 1 -or $next.Items[0].Id -ne '2115' -or
        [DateTimeOffset]::Parse([string]$one.LastPlayedAtUtc) -lt $startedAt) { throw 'The restored progress or authoritative queries did not match.' }
    foreach ($id in $unchangedIds) {
        if (($before[$id].UserData | ConvertTo-Json -Depth 8 -Compress) -ne ($after[$id].UserData | ConvertTo-Json -Depth 8 -Compress)) {
            throw 'An unrelated preceding episode changed during preparation.'
        }
    }
    $finalStats = Invoke-Fixture GET '/_fixture/stats'
    $receipt = [ordered]@{ Scope='OwnedSyntheticEpisodeStatePreparationOnly'; Outcome='Passed';
        ClosedAppProcessId=$ClosedAppProcessId; StartedAtUtc=$startedAt.ToString('O'); CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
        ServerId=$public.Id; UserId=$userId; Before=@((Episode-State $before['2115']),(Episode-State $before['2116']));
        After=@($one,$two); ResumeQuery='SeriesId=2000, ParentId=2000, MediaTypes=Video'; ResumeIds=@($resume.Items.Id);
        NextUpIds=@($next.Items.Id); UnrelatedEpisodeIds=$unchangedIds; UnrelatedFullUserDataUnchanged=$true;
        SeedReportSessionId=$sessionId; SeedReportKinds=@('Start','Progress','Stop'); MediaBytesRequested=0; NativeDecoderRun=$false;
        RequestCount=$requestCount; ProtocolStatsAfter=@{ StartCount=$finalStats.StartCount; ProgressCount=$finalStats.ProgressCount; StopCount=$finalStats.StopCount } }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $receiptRoot 'receipt.json') -Encoding utf8
    [pscustomobject]@{ Outcome='Passed'; Receipt=(Join-Path $receiptRoot 'receipt.json'); ResumeItemId='2115';
        ResumePositionTicks=312000000L; NextUpItemId='2115'; UnrelatedEpisodesPreserved=10; NativeDecoderRun=$false }
}
catch {
    [ordered]@{ Scope='OwnedSyntheticEpisodeStatePreparationOnly'; Outcome='Failed'; Phase=$phase;
        RecordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); ErrorCategory=$_.Exception.GetType().Name;
        RequestCount=$requestCount; NoExceptionPayloadRecorded=$true } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $receiptRoot 'failed.json') -Encoding utf8
    throw 'The bounded fixture state preparation failed. Its credential-free phase receipt was retained.'
}
finally {
    if ($authenticated) {
        try { Invoke-Fixture POST '/emby/Sessions/Logout' $null 204 | Out-Null }
        catch { Write-Warning 'The new preparation token revocation was not confirmed; no token was printed.' }
    }
    $headers.Remove('X-Emby-Token')
}
