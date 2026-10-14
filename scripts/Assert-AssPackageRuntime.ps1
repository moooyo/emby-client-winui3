#requires -Version 7.0
param([Parameter(Mandatory)][string] $PublishRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assPackageRepository = Split-Path $PSScriptRoot -Parent
$assPackageLockPath = Join-Path $assPackageRepository 'native/FFmpegInteropX/ass.dependencies.lock.json'
$assPackageLock = Get-Content -LiteralPath $assPackageLockPath -Raw | ConvertFrom-Json -AsHashtable
$assPackageLockSha256 = (Get-FileHash -LiteralPath $assPackageLockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$assPackageSbomPath = Join-Path $PublishRoot 'emby-client-win-x64.cdx.json'
$assPackageNoticesPath = Join-Path $PublishRoot 'licenses/third-party/manifest.json'
foreach ($assPackageMetadataPath in @($assPackageSbomPath, $assPackageNoticesPath)) {
    if (!(Test-Path -LiteralPath $assPackageMetadataPath -PathType Leaf) -or (Get-Item -LiteralPath $assPackageMetadataPath).Length -gt 16777216) {
        throw 'The publish must contain its complete native SBOM and third-party notice inventory.'
    }
}
$assPackageSbom = Get-Content -LiteralPath $assPackageSbomPath -Raw | ConvertFrom-Json -AsHashtable
$assPackageNotices = Get-Content -LiteralPath $assPackageNoticesPath -Raw | ConvertFrom-Json -AsHashtable
if ($assPackageSbom.bomFormat -ne 'CycloneDX' -or $assPackageSbom.specVersion -ne '1.6') { throw 'Expected the publish CycloneDX 1.6 native dependency inventory.' }

function Get-AssPackageProperty([object] $Component, [string] $Name) {
    $properties = @($Component.properties | Where-Object name -eq $Name)
    if ($properties.Count -ne 1) { throw "A native ASS SBOM component must contain one $Name property." }
    return [string]$properties[0].value
}

$assPackageLibraries = @{}
foreach ($assPackageLibrary in $assPackageLock.libraries) {
    $assPackageComponents = @($assPackageSbom.components | Where-Object { $_.type -eq 'library' -and $_.name -eq $assPackageLibrary.id })
    if ($assPackageComponents.Count -ne 1 -or $assPackageComponents[0].version -cne $assPackageLibrary.version) { throw "The publish is missing the locked native library identity: $($assPackageLibrary.id)" }
    $assPackageComponent = $assPackageComponents[0]
    if ((Get-AssPackageProperty $assPackageComponent 'emby:native:assDependencyLockSha256') -cne $assPackageLockSha256 -or
        (Get-AssPackageProperty $assPackageComponent 'emby:native:vcpkgCommit') -cne $assPackageLock.vcpkg.commit -or
        (Get-AssPackageProperty $assPackageComponent 'emby:native:portVersion') -cne [string]$assPackageLibrary.portVersion) {
        throw "The publish native library provenance differs from its source lock: $($assPackageLibrary.id)"
    }
    $assPackageSourceHashes = @($assPackageComponent.hashes | Where-Object alg -eq 'SHA-512')
    $assPackageLicenses = @($assPackageComponent.licenses | Where-Object expression -eq $assPackageLibrary.license)
    if ($assPackageSourceHashes.Count -ne 1 -or $assPackageSourceHashes[0].content -cne $assPackageLibrary.source.sha512 -or $assPackageLicenses.Count -ne 1) { throw 'The native library source digest or license differs from its lock.' }
    $assPackageLibraries[$assPackageLibrary.id] = $assPackageComponent
    foreach ($assPackageNotice in $assPackageLibrary.notices) {
        $assPackageNoticeRelative = 'AssNative-0.17.5/' + $assPackageNotice
        $assPackageNoticeEntries = @($assPackageNotices | Where-Object File -eq $assPackageNoticeRelative)
        $assPackageNoticeFile = Join-Path $PublishRoot ('licenses/third-party/' + $assPackageNoticeRelative)
        if ($assPackageNoticeEntries.Count -ne 1 -or !(Test-Path -LiteralPath $assPackageNoticeFile -PathType Leaf)) { throw "The publish is missing a native library notice: $assPackageNoticeRelative" }
        if ($assPackageNoticeEntries[0].ComponentVersion -cne $assPackageLibrary.version -or
            $assPackageNoticeEntries[0].SourceArchiveSha512 -cne $assPackageLibrary.source.sha512 -or
            (Get-FileHash -LiteralPath $assPackageNoticeFile -Algorithm SHA256).Hash -cne $assPackageNoticeEntries[0].Sha256) {
            throw "The native library notice does not match its copied source inventory: $assPackageNoticeRelative"
        }
    }
}

$assPackageFiles = @($assPackageSbom.components | Where-Object {
    $_.type -eq 'file' -and @($_.properties | Where-Object { $_.name -eq 'emby:native:family' -and $_.value -eq 'ass' }).Count -eq 1
})
$assPackageFilesByName = @{}
foreach ($assPackageFile in $assPackageFiles) {
    if ([IO.Path]::GetFileName($assPackageFile.name) -ne $assPackageFile.name -or $assPackageFilesByName.ContainsKey($assPackageFile.name)) { throw 'The native ASS package file inventory has an invalid or duplicate name.' }
    $assPackageOwner = Get-AssPackageProperty $assPackageFile 'emby:native:assLibrary'
    if (!$assPackageLibraries.ContainsKey($assPackageOwner)) { throw 'A native ASS package file has no locked source-library owner.' }
    $assPackageActualFile = Join-Path $PublishRoot $assPackageFile.name
    $assPackageFileHashes = @($assPackageFile.hashes | Where-Object alg -eq 'SHA-256')
    if (!(Test-Path -LiteralPath $assPackageActualFile -PathType Leaf) -or $assPackageFileHashes.Count -ne 1 -or
        $assPackageFileHashes[0].content -notmatch '^[a-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $assPackageActualFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assPackageFileHashes[0].content -or
        [string](Get-Item -LiteralPath $assPackageActualFile).Length -cne (Get-AssPackageProperty $assPackageFile 'emby:native:bytes')) {
        throw "The complete publish is missing or has changed a native ASS runtime DLL: $($assPackageFile.name)"
    }
    $assPackageFilesByName[$assPackageFile.name] = $assPackageFile
}
if (!$assPackageFilesByName.ContainsKey($assPackageLock.runtime.name)) { throw 'The complete publish is missing its locked libass renderer DLL.' }
$assPackageSystemDlls = @('kernel32.dll', 'kernelbase.dll', 'ntdll.dll', 'user32.dll', 'gdi32.dll', 'advapi32.dll', 'ole32.dll', 'oleaut32.dll', 'shell32.dll', 'shlwapi.dll', 'bcrypt.dll', 'ws2_32.dll', 'rpcrt4.dll', 'dwrite.dll', 'msvcrt.dll', 'ucrtbase.dll', 'version.dll', 'winmm.dll', 'imm32.dll', 'combase.dll', 'secur32.dll')
foreach ($assPackageFile in $assPackageFiles) {
    foreach ($assPackageImport in (Get-AssPackageProperty $assPackageFile 'emby:native:peImports').Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        if (!$assPackageFilesByName.ContainsKey($assPackageImport) -and $assPackageImport -notin $assPackageSystemDlls -and $assPackageImport -notmatch '^(api-ms-win-|ext-ms-).*\.dll$') {
            throw "The packaged ASS runtime closure is incomplete: $($assPackageFile.name) imports $assPackageImport."
        }
    }
}
foreach ($assPackageLibrary in $assPackageLock.libraries) {
    $assPackageLibraryComponent = $assPackageLibraries[$assPackageLibrary.id]
    $assPackageEdges = @($assPackageSbom.dependencies | Where-Object ref -eq $assPackageLibraryComponent.'bom-ref')
    $assPackageOwnedFiles = @($assPackageFiles | Where-Object { (Get-AssPackageProperty $_ 'emby:native:assLibrary') -eq $assPackageLibrary.id })
    if ($assPackageEdges.Count -ne 1 -or $assPackageOwnedFiles.Count -eq 0) { throw 'A packaged native ASS library has no runtime file ownership edge.' }
    foreach ($assPackageFile in $assPackageOwnedFiles) {
        if ($assPackageEdges[0].dependsOn -notcontains $assPackageFile.'bom-ref') { throw 'The native ASS package file dependency graph is incomplete.' }
    }
    foreach ($assPackageChild in $assPackageLibrary.dependencies) {
        if ($assPackageEdges[0].dependsOn -notcontains $assPackageLibraries[$assPackageChild].'bom-ref') { throw 'The native ASS package source dependency graph is incomplete.' }
    }
}
