param(
    [string] $DependencyRoot,
    [string] $NuGetRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (!$DependencyRoot) { $DependencyRoot = Join-Path $repositoryRoot 'artifacts/decoder-apis/dependencies' }
if (!$NuGetRoot) {
    $NuGetRoot = $env:NUGET_PACKAGES
    if (!$NuGetRoot) { $NuGetRoot = Join-Path $env:USERPROFILE '.nuget/packages' }
}
$DependencyRoot = [IO.Path]::GetFullPath($DependencyRoot)
$lock = Get-Content (Join-Path $repositoryRoot 'native/FFmpegInteropX/dependencies.lock.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $DependencyRoot -Force | Out-Null

function Get-LockedArchive([string] $Url, [string] $Path, [string] $Algorithm, [string] $ExpectedHash) {
    if (!(Test-Path -LiteralPath $Path)) {
        $temporaryPath = $Path + '.download'
        Invoke-WebRequest -Uri $Url -OutFile $temporaryPath
        $actualHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm $Algorithm).Hash.ToLowerInvariant()
        if ($Algorithm -eq 'SHA512') {
            $actualHash = [Convert]::ToBase64String([Convert]::FromHexString($actualHash))
        }
        if ($actualHash -cne $ExpectedHash) {
            throw "Dependency hash mismatch for $Url. Expected $ExpectedHash; received $actualHash. The locked dependency must be deliberately updated."
        }
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
    if ($Algorithm -eq 'SHA512') { $actualHash = [Convert]::ToBase64String([Convert]::FromHexString($actualHash)) }
    if ($actualHash -cne $ExpectedHash) { throw "Cached dependency hash mismatch: $Path" }
}

$runtimeArchive = Join-Path $DependencyRoot $lock.ffmpeg.runtime.name
$developmentArchive = Join-Path $DependencyRoot $lock.ffmpeg.development.name
Get-LockedArchive $lock.ffmpeg.runtime.url $runtimeArchive 'SHA256' $lock.ffmpeg.runtime.sha256
Get-LockedArchive $lock.ffmpeg.development.url $developmentArchive 'SHA256' $lock.ffmpeg.development.sha256
if ((Get-Item -LiteralPath $runtimeArchive).Length -ne $lock.ffmpeg.runtime.size -or (Get-Item -LiteralPath $developmentArchive).Length -ne $lock.ffmpeg.development.size) { throw 'A locked FFmpeg archive has an unexpected size.' }
$dependencyFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lock.ffmpeg.runtime.sha256 + ':' + $lock.ffmpeg.development.sha256))).ToLowerInvariant()
$ffmpegRoot = Join-Path $DependencyRoot $lock.ffmpeg.payloadDirectory
$stampPath = Join-Path $ffmpegRoot 'archives.sha256'
if (!(Test-Path -LiteralPath $stampPath)) {
    $extractRoot = Join-Path $DependencyRoot ('extract-devenvy-' + $dependencyFingerprint.Substring(0, 12))
    $runtimeExtractRoot = Join-Path $extractRoot 'runtime'
    $developmentExtractRoot = Join-Path $extractRoot 'development'
    New-Item -ItemType Directory -Path $runtimeExtractRoot,$developmentExtractRoot -Force | Out-Null
    & tar.exe -xf $runtimeArchive -C $runtimeExtractRoot
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg runtime extraction failed.' }
    & tar.exe -xf $developmentArchive -C $developmentExtractRoot
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg development extraction failed.' }
    if (!(Test-Path (Join-Path $runtimeExtractRoot 'avcodec-62.dll')) -or !(Test-Path (Join-Path $developmentExtractRoot 'lib/avcodec.lib'))) {
        throw 'The locked FFmpeg archives do not contain the expected x64 runtime and MSVC development libraries.'
    }
    $binRoot = Join-Path $ffmpegRoot 'bin'
    New-Item -ItemType Directory -Path $ffmpegRoot,$binRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $runtimeExtractRoot -File | Where-Object Extension -in '.dll','.exe' | Copy-Item -Destination $binRoot -Force
    Copy-Item -LiteralPath (Join-Path $runtimeExtractRoot 'legal') -Destination $ffmpegRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $developmentExtractRoot 'include'),(Join-Path $developmentExtractRoot 'lib') -Destination $ffmpegRoot -Recurse -Force
    Set-Content -LiteralPath $stampPath -Value $dependencyFingerprint -NoNewline
}
if ((Get-Content -LiteralPath $stampPath -Raw).Trim() -cne $dependencyFingerprint) { throw 'The extracted FFmpeg payload does not match the archive pair.' }
foreach ($major in $lock.ffmpeg.expectedMajors.PSObject.Properties) {
    if (!(Test-Path (Join-Path $ffmpegRoot "bin/$($major.Name)-$($major.Value).dll"))) { throw "Missing locked FFmpeg component: $($major.Name)" }
}
if (!(Test-Path (Join-Path $ffmpegRoot 'include/libavcodec/avcodec.h'))) { throw 'FFmpeg development headers are missing.' }

foreach ($package in $lock.nuget) {
    $packageRoot = Join-Path $NuGetRoot "$($package.id)/$($package.version)"
    $packageArchive = Join-Path $packageRoot "$($package.id).$($package.version).nupkg"
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $url = "https://api.nuget.org/v3-flatcontainer/$($package.id)/$($package.version)/$($package.id).$($package.version).nupkg"
    Get-LockedArchive $url $packageArchive 'SHA512' $package.sha512
    if (!(Test-Path (Join-Path $packageRoot "$($package.id).nuspec"))) {
        [IO.Compression.ZipFile]::ExtractToDirectory($packageArchive, $packageRoot, $true)
    }
}

[pscustomobject]@{ DependencyRoot = $DependencyRoot; FFmpegRoot = $ffmpegRoot; NuGetRoot = [IO.Path]::GetFullPath($NuGetRoot); ArchiveSha256 = $lock.ffmpeg.runtime.sha256; DevelopmentArchiveSha256 = $lock.ffmpeg.development.sha256; DependencyFingerprint = $dependencyFingerprint }
