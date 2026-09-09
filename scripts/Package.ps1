#requires -Version 7.0

<#
.SYNOPSIS
Creates and structurally verifies an unsigned MSIX from an existing Native AOT publish directory.
.DESCRIPTION
Does not build, publish, sign, install, register, or change certificate trust. All generated files stay under artifacts.
The source publish directory is read-only. Each run uses a new staging and review directory.
#>
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$ManifestPath,
    [string]$Version,
    [string]$SdkBinDirectory,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

$packageWorkspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $packageWorkspace 'artifacts'))
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $artifactRoot 'aot' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactRoot 'packages' }
if (-not $ManifestPath) { $ManifestPath = Join-Path $packageWorkspace 'src/EmbyClient.App/Package.appxmanifest' }

function Resolve-WorkspacePath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $packageWorkspace $Path))
}

function Test-WithinPath([string]$Path, [string]$Root) {
    $prefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ArtifactPath([string]$Path) {
    if (-not (Test-WithinPath $Path $artifactRoot)) { throw "Generated files must be under $artifactRoot." }
    $ancestor = $Path
    while ($ancestor -and (Test-WithinPath $ancestor $packageWorkspace)) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Generated paths cannot traverse a reparse point: $ancestor"
            }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}

function Read-Xml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose() }
}

function Write-Xml([Xml.XmlDocument]$Document, [string]$Path) {
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try { $Document.Save($writer) }
    finally { $writer.Dispose() }
}

function Invoke-PackageTool([string]$Tool, [string[]]$Arguments, [string]$LogPath) {
    $messages = & $Tool @Arguments 2>&1
    $toolExitCode = $LASTEXITCODE
    # MakePri can emit UTF-16 to a redirected pipe. Remove the resulting interleaved NULs from its English log.
    @($messages | ForEach-Object { $_.ToString().Replace([string][char]0, '') }) | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($toolExitCode -ne 0) {
        $tail = (Get-Content -LiteralPath $LogPath -Tail 12) -join [Environment]::NewLine
        throw "$(Split-Path -Leaf $Tool) failed with exit code $toolExitCode. Log: $LogPath`n$tail"
    }
}

function Assert-NativeX64Executable([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'The application is not a PE executable.' }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset + 264 -gt $stream.Length) { throw 'The executable has an invalid PE header.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'This packaging script requires a Windows x64 executable.'
        }
        $stream.Position = $peOffset + 24
        if ($reader.ReadUInt16() -ne 0x20b) { throw 'The executable is not PE32+.' }
        $stream.Position = $peOffset + 24 + 112 + (14 * 8)
        if ($reader.ReadUInt32() -ne 0 -or $reader.ReadUInt32() -ne 0) {
            throw 'The executable contains a CLR header. Supply the verified Native AOT publish output.'
        }
    }
    finally { $reader.Dispose() }
}

function Get-ResourceKeys([Xml.XmlDocument]$Document) {
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $Document.SelectNodes('//NamedResource')) {
        $uriText = $resource.GetAttribute('uri')
        if (-not $uriText) { throw 'A PRI resource is missing its resource URI.' }
        $uri = [Uri]::new($uriText)
        [void]$keys.Add($uri.AbsolutePath.TrimStart('/'))
    }
    return ,$keys
}

$publishRoot = Resolve-WorkspacePath $PublishDirectory
$outputRoot = Resolve-WorkspacePath $OutputDirectory
$sourceManifestPath = Resolve-WorkspacePath $ManifestPath
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) { throw "Publish directory not found: $publishRoot" }
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { throw "Package manifest not found: $sourceManifestPath" }
Assert-ArtifactPath $outputRoot
if ($outputRoot.Equals($publishRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-WithinPath $outputRoot $publishRoot)) {
    throw 'Package output must not be written inside the source publish directory.'
}
if ((Get-Item -LiteralPath $publishRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The publish directory cannot be a reparse point.'
}
$publishItems = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -Force)
if ($publishItems | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'The publish directory contains a reparse point; use a complete ordinary publish directory.'
}

foreach ($requiredFile in @('EmbyClient.App.exe', 'EmbyClient.App.pri', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredFile) -PathType Leaf)) {
        throw "The publish directory is missing a required application/runtime file: $requiredFile"
    }
}
foreach ($managedCompanion in @('EmbyClient.App.dll', 'EmbyClient.App.runtimeconfig.json')) {
    if (Test-Path -LiteralPath (Join-Path $publishRoot $managedCompanion)) {
        throw 'The directory appears to contain a managed publish. Use the verified Native AOT output.'
    }
}
foreach ($reservedFile in @('AppxManifest.xml', 'AppxSignature.p7x', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri')) {
    if (Test-Path -LiteralPath (Join-Path $publishRoot $reservedFile)) {
        throw "The source contains packaging metadata ($reservedFile); use the original unpackaged Native AOT output."
    }
}
Assert-NativeX64Executable (Join-Path $publishRoot 'EmbyClient.App.exe')

if (-not $SdkBinDirectory) {
    $sdkRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/bin'
    $sdkFolders = @(Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object {
        $sdkVersion = $null
        [Version]::TryParse($_.Name, [ref]$sdkVersion)
    } | Sort-Object { [Version]$_.Name } -Descending)
    foreach ($sdkFolder in $sdkFolders) {
        $candidate = Join-Path $sdkFolder.FullName 'x64'
        if ((Test-Path -LiteralPath (Join-Path $candidate 'makeappx.exe')) -and (Test-Path -LiteralPath (Join-Path $candidate 'makepri.exe'))) {
            $SdkBinDirectory = $candidate
            break
        }
    }
}
if (-not $SdkBinDirectory) { throw 'Windows SDK MakeAppx and MakePri were not found. Specify -SdkBinDirectory.' }
$sdkBin = Resolve-WorkspacePath $SdkBinDirectory
$makeAppx = Join-Path $sdkBin 'makeappx.exe'
$makePri = Join-Path $sdkBin 'makepri.exe'
foreach ($toolPath in @($makeAppx, $makePri)) {
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) { throw "Required Windows SDK tool not found: $toolPath" }
}

$manifest = Read-Xml $sourceManifestPath
$sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$foundationNamespace = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$uap10Namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
$ns.AddNamespace('f', $foundationNamespace)
$ns.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $ns)
$applications = @($manifest.SelectNodes('/f:Package/f:Applications/f:Application', $ns))
$deviceFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $ns)
if ($null -eq $identity -or $applications.Count -ne 1 -or $null -eq $deviceFamily) { throw 'Expected one desktop application and a package identity.' }
if ($deviceFamily.GetAttribute('Name') -ne 'Windows.Desktop' -or [Version]$deviceFamily.GetAttribute('MinVersion') -lt [Version]'10.0.19041.0') {
    throw 'The native desktop manifest must target Windows.Desktop with a minimum version of at least 10.0.19041.0.'
}
if ($null -eq $manifest.SelectSingleNode('/f:Package/f:Capabilities/rescap:Capability[@Name="runFullTrust"]', $ns)) {
    throw 'The desktop package must declare the runFullTrust capability.'
}
if ($manifest.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $ns).Count -ne 0) {
    throw 'This self-contained packaging path does not accept external framework package dependencies.'
}
if (-not $Version) { $Version = $identity.GetAttribute('Version') }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -ne 0) {
    throw 'Version must contain four numeric components from 0 through 65535.'
}
$identity.SetAttribute('Version', $Version)
$identity.SetAttribute('ProcessorArchitecture', 'x64')
$identityName = $identity.GetAttribute('Name')
$publisher = $identity.GetAttribute('Publisher')
if ($identityName -notmatch '^[A-Za-z0-9.-]{3,50}$' -or [string]::IsNullOrWhiteSpace($publisher)) { throw 'The package identity is invalid.' }
$application = $applications[0]
$application.SetAttribute('Executable', 'EmbyClient.App.exe')
$application.RemoveAttribute('EntryPoint')
$xmlnsAttribute = $manifest.CreateAttribute('xmlns', 'uap10', 'http://www.w3.org/2000/xmlns/')
$xmlnsAttribute.Value = $uap10Namespace
[void]$manifest.DocumentElement.Attributes.SetNamedItem($xmlnsAttribute)
foreach ($pair in @{ RuntimeBehavior = 'packagedClassicApp'; TrustLevel = 'mediumIL' }.GetEnumerator()) {
    $attribute = $manifest.CreateAttribute('uap10', $pair.Key, $uap10Namespace)
    $attribute.Value = $pair.Value
    [void]$application.Attributes.SetNamedItem($attribute)
}
$ignorable = @($manifest.DocumentElement.GetAttribute('IgnorableNamespaces').Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
$manifest.DocumentElement.SetAttribute('IgnorableNamespaces', (($ignorable + 'uap10' | Select-Object -Unique) -join ' '))
if ($manifest.OuterXml -match '\$[^$]+\$') { throw 'The staged manifest still contains an unresolved template token.' }

$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$reviewRoot = Join-Path $artifactRoot "packaging/$runName"
Assert-ArtifactPath $reviewRoot
if ($reviewRoot.Equals($publishRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-WithinPath $reviewRoot $publishRoot)) {
    throw 'Staging must not be inside the source publish directory.'
}
$payloadRoot = Join-Path $reviewRoot 'payload'
$resourceRoot = Join-Path $reviewRoot 'resource-input'
$unpackedRoot = Join-Path $reviewRoot 'unpacked'
foreach ($directory in @($outputRoot, $reviewRoot, $payloadRoot, $resourceRoot)) {
    [void](New-Item -ItemType Directory -Path $directory -Force)
}

$sourceFiles = @($publishItems | Where-Object { -not $_.PSIsContainer -and ($IncludeSymbols -or $_.Extension -ne '.pdb') } | Sort-Object FullName)
$sourceInventory = [Collections.Generic.List[object]]::new()
foreach ($file in $sourceFiles) {
    $relativePath = [IO.Path]::GetRelativePath($publishRoot, $file.FullName)
    $destination = Join-Path $payloadRoot $relativePath
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sourceHash) {
        throw "The source changed during staging: $relativePath. Run packaging after publishing has finished."
    }
    $sourceInventory.Add([ordered]@{ Path = $relativePath; Length = (Get-Item -LiteralPath $destination).Length; Sha256 = $sourceHash })
}
$stagedManifest = Join-Path $payloadRoot 'AppxManifest.xml'
Write-Xml $manifest $stagedManifest

# Merge the complete published PRI, including embedded XBF and qualified Assets, into the package's main map.
# An Assets-only resources.pri would hide the application's default XAML resources in packaged execution.
Copy-Item -LiteralPath (Join-Path $payloadRoot 'EmbyClient.App.pri') -Destination (Join-Path $resourceRoot 'EmbyClient.App.pri')
$priConfigPath = Join-Path $reviewRoot 'priconfig.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="\" startIndexAt="EmbyClient.App.pri">
    <default>
      <qualifier name="Language" value="en-US" />
      <qualifier name="Scale" value="200" />
    </default>
    <indexer-config type="PRI" />
  </index>
</resources>
'@ | Set-Content -LiteralPath $priConfigPath -Encoding utf8
$packagePriPath = Join-Path $payloadRoot 'resources.pri'
Invoke-PackageTool $makePri @('new', '/pr', $resourceRoot, '/cf', $priConfigPath, '/in', $identityName, '/of', $packagePriPath) (Join-Path $reviewRoot 'makepri-new.log')
$originalPriDump = Join-Path $reviewRoot 'source-pri.xml'
$packagePriDump = Join-Path $reviewRoot 'package-pri.xml'
Invoke-PackageTool $makePri @('dump', '/if', (Join-Path $payloadRoot 'EmbyClient.App.pri'), '/dt', 'detailed', '/of', $originalPriDump) (Join-Path $reviewRoot 'makepri-source-dump.log')
Invoke-PackageTool $makePri @('dump', '/if', $packagePriPath, '/dt', 'detailed', '/of', $packagePriDump) (Join-Path $reviewRoot 'makepri-package-dump.log')
$sourceResources = Get-ResourceKeys (Read-Xml $originalPriDump)
$packagePriDocument = Read-Xml $packagePriDump
$resourceMaps = @($packagePriDocument.SelectNodes('//ResourceMap'))
if ($resourceMaps.Count -ne 1 -or -not $resourceMaps[0].GetAttribute('name').Equals($identityName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The package PRI must contain one main resource map matching the package identity.'
}
$packageResources = Get-ResourceKeys $packagePriDocument
if ($sourceResources.Count -eq 0 -or -not $sourceResources.SetEquals($packageResources)) {
    throw 'The package PRI resource keys do not match the complete published application PRI.'
}
if (-not $packageResources.Contains('Files/App.xbf')) { throw 'The package resource index does not contain App.xbf.' }

$packageFileName = '{0}_{1}_x64_unsigned_{2}.msix' -f $identityName, $Version, $runName
$packagePath = Join-Path $outputRoot $packageFileName
Invoke-PackageTool $makeAppx @('pack', '/d', $payloadRoot, '/p', $packagePath, '/h', 'SHA256', '/no') (Join-Path $reviewRoot 'makeappx-pack.log')
Invoke-PackageTool $makeAppx @('unpack', '/p', $packagePath, '/d', $unpackedRoot, '/no') (Join-Path $reviewRoot 'makeappx-unpack.log')

$payloadInventory = [Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName)) {
    $relativePath = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
    $unpackedPath = Join-Path $unpackedRoot $relativePath
    $payloadHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if (-not (Test-Path -LiteralPath $unpackedPath -PathType Leaf) -or (Get-FileHash -LiteralPath $unpackedPath -Algorithm SHA256).Hash -ne $payloadHash) {
        throw "The unpacked payload does not match the staged input: $relativePath"
    }
    $payloadInventory.Add([ordered]@{ Path = $relativePath; Length = $file.Length; Sha256 = $payloadHash })
}
foreach ($source in $sourceInventory) {
    $sourcePath = Join-Path $publishRoot $source.Path
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $source.Sha256) {
        throw "The source publish changed during packaging: $($source.Path). Discard this review candidate and rerun after publishing."
    }
}
$currentSourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in (Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Force | Where-Object { $IncludeSymbols -or $_.Extension -ne '.pdb' })) {
    [void]$currentSourcePaths.Add([IO.Path]::GetRelativePath($publishRoot, $file.FullName))
}
if (-not $currentSourcePaths.SetEquals([string[]]@($sourceInventory | ForEach-Object { $_.Path }))) {
    throw 'The source publish file set changed during packaging. Rerun after publishing has finished.'
}
if ((Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash -ne $sourceManifestHash) {
    throw 'The source manifest changed during packaging. Rerun after manifest edits have finished.'
}
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $archiveNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($requiredEntry in @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri', 'EmbyClient.App.exe')) {
        if ($archiveNames -notcontains $requiredEntry) { throw "The package is missing required content: $requiredEntry" }
    }
    if ($archiveNames -contains 'AppxSignature.p7x') { throw 'Expected an unsigned package, but a signature entry exists.' }
}
finally { $archive.Dispose() }

$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$reviewPath = Join-Path $reviewRoot 'package-review.json'
$review = [ordered]@{
    CreatedUtc = [DateTime]::UtcNow.ToString('O')
    PackagePath = $packagePath
    PackageLength = (Get-Item -LiteralPath $packagePath).Length
    PackageSha256 = $packageHash
    Identity = [ordered]@{ Name = $identityName; Publisher = $publisher; Version = $Version; Architecture = 'x64' }
    DevelopmentPublisherPlaceholder = ($publisher -eq 'CN=EmbyClient.Development')
    Signed = $false
    InstallationVerified = $false
    PackageRuntimeVerified = $false
    ManifestPath = $stagedManifest
    SourcePublishDirectory = $publishRoot
    SourceManifestSha256 = $sourceManifestHash
    IncludedSymbols = [bool]$IncludeSymbols
    ResourceKeyCount = $packageResources.Count
    ToolVersions = [ordered]@{
        MakeAppx = (Get-Item -LiteralPath $makeAppx).VersionInfo.FileVersion
        MakePri = (Get-Item -LiteralPath $makePri).VersionInfo.FileVersion
        SdkBinDirectory = $sdkBin
    }
    Validation = @('Native x64 PE shape', 'Required self-contained payload files', 'Complete PRI resource key preservation',
        'MakeAppx default semantic validation', 'MakeAppx unpack', 'All staged file SHA256 round trips', 'Source snapshot unchanged', 'No package signature')
    SourceFiles = $sourceInventory
    PayloadFiles = $payloadInventory
}
$review | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reviewPath -Encoding utf8
"$packageHash  $packageFileName" | Set-Content -LiteralPath "$packagePath.sha256" -Encoding ascii
Copy-Item -LiteralPath $reviewPath -Destination "$packagePath.review.json"
Write-Output "Unsigned MSIX: $packagePath"
Write-Output "SHA256: $packageHash"
Write-Output "Review directory: $reviewRoot"
Write-Output 'Structural verification passed. Package installation and packaged runtime behavior remain unverified.'
