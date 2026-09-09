#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$downloadRoot = Join-Path $artifactRoot 'downloads'
$seConvRoot = Join-Path $artifactRoot 'seconv-5.1.0'
$fontRoot = Join-Path $artifactRoot 'font'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$embyRoot = Join-Path $repositoryRoot 'artifacts/emby-validation/portable-4.9.5.0/system'
$embyToolHashes = @{
    'ffmpeg.exe' = '969535ce1e41c35b93f62104b9099aa4b2fc803ebcfd7f2eb15c80d7e36340cf'
    'ffprobe.exe' = '3d649e06a1979491d64ab91bc60d19a19ecb423792439700a409d2b09a0d87c2'
}
$seConvUrl = 'https://github.com/SubtitleEdit/subtitleedit/releases/download/v5.1.0/SeConv-Windows-x64.zip'
$seConvSha256 = 'ce081a6c6844d44cb1373ee501a963c9e37c788bf757c2fa0b39821ef9969839'
$fontCommit = '6ce172f74aa355ea43eb964fa4a91570a4d3064d'
$fontBaseUrl = "https://raw.githubusercontent.com/google/fonts/$fontCommit/ofl/bungeeshade"

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-GitBlobSha1([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $prefix = [Text.Encoding]::UTF8.GetBytes("blob $($bytes.Length)`0")
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $hash.AppendData($prefix)
        $hash.AppendData($bytes)
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

function Get-PinnedFile([string] $Url, [string] $Path, [string] $Sha256, [string] $GitBlobSha1) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).download"
        Invoke-WebRequest -Uri $Url -OutFile $temporary
        if ($Sha256 -and (Get-Sha256 $temporary) -ne $Sha256) { throw 'Downloaded asset SHA-256 mismatch.' }
        if ($GitBlobSha1 -and (Get-GitBlobSha1 $temporary) -ne $GitBlobSha1) { throw 'Downloaded Git blob identity mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $Path
    }
    if ($Sha256 -and (Get-Sha256 $Path) -ne $Sha256) { throw 'Cached asset SHA-256 mismatch.' }
    if ($GitBlobSha1 -and (Get-GitBlobSha1 $Path) -ne $GitBlobSha1) { throw 'Cached Git blob identity mismatch.' }
}

New-Item -ItemType Directory -Path $downloadRoot, $fontRoot -Force | Out-Null
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/SubtitleEdit/subtitleedit/releases/tags/v5.1.0'
$asset = @($release.assets | Where-Object { $_.name -eq 'SeConv-Windows-x64.zip' })
if ($asset.Count -ne 1 -or $asset[0].digest -ne "sha256:$seConvSha256" -or $asset[0].browser_download_url -ne $seConvUrl) {
    throw 'The official fixed-version release metadata does not match the pinned asset.'
}
$zipPath = Join-Path $downloadRoot 'SeConv-Windows-x64-v5.1.0.zip'
Get-PinnedFile -Url $seConvUrl -Path $zipPath -Sha256 $seConvSha256
if (-not (Test-Path -LiteralPath $seConvRoot)) {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $seConvRoot
}
$packageFiles = [Collections.Generic.List[object]]::new()
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $extractedPath = [IO.Path]::GetFullPath((Join-Path $seConvRoot $entry.FullName))
        if (-not $extractedPath.StartsWith([IO.Path]::GetFullPath($seConvRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A release archive entry escapes the dedicated dependency directory.'
        }
        $entryStream = $entry.Open()
        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant() }
        finally { $entryStream.Dispose() }
        if ((Get-Sha256 $extractedPath) -ne $entryHash) { throw 'An extracted dependency differs from the verified release archive.' }
        $packageFiles.Add([ordered]@{ RelativePath = $entry.FullName; Path = $extractedPath; Sha256 = $entryHash })
    }
}
finally { $archive.Dispose() }
$seConv = @(Get-ChildItem -LiteralPath $seConvRoot -Recurse -File | Where-Object { $_.Name -ieq 'seconv.exe' })
if ($seConv.Count -ne 1) { throw 'The extracted fixed package must contain exactly one seconv.exe.' }
$fontPath = Join-Path $fontRoot 'BungeeShade-Regular.ttf'
$licensePath = Join-Path $fontRoot 'OFL.txt'
Get-PinnedFile -Url "$fontBaseUrl/BungeeShade-Regular.ttf" -Path $fontPath -GitBlobSha1 '80ada80a0404713e7cd31a6b7982ff7a549acdfe'
Get-PinnedFile -Url "$fontBaseUrl/OFL.txt" -Path $licensePath -GitBlobSha1 '6f47072f01b8698301896b247b7087c023a4da2d'
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $toolPath = Join-Path $embyRoot $name
    if (-not (Test-Path -LiteralPath $toolPath)) { throw 'The previously verified official Windows Emby portable media tools are required.' }
    if ((Get-Sha256 $toolPath) -ne $embyToolHashes[$name]) {
        throw 'An existing Emby media-tool binary does not match the fixed official hash recorded in SOURCES.md.'
    }
}
$embyArchive = Join-Path $repositoryRoot 'artifacts/emby-validation/downloads/embyserver-win-x64-4.9.5.0.7z'
if ((Get-Sha256 $embyArchive) -ne '6883356517a42316ef5b270081f81d8de4151fec384fbe1ec1b31d2cf77688f6') {
    throw 'The reused official Windows Emby archive SHA-256 does not match its recorded release.'
}
$manifest = [ordered]@{
    FormatVersion = 1
    PreparedUtc = [DateTime]::UtcNow.ToString('O')
    SeConv = [ordered]@{
        Version = '5.1.0'
        Url = $seConvUrl
        ReleaseAssetSha256 = $seConvSha256
        ExecutablePath = $seConv[0].FullName
        ExecutableSha256 = Get-Sha256 $seConv[0].FullName
        PackageFiles = @($packageFiles.ToArray())
        LicenseFiles = @((Get-ChildItem -LiteralPath $seConvRoot -Recurse -File | Where-Object { $_.Name -match '(?i)license|copying|notice' }).FullName)
    }
    Font = [ordered]@{
        Family = 'Bungee Shade'
        SourceCommit = $fontCommit
        Url = "$fontBaseUrl/BungeeShade-Regular.ttf"
        Path = $fontPath
        Sha256 = Get-Sha256 $fontPath
        GitBlobSha1 = '80ada80a0404713e7cd31a6b7982ff7a549acdfe'
        License = 'SIL Open Font License 1.1'
        LicensePath = $licensePath
        LicenseSha256 = Get-Sha256 $licensePath
    }
    EmbyMediaTools = [ordered]@{
        ServerVersion = '4.9.5.0'
        ArchiveSha256 = '6883356517a42316ef5b270081f81d8de4151fec384fbe1ec1b31d2cf77688f6'
        FfmpegPath = Join-Path $embyRoot 'ffmpeg.exe'
        FfmpegSha256 = Get-Sha256 (Join-Path $embyRoot 'ffmpeg.exe')
        FfprobePath = Join-Path $embyRoot 'ffprobe.exe'
        FfprobeSha256 = Get-Sha256 (Join-Path $embyRoot 'ffprobe.exe')
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifactRoot 'dependencies.json') -Encoding utf8
Write-Output 'Prepared fixed official dependencies and recorded release/blob/file hashes. No executable, server, or font installer was run.'
