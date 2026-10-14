#requires -Version 7.0

<#
.SYNOPSIS
Creates a complete unpackaged Windows x64 release ZIP and SHA256 sidecar from an existing Native AOT publish.
.DESCRIPTION
Does not build, publish to GitHub, sign, launch, install, register, or change certificate trust.
The source publish directory is read-only. Generated files remain under the repository artifacts directory.
#>
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$Version,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseWorkspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseArtifactRoot = [IO.Path]::GetFullPath((Join-Path $releaseWorkspace 'artifacts'))
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $releaseArtifactRoot 'aot' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $releaseArtifactRoot 'releases' }
if (-not $Version) {
    [xml]$releaseProject = Get-Content -LiteralPath (Join-Path $releaseWorkspace 'src/EmbyClient.App/EmbyClient.App.csproj') -Raw
    $Version = [string]@($releaseProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Use a stable three- or four-part numeric release version.' }
$releaseVersion = [Version]::Parse($Version)
$Version = $releaseVersion.ToString()

function Resolve-ReleasePath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $releaseWorkspace $Path))
}

function Test-ReleaseChild([string]$Path, [string]$Root) {
    return $Path.StartsWith($Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Convert-ReleaseFourPart([Version]$Value) {
    return [Version]::new($Value.Major, $Value.Minor, [Math]::Max(0, $Value.Build), [Math]::Max(0, $Value.Revision))
}

function Assert-ReleaseOrdinaryPath([string]$Path) {
    $releaseAncestor = [IO.Path]::GetFullPath($Path)
    while ($releaseAncestor) {
        if (Test-Path -LiteralPath $releaseAncestor) {
            if (((Get-Item -LiteralPath $releaseAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release paths cannot traverse a reparse point: $releaseAncestor"
            }
        }
        $releaseAncestor = [IO.Path]::GetDirectoryName($releaseAncestor)
    }
}

function Assert-ReleaseArtifactPath([string]$Path) {
    if (-not (Test-ReleaseChild $Path $releaseArtifactRoot)) { throw "Generated files must be under $releaseArtifactRoot." }
    Assert-ReleaseOrdinaryPath $Path
}

function Assert-ReleaseRelativePath([string]$Path) {
    if (-not $Path -or $Path.Length -gt 240 -or $Path.Contains('\') -or $Path.StartsWith('/')) { throw 'A publish filename is unsafe for an update ZIP.' }
    foreach ($releasePart in $Path.Split('/')) {
        if (-not $releasePart -or $releasePart -in @('.', '..') -or $releasePart.EndsWith('.') -or $releasePart.EndsWith(' ')) {
            throw 'A publish filename is unsafe for Windows.'
        }
        foreach ($releaseCharacter in $releasePart.ToCharArray()) {
            if ([char]::IsControl($releaseCharacter) -or '<>:"|?*'.Contains([string]$releaseCharacter)) { throw 'A publish filename is unsafe for Windows.' }
        }
        $releaseStem = $releasePart.Split('.')[0].TrimEnd(' ').ToUpperInvariant()
        if ($releaseStem -in @('CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$') -or $releaseStem -match '^(COM|LPT)[1-9\u00B9\u00B2\u00B3]$') {
            throw 'A publish filename names a Windows device.'
        }
    }
}

function Get-ReleaseSourceFiles([string]$Root) {
    $releasePending = [Collections.Generic.Stack[string]]::new()
    $releasePending.Push($Root)
    $releaseEntryCount = 0
    while ($releasePending.Count -gt 0) {
        foreach ($releaseItem in Get-ChildItem -LiteralPath $releasePending.Pop() -Force) {
            $releaseEntryCount++
            if ($releaseEntryCount -ge 8192) { throw 'The publish contains too many files and directories for an update ZIP.' }
            if (($releaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The publish contains a reparse point.' }
            if ($releaseItem.PSIsContainer) { $releasePending.Push($releaseItem.FullName) }
            elseif ($IncludeSymbols -or $releaseItem.Extension -ne '.pdb') { Write-Output $releaseItem }
        }
    }
}

function Assert-ReleasePe([string]$Path, [bool]$RequireAotExecutable) {
    $releaseStream = [IO.File]::OpenRead($Path)
    $releaseReader = [IO.BinaryReader]::new($releaseStream)
    try {
        if ($releaseStream.Length -lt 256 -or $releaseReader.ReadUInt16() -ne 0x5a4d) { throw 'The publish contains an invalid PE image.' }
        $releaseStream.Position = 0x3c
        $releaseOffset = $releaseReader.ReadInt32()
        if ($releaseOffset -lt 64 -or $releaseOffset -gt 1048576 -or $releaseOffset + 264L -gt $releaseStream.Length) { throw 'A publish PE header is invalid.' }
        $releaseStream.Position = $releaseOffset
        if ($releaseReader.ReadUInt32() -ne 0x00004550 -or $releaseReader.ReadUInt16() -ne 0x8664) { throw 'A release requires Windows x64 PE images.' }
        $releaseStream.Position = $releaseOffset + 20
        $releaseOptionalSize = $releaseReader.ReadUInt16()
        $releaseCharacteristics = $releaseReader.ReadUInt16()
        if ($releaseOptionalSize -lt 240 -or $releaseOffset + 24L + $releaseOptionalSize -gt $releaseStream.Length -or $releaseReader.ReadUInt16() -ne 0x20b) {
            throw 'A publish PE image is not PE32+.'
        }
        if ($RequireAotExecutable) {
            if (($releaseCharacteristics -band 0x0002) -eq 0 -or ($releaseCharacteristics -band 0x2000) -ne 0) { throw 'The app entry point is not an executable image.' }
            $releaseStream.Position = $releaseOffset + 24 + 108
            if ($releaseReader.ReadUInt32() -lt 15) { throw 'The app PE data directories are incomplete.' }
            $releaseStream.Position = $releaseOffset + 24 + 112 + (14 * 8)
            if ($releaseReader.ReadUInt32() -ne 0 -or $releaseReader.ReadUInt32() -ne 0) { throw 'The app must be Native AOT, not a managed publish.' }
        }
    }
    finally { $releaseReader.Dispose() }
}

$releasePublishRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-ReleasePath $PublishDirectory))
$releaseOutputRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-ReleasePath $OutputDirectory))
if (-not (Test-Path -LiteralPath $releasePublishRoot -PathType Container)) { throw "Publish directory not found: $releasePublishRoot" }
Assert-ReleaseOrdinaryPath $releasePublishRoot
Assert-ReleaseArtifactPath $releaseOutputRoot
if ($releaseOutputRoot.Equals($releasePublishRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-ReleaseChild $releaseOutputRoot $releasePublishRoot)) {
    throw 'Release output must not be written inside the source publish directory.'
}
$releaseFiles = @(Get-ReleaseSourceFiles $releasePublishRoot | Sort-Object FullName)
$releaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[long]$releaseTotal = 0
foreach ($releaseFile in $releaseFiles) {
    $releaseRelative = [IO.Path]::GetRelativePath($releasePublishRoot, $releaseFile.FullName).Replace('\', '/')
    Assert-ReleaseRelativePath $releaseRelative
    if (-not $releaseNames.Add($releaseRelative)) { throw 'Publish files collide under Windows filename rules.' }
    if ($releaseFile.Length -gt 536870912) { throw 'A publish file exceeds the 512 MiB entry limit.' }
    $releaseTotal += $releaseFile.Length
    if ($releaseTotal -gt 2147483648) { throw 'The publish exceeds the 2 GiB expanded payload limit.' }
}
foreach ($releaseRequired in @(
    'EmbyClient.App.exe', 'EmbyClient.App.pri', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
    'FFmpegInteropX.dll', 'emby-client-win-x64.cdx.json', 'licenses/third-party/manifest.json', 'licenses/third-party/SOURCES.md',
    'Assets/AppIcon.ico', 'Assets/Lumen/Fonts/NotoSerifSC-Variable.ttf', 'Assets/Lumen/Fonts/Manrope-Variable.ttf',
    'Assets/Lumen/Fonts/LeagueGothic-Variable.ttf', 'Assets/Lumen/Fonts/IBMPlexMono-Regular.ttf', 'Assets/Lumen/Fonts/IBMPlexMono-Medium.ttf'
)) {
    if (-not $releaseNames.Contains($releaseRequired) -or (Get-Item -LiteralPath (Join-Path $releasePublishRoot $releaseRequired)).Length -eq 0) {
        throw "The complete publish is missing $releaseRequired."
    }
}
foreach ($releaseReserved in @('release-update-manifest.json', 'EmbyClient.App.dll', 'EmbyClient.App.runtimeconfig.json',
    'AppxManifest.xml', 'AppxSignature.p7x', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri')) {
    if ($releaseNames.Contains($releaseReserved)) { throw "Use the original unpackaged Native AOT publish; it contains reserved file $releaseReserved." }
}
foreach ($releaseFamily in @('avcodec', 'avformat', 'avutil', 'avfilter', 'avdevice', 'swresample', 'swscale')) {
    $releaseLibraries = @($releaseNames | Where-Object { $_ -match ('^' + $releaseFamily + '-\d+\.dll$') })
    if ($releaseLibraries.Count -ne 1) { throw "Expected one $releaseFamily shared library in the complete publish." }
}
& (Join-Path $PSScriptRoot 'Assert-AssPackageRuntime.ps1') -PublishRoot $releasePublishRoot
if (@($releaseNames | Where-Object { $_ -like 'Assets/Lumen/Icons/*.svg' }).Count -eq 0) { throw 'The publish is missing its icon library.' }
Assert-ReleasePe (Join-Path $releasePublishRoot 'EmbyClient.App.exe') $true
foreach ($releaseDll in $releaseFiles | Where-Object { $_.DirectoryName -eq $releasePublishRoot -and $_.Extension -eq '.dll' }) {
    Assert-ReleasePe $releaseDll.FullName $false
}
$releaseSbomPath = Join-Path $releasePublishRoot 'emby-client-win-x64.cdx.json'
if ((Get-Item -LiteralPath $releaseSbomPath).Length -gt 16777216) { throw 'The publish SBOM exceeds the size limit.' }
$releaseSbom = Get-Content -LiteralPath $releaseSbomPath -Raw | ConvertFrom-Json
if ($releaseSbom.bomFormat -ne 'CycloneDX' -or
    (Convert-ReleaseFourPart ([Version]$releaseSbom.metadata.component.version)) -ne (Convert-ReleaseFourPart $releaseVersion)) {
    throw 'The supplied release version must match the original application SBOM.'
}

$releaseRun = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '-' + [Guid]::NewGuid().ToString('N')
$releaseReviewRoot = Join-Path $releaseArtifactRoot "release-staging/$releaseRun"
$releasePayloadRoot = Join-Path $releaseReviewRoot 'payload'
Assert-ReleaseArtifactPath $releaseReviewRoot
if ($releaseReviewRoot.Equals($releasePublishRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-ReleaseChild $releaseReviewRoot $releasePublishRoot)) {
    throw 'Staging must not be written inside the source publish directory.'
}
[void](New-Item -ItemType Directory -Path $releasePayloadRoot)
[void](New-Item -ItemType Directory -Path $releaseOutputRoot -Force)
$releaseInventory = [Collections.Generic.List[object]]::new()
foreach ($releaseFile in $releaseFiles) {
    $releaseRelative = [IO.Path]::GetRelativePath($releasePublishRoot, $releaseFile.FullName).Replace('\', '/')
    $releaseDestination = [IO.Path]::GetFullPath((Join-Path $releasePayloadRoot $releaseRelative))
    if (-not (Test-ReleaseChild $releaseDestination $releasePayloadRoot)) { throw 'A staging path escapes the payload directory.' }
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $releaseDestination) -Force)
    $releaseHash = (Get-FileHash -LiteralPath $releaseFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $releaseFile.FullName -Destination $releaseDestination
    $releaseCopied = Get-Item -LiteralPath $releaseDestination
    if ($releaseCopied.Length -ne $releaseFile.Length -or (Get-FileHash -LiteralPath $releaseDestination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $releaseHash) {
        throw 'The source publish changed while it was being staged.'
    }
    $releaseInventory.Add([ordered]@{ path = $releaseRelative; length = $releaseCopied.Length; sha256 = $releaseHash })
}
$releaseManifest = [ordered]@{
    schemaVersion = 1; architecture = 'x64'; version = $Version; executable = 'EmbyClient.App.exe'; files = $releaseInventory
}
$releaseManifestPath = Join-Path $releasePayloadRoot 'release-update-manifest.json'
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestPath -Encoding utf8NoBOM
if ((Get-Item -LiteralPath $releaseManifestPath).Length -gt 4194304 -or (Get-Item -LiteralPath $releaseManifestPath).Length + $releaseTotal -gt 2147483648) {
    throw 'The manifest or complete expanded release exceeds the update limits.'
}
foreach ($releaseEntry in $releaseInventory) {
    $releaseSourcePath = Join-Path $releasePublishRoot $releaseEntry.path
    if ((Get-FileHash -LiteralPath $releaseSourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $releaseEntry.sha256) {
        throw 'The source publish changed during packaging. Rerun after publishing has finished.'
    }
}
$releaseCurrentNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($releaseFile in Get-ReleaseSourceFiles $releasePublishRoot) {
    [void]$releaseCurrentNames.Add([IO.Path]::GetRelativePath($releasePublishRoot, $releaseFile.FullName).Replace('\', '/'))
}
if (-not $releaseCurrentNames.SetEquals($releaseNames)) { throw 'The source publish file set changed during packaging.' }

$releaseArchiveName = "emby-client-windows-x64-aot-$Version.zip"
$releaseArchivePath = Join-Path $releaseOutputRoot $releaseArchiveName
Assert-ReleaseArtifactPath $releaseArchivePath
if (Test-Path -LiteralPath $releaseArchivePath) { throw 'A release ZIP for this version already exists. Use a separate artifacts output directory.' }
[IO.Compression.ZipFile]::CreateFromDirectory($releasePayloadRoot, $releaseArchivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
if ((Get-Item -LiteralPath $releaseArchivePath).Length -gt 1073741824) { throw 'The release ZIP exceeds the 1 GiB download limit.' }
$releaseArchive = [IO.Compression.ZipFile]::OpenRead($releaseArchivePath)
try {
    $releaseZipNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($releaseEntry in $releaseArchive.Entries) {
        if ($releaseEntry.FullName.EndsWith('/')) { continue }
        Assert-ReleaseRelativePath $releaseEntry.FullName
        if (-not $releaseZipNames.Add($releaseEntry.FullName)) { throw 'The release ZIP contains colliding paths.' }
    }
    $releaseExpectedZipNames = [Collections.Generic.HashSet[string]]::new($releaseNames, [StringComparer]::OrdinalIgnoreCase)
    [void]$releaseExpectedZipNames.Add('release-update-manifest.json')
    if (-not $releaseZipNames.SetEquals($releaseExpectedZipNames)) { throw 'The release ZIP did not preserve the complete payload file set.' }
}
finally { $releaseArchive.Dispose() }
$releaseArchiveHash = (Get-FileHash -LiteralPath $releaseArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$releaseArchiveHash  $releaseArchiveName" | Set-Content -LiteralPath "$releaseArchivePath.sha256" -Encoding ascii
Write-Output "Complete Windows x64 release ZIP: $releaseArchivePath"
Write-Output "SHA256 sidecar: $releaseArchivePath.sha256"
Write-Output "Staging inventory: $releaseManifestPath"
Write-Output 'Publish the ZIP and sidecar together as assets of the matching stable version tag. No signing, installation, or runtime activation was performed.'
