#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^/tmp/emby-client-validation-[a-zA-Z0-9-]+/media$')]
    [string] $MediaDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serverUrl = 'http://127.0.0.1:19096'
$apiRoot = "$serverUrl/emby"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/emby-validation'))
$headers = @{ 'X-Emby-Authorization' = 'MediaBrowser Client="Emby Client Server Setup", Device="Isolated validation", DeviceId="official-validation-setup", Version="1.0.0"' }

function Invoke-EmbyJson {
    param([string] $Method, [string] $Path, [object] $Body)

    $parameters = @{
        Method = $Method
        Uri = "$apiRoot/$Path"
        Headers = $headers
        TimeoutSec = 30
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
    }
    Invoke-RestMethod @parameters
}

$publicInfo = Invoke-EmbyJson -Method Get -Path 'System/Info/Public'
$runtimeDirectory = $MediaDirectory.Substring(0, $MediaDirectory.Length - '/media'.Length)
$runId = $runtimeDirectory.Substring('/tmp/emby-client-validation-'.Length)
$namespaceState = (wsl --distribution Debian --exec cat "$runtimeDirectory/state.json") | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw 'The owned namespace state could not be read.'
}
[xml] $initialConfiguration = (wsl --distribution Debian --exec cat "$runtimeDirectory/programdata/config/system.xml") -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw 'The owned server configuration could not be read.'
}
if ($publicInfo.Version -ne '4.9.5.0' -or
    $publicInfo.ServerName -ne "Official Emby validation $runId" -or
    $namespaceState.status -ne 'ready' -or $namespaceState.mode -ne 'server' -or
    $namespaceState.outer.bound_endpoint.address -ne '127.0.0.1' -or
    $namespaceState.outer.bound_endpoint.port -ne 19096 -or
    $namespaceState.inner.server_network_namespace -ne $namespaceState.inner.network.network_namespace -or
    $namespaceState.inner.network.network_namespace -eq $namespaceState.outer.network_namespace -or
    $initialConfiguration.ServerConfiguration.IsStartupWizardCompleted -ne 'false') {
    throw 'Only a fresh official Emby Server 4.9.5.0 validation instance may be initialized.'
}
$adminCredentialPath = Join-Path $artifactRoot 'official-admin-credentials.json'
$userCredentialPath = Join-Path $artifactRoot 'official-user-credentials.json'
if ((Test-Path -LiteralPath $adminCredentialPath) -or (Test-Path -LiteralPath $userCredentialPath)) {
    throw 'Existing validation credential files must be reviewed before initializing a different server.'
}

$adminPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$userPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
[ordered]@{ ServerUrl = $serverUrl; Username = 'validation-admin'; Password = $adminPassword } |
    ConvertTo-Json | Set-Content -LiteralPath $adminCredentialPath -Encoding utf8
[ordered]@{ ServerUrl = $serverUrl; Username = 'validation-user'; Password = $userPassword } |
    ConvertTo-Json | Set-Content -LiteralPath $userCredentialPath -Encoding utf8

$null = Invoke-EmbyJson -Method Post -Path 'Startup/Configuration' -Body @{ UICulture = 'en-US' }
$null = Invoke-EmbyJson -Method Post -Path 'Startup/User' -Body @{ Name = 'validation-admin'; Password = $adminPassword }
$null = Invoke-EmbyJson -Method Post -Path 'Startup/RemoteAccess' -Body @{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false }
$null = Invoke-EmbyJson -Method Post -Path 'Startup/Complete'
$authentication = Invoke-EmbyJson -Method Post -Path 'Users/AuthenticateByName' -Body @{ Username = 'validation-admin'; Pw = $adminPassword }
$headers['X-Emby-Token'] = $authentication.AccessToken

$configuration = Invoke-EmbyJson -Method Get -Path 'System/Configuration'
$configuration.EnableUPnP = $false
$configuration.EnableRemoteAccess = $false
$configuration.EnableAutoUpdate = $false
$null = Invoke-EmbyJson -Method Post -Path 'System/Configuration' -Body $configuration

$user = Invoke-EmbyJson -Method Post -Path 'Users/New' -Body @{ Name = 'validation-user' }
$null = Invoke-EmbyJson -Method Post -Path "Users/$($user.Id)/Password" -Body @{ Id = $user.Id; NewPw = $userPassword; ResetPassword = $false }
$libraryOptions = @{
    PathInfos = @(@{ Path = $MediaDirectory })
    ContentType = 'movies'
    EnableRealtimeMonitor = $false
    EnableChapterImageExtraction = $false
    ExtractChapterImagesDuringLibraryScan = $false
    EnableMarkerDetectionDuringLibraryScan = $false
    EnableInternetProviders = $false
    DownloadImagesInAdvance = $false
    SaveLocalMetadata = $false
    MetadataSavers = @()
    SubtitleDownloadLanguages = @()
    TypeOptions = @(@{
        Type = 'Movie'
        MetadataFetchers = @()
        MetadataFetcherOrder = @()
        ImageFetchers = @()
        ImageFetcherOrder = @()
    })
}
$null = Invoke-EmbyJson -Method Post -Path 'Library/VirtualFolders?name=Official%20Validation&collectionType=movies&refreshLibrary=true' -Body @{ LibraryOptions = $libraryOptions }
$null = Invoke-EmbyJson -Method Post -Path 'Sessions/Logout'
$headers.Remove('X-Emby-Token')
$authentication = $null
$adminPassword = $null
$userPassword = $null

Write-Output 'Initialized a temporary official server with separate administrator and playback users.'
Write-Output 'Added the synthetic movie directory and requested a library scan. Wait for scan completion before running ApiProbe.'
Write-Output "Playback credentials file: $userCredentialPath"
