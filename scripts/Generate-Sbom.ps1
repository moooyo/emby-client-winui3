#Requires -Version 7.0
param(
    [string]$OutputPath,
    [string]$AssetsPath,
    [string]$LockPath
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $AssetsPath) { $AssetsPath = Join-Path $workspace 'src/EmbyClient.App/obj/project.assets.json' }
if (-not $LockPath) { $LockPath = Join-Path $workspace 'src/EmbyClient.App/packages.lock.json' }
if (-not $OutputPath) { $OutputPath = Join-Path $workspace 'artifacts/sbom/emby-client-win-x64.cdx.json' }
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $workspace $OutputPath }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
if (-not $OutputPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($OutputPath) -ne '.json') {
    throw 'The output must be a JSON file below the repository artifacts directory.'
}
$ancestor = Split-Path -Parent $OutputPath
while ($ancestor -and $ancestor.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Reparse points are not accepted in the artifact output path.'
    }
    $ancestor = Split-Path -Parent $ancestor
}
if ((Test-Path -LiteralPath $OutputPath) -and ((Get-Item -LiteralPath $OutputPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The output file must not be a reparse point.'
}

$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -AsHashtable
if ($assets.project.restore.projectName -ne 'EmbyClient.App') { throw 'The restored assets must belong to EmbyClient.App, not a test or probe project.' }
$targetKey = @($assets.targets.Keys | Where-Object { $_ -match '^net10\.0-windows[^/]+/win-x64$' })
if ($targetKey.Count -ne 1) { throw 'Exactly one restored .NET 10 Windows x64 asset target is required.' }
$target = $assets.targets[$targetKey[0]]
$frameworkKey = $targetKey[0].Split('/')[0]
$framework = $assets.project.frameworks[$frameworkKey]
if (-not $framework) { throw 'The matching restored framework metadata is missing.' }
$lockVersions = @{}
foreach ($frameworkLock in $lock.dependencies.Values) {
    foreach ($entry in $frameworkLock.GetEnumerator()) {
        if ($entry.Value.resolved) { $lockVersions[$entry.Key + '/' + $entry.Value.resolved] = $entry.Value }
    }
}
$buildOnly = @('Microsoft.DotNet.ILCompiler', 'runtime.win-x64.Microsoft.DotNet.ILCompiler', 'Microsoft.NET.ILLink.Tasks', 'Microsoft.Windows.SDK.BuildTools', 'Microsoft.Windows.SDK.BuildTools.MSIX')
$selected = @{}
foreach ($entry in $target.GetEnumerator()) {
    if ($entry.Value.type -eq 'package' -and $entry.Key.Split('/')[0] -notin $buildOnly) {
        if (-not $lockVersions.ContainsKey($entry.Key)) { throw "The app lock file does not match the resolved asset: $($entry.Key)" }
        $selected[$entry.Key] = $entry.Value
    }
}
$runtimePacks = @('Microsoft.NETCore.App.Runtime.NativeAOT.win-x64', 'Microsoft.Windows.SDK.NET.Ref')
foreach ($packId in $runtimePacks) {
    $download = @($framework.downloadDependencies | Where-Object name -eq $packId)
    if ($download.Count -ne 1 -or $download[0].version -notmatch '^\[([^,\]]+)(?:,\s*\1)?\]$') {
        throw "An exact restored framework download version is required for $packId."
    }
    $selected[$packId + '/' + $Matches[1]] = @{ type = 'package'; frameworkPack = $true }
}

$components = @()
$refsById = @{}
foreach ($key in ($selected.Keys | Sort-Object)) {
    $id, $version = $key.Split('/')
    $relativePackagePath = $id.ToLowerInvariant() + '/' + $version.ToLowerInvariant()
    $packageDirectory = $null
    foreach ($folder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $folder $relativePackagePath
        if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
    }
    if (-not $packageDirectory) { throw "The restored package cache is missing $key." }
    $nuspecPath = Join-Path $packageDirectory ($id.ToLowerInvariant() + '.nuspec')
    $archivePath = Join-Path $packageDirectory ($id.ToLowerInvariant() + '.' + $version.ToLowerInvariant() + '.nupkg')
    if (-not (Test-Path -LiteralPath $archivePath)) { throw "The actual NuGet archive is required to hash $key." }
    [xml]$spec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $spec.package.metadata
    if ([string]$metadata.id -ne $id -or [string]$metadata.version -ne $version) { throw "NuGet metadata disagrees with the resolved identity: $key." }
    $purl = 'pkg:nuget/' + [Uri]::EscapeDataString($id) + '@' + [Uri]::EscapeDataString($version)
    $refsById[$id] = $purl
    $properties = @(
        @{ name = 'emby:inventory:level'; value = 'resolved-package' },
        @{ name = 'emby:nuget:nuspecSha256'; value = (Get-FileHash -LiteralPath $nuspecPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
    if ($selected[$key].frameworkPack) { $properties += @{ name = 'emby:dependency:origin'; value = 'framework-download' } }
    else {
        $properties += @{ name = 'emby:dependency:origin'; value = 'app-assets-and-lock' }
        $properties += @{ name = 'emby:nuget:lockContentHash'; value = [string]$lockVersions[$key].contentHash }
    }
    $licenseType = [string]$metadata.license.type
    $licenseValue = [string]$metadata.license.InnerText
    $licenses = @()
    if ($licenseType -eq 'expression') { $licenses = @(@{ expression = $licenseValue }) }
    elseif ($licenseType -eq 'file') {
        $licenses = @(@{ license = @{ name = 'NuGet license file: ' + $licenseValue; url = 'https://www.nuget.org/packages/' + $id + '/' + $version + '/license' } })
    }
    elseif ($metadata.licenseUrl) {
        $licenseType = 'url'
        $licenseValue = [string]$metadata.licenseUrl
        $licenses = @(@{ license = @{ name = 'License referenced by NuGet metadata'; url = $licenseValue } })
    }
    if ($licenseType) { $properties += @{ name = 'emby:nuget:licenseType'; value = $licenseType }; $properties += @{ name = 'emby:nuget:licenseValue'; value = $licenseValue } }
    $external = @(@{ type = 'distribution'; url = 'https://api.nuget.org/v3-flatcontainer/' + $relativePackagePath + '/' + $id.ToLowerInvariant() + '.' + $version.ToLowerInvariant() + '.nupkg' })
    if ($metadata.repository.url) { $external += @{ type = 'vcs'; url = [string]$metadata.repository.url } }
    elseif ($metadata.projectUrl) { $external += @{ type = 'website'; url = [string]$metadata.projectUrl } }
    if ($metadata.repository.commit) { $properties += @{ name = 'emby:nuget:repositoryCommit'; value = [string]$metadata.repository.commit } }
    $component = [ordered]@{
        type = 'library'; 'bom-ref' = $purl; name = $id; version = $version; purl = $purl
        hashes = @(@{ alg = 'SHA-512'; content = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash.ToLowerInvariant() })
        externalReferences = $external; properties = $properties
    }
    if ($licenses.Count -gt 0) { $component.licenses = $licenses }
    $components += $component
}

$rootRef = 'urn:emby-client:app'
$dependencies = @()
$rootDependencies = @($framework.dependencies.Keys | Where-Object { $refsById.ContainsKey($_) } | ForEach-Object { $refsById[$_] })
$rootDependencies += @($runtimePacks | ForEach-Object { $refsById[$_] })
$dependencies += @{ ref = $rootRef; dependsOn = @($rootDependencies | Sort-Object -Unique) }
foreach ($key in ($selected.Keys | Sort-Object)) {
    $id = $key.Split('/')[0]
    $children = @($selected[$key].dependencies.Keys | Where-Object { $_ -and $refsById.ContainsKey($_) } | ForEach-Object { $refsById[$_] } | Sort-Object -Unique)
    $dependencies += @{ ref = $refsById[$id]; dependsOn = $children }
}

# Record the actual native overrides separately from the restored NuGet package graph.
$decoderLockPath = Join-Path $workspace 'native/FFmpegInteropX/dependencies.lock.json'
if (Test-Path -LiteralPath $decoderLockPath) {
    $nativeReceiptPath = Join-Path $workspace 'artifacts/decoder-apis/native/build-receipt.json'
    if (-not (Test-Path -LiteralPath $nativeReceiptPath)) { throw 'Build the native decoder before generating its runtime inventory.' }
    $decoderLock = Get-Content -LiteralPath $decoderLockPath -Raw | ConvertFrom-Json -AsHashtable
    $nativeReceipt = Get-Content -LiteralPath $nativeReceiptPath -Raw | ConvertFrom-Json -AsHashtable
    $nativeRuntimeRoot = Join-Path $workspace 'artifacts/decoder-apis/native/runtime'
    $wrapperFile = @($nativeReceipt.runtimeFiles | Where-Object name -eq 'FFmpegInteropX.dll')
    if ($wrapperFile.Count -ne 1) { throw 'Exactly one custom decoder wrapper is required.' }
    $wrapperRef = 'urn:emby-client:native-decoder:' + $nativeReceipt.sourceFingerprint
    $ffmpegRef = 'urn:emby-client:ffmpeg-runtime:' + $decoderLock.ffmpeg.runtime.sha256
    $components += [ordered]@{
        type = 'library'; 'bom-ref' = $wrapperRef; name = 'FFmpegInteropX.EmbyDecoderApis'; version = '2.1.0+emby-api1'
        hashes = @(@{ alg = 'SHA-256'; content = $wrapperFile[0].sha256 })
        licenses = @(@{ expression = 'Apache-2.0' })
        externalReferences = @(@{ type = 'vcs'; url = $decoderLock.wrapper.repository + '/tree/' + $decoderLock.wrapper.commit })
        properties = @(
            @{ name = 'emby:native:sourceFingerprint'; value = $nativeReceipt.sourceFingerprint },
            @{ name = 'emby:native:sourceChanges'; value = 'Repository native/FFmpegInteropX/Source with session-scoped decoder API extensions.' },
            @{ name = 'emby:native:buildReceiptSha256'; value = (Get-FileHash -LiteralPath $nativeReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        )
    }
    $components += [ordered]@{
        type = 'library'; 'bom-ref' = $ffmpegRef; name = 'FFmpeg.Devenvy.SharedRuntime'; version = $decoderLock.ffmpeg.runtimeVersion
        hashes = @(@{ alg = 'SHA-256'; content = $decoderLock.ffmpeg.runtime.sha256 })
        licenses = @(@{ expression = $decoderLock.ffmpeg.license })
        externalReferences = @(
            @{ type = 'distribution'; url = $decoderLock.ffmpeg.runtime.url },
            @{ type = 'vcs'; url = $decoderLock.ffmpeg.repository + '/tree/' + $decoderLock.ffmpeg.buildScriptsCommit }
        )
        properties = @(
            @{ name = 'emby:native:developmentArchiveSha256'; value = $decoderLock.ffmpeg.development.sha256 },
            @{ name = 'emby:native:buildRelease'; value = $decoderLock.ffmpeg.release },
            @{ name = 'emby:native:licenseScope'; value = 'FFmpeg library license; bundled external dependency notices and exceptions remain in the retained supplier legal tree.' }
        )
    }
    $ffmpegParts = @()
    foreach ($file in $nativeReceipt.runtimeFiles | Where-Object name -like '*.dll') {
        if ([IO.Path]::GetFileName($file.name) -ne $file.name) { throw 'Native inventory file names must be basenames.' }
        $actualFile = Join-Path $nativeRuntimeRoot $file.name
        if (-not (Test-Path -LiteralPath $actualFile) -or (Get-FileHash -LiteralPath $actualFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "Native runtime hash does not match the build receipt: $($file.name)"
        }
        if ($file.name -eq 'FFmpegInteropX.dll') { continue }
        $partRef = 'urn:emby-client:native-file:' + $file.sha256
        $ffmpegParts += $partRef
        $components += [ordered]@{
            type = 'file'; 'bom-ref' = $partRef; name = $file.name
            hashes = @(@{ alg = 'SHA-256'; content = $file.sha256 })
            properties = @(@{ name = 'emby:native:bytes'; value = [string]$file.size })
        }
        $dependencies += @{ ref = $partRef; dependsOn = @() }
    }
    if ($ffmpegParts.Count -ne 7) { throw 'The locked FFmpeg runtime must contain exactly seven shared library components.' }
    $dependencies += @{ ref = $ffmpegRef; dependsOn = $ffmpegParts }
    $dependencies += @{ ref = $wrapperRef; dependsOn = @($ffmpegRef) }
    $dependencies[0].dependsOn += @($wrapperRef, $ffmpegRef)
    foreach ($component in $components | Where-Object name -eq 'FFmpegInteropX.Desktop.FFmpeg') {
        $component.properties += @{ name = 'emby:native:runtimePayloadReplacedBy'; value = $ffmpegRef }
    }
    foreach ($component in $components | Where-Object name -eq 'FFmpegInteropX.Desktop.Lib') {
        $component.properties += @{ name = 'emby:native:wrapperPayloadReplacedBy'; value = $wrapperRef }
    }
}
[xml]$project = Get-Content (Join-Path $workspace 'src/EmbyClient.App/EmbyClient.App.csproj') -Raw
$appVersion = [string]@($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$assetsHash = (Get-FileHash -LiteralPath $AssetsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$lockHash = (Get-FileHash -LiteralPath $LockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$bom = [ordered]@{
    '$schema' = 'http://cyclonedx.org/schema/bom-1.6.schema.json'
    bomFormat = 'CycloneDX'; specVersion = '1.6'; version = 1
    metadata = @{
        component = @{ type = 'application'; 'bom-ref' = $rootRef; name = 'EmbyClient.App'; version = $appVersion }
        properties = @(
            @{ name = 'emby:inventory:scope'; value = 'Resolved App package graph, selected runtime packs, and hash-verified native decoder overrides. External dependency notices within the FFmpeg runtime remain a separate supplier inventory.' },
            @{ name = 'emby:inventory:target'; value = $targetKey[0] },
            @{ name = 'emby:inventory:assetsSha256'; value = $assetsHash },
            @{ name = 'emby:inventory:lockSha256'; value = $lockHash },
            @{ name = 'emby:inventory:excludedBuildPackages'; value = ($buildOnly -join ';') },
            @{ name = 'emby:inventory:hashMeaning'; value = 'Component SHA-512 hashes identify actual cached nupkg archive bytes. NuGet lock content hashes are separately preserved metadata and are not treated as archive hashes.' }
        )
    }
    components = $components; dependencies = $dependencies
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
function ConvertTo-CanonicalValue([object]$Value) {
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in ($Value.Keys | Sort-Object)) { $ordered[$key] = ConvertTo-CanonicalValue $Value[$key] }
        return ,$ordered
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @(foreach ($item in $Value) { ConvertTo-CanonicalValue $item })
        return ,$items
    }
    return ,$Value
}
$json = ConvertTo-CanonicalValue $bom | ConvertTo-Json -Depth 30
# Round-trip before publishing the package inventory. Schema validation is separate.
$readback = $json | ConvertFrom-Json -AsHashtable
if ($readback.components.Count -ne $components.Count -or $readback.specVersion -ne '1.6') { throw 'The SBOM JSON readback did not preserve the expected structure.' }
[IO.File]::WriteAllText($OutputPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "CycloneDX 1.6 package SBOM: $OutputPath"
Write-Output "Components: $($components.Count). JSON structure readback passed; schema validation was not run by this script."
