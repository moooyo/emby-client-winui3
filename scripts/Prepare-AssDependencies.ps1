#requires -Version 7.0
param(
    [string] $DependencyRoot,
    [string] $VisualStudioRoot,
    [string] $MsvcVersion,
    [string] $WindowsSdkVersion,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assRepositoryRoot = Split-Path $PSScriptRoot -Parent
$assLockPath = Join-Path $assRepositoryRoot 'native/FFmpegInteropX/ass.dependencies.lock.json'
$assLock = Get-Content -LiteralPath $assLockPath -Raw | ConvertFrom-Json
$assConfigurationRoot = Join-Path $assRepositoryRoot 'native/FFmpegInteropX/AssNative'
if (!$DependencyRoot) { $DependencyRoot = Join-Path $assRepositoryRoot 'artifacts/decoder-apis/dependencies' }
$DependencyRoot = [IO.Path]::GetFullPath($DependencyRoot)
$assPayloadRoot = Join-Path $DependencyRoot $assLock.payloadDirectory
$assInstallRoot = Join-Path $assPayloadRoot 'installed'
$assNativeRoot = Join-Path $assInstallRoot $assLock.vcpkg.triplet
$assRuntimeRoot = Join-Path $assPayloadRoot 'runtime'
$assReceiptPath = Join-Path $assPayloadRoot 'build-receipt.json'
$assMutexName = 'Local\EmbyAssNative-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($assPayloadRoot.ToLowerInvariant()))).Substring(0, 24)
$assBuildMutex = [Threading.Mutex]::new($false, $assMutexName)
$assMutexAcquired = $false
$assSavedEnvironment = @{}

function Get-AssSha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AssLockedDownload([object] $Artifact, [string] $Path) {
    if (!(Test-Path -LiteralPath $Path)) {
        $temporaryPath = $Path + '.download'
        Invoke-WebRequest -Uri $Artifact.url -OutFile $temporaryPath
        if ((Get-AssSha256 $temporaryPath) -cne $Artifact.sha256 -or (Get-Item -LiteralPath $temporaryPath).Length -ne $Artifact.size) {
            throw "The locked libass build tool download does not match its hash and size: $($Artifact.url)"
        }
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    if ((Get-AssSha256 $Path) -cne $Artifact.sha256 -or (Get-Item -LiteralPath $Path).Length -ne $Artifact.size) {
        throw "The cached libass build tool does not match its lock: $Path"
    }
}

function Invoke-AssBuildTool([string] $Tool, [string[]] $Arguments, [string] $LogPath) {
    $messages = & $Tool @Arguments 2>&1
    $toolExitCode = $LASTEXITCODE
    $messages | Out-File -LiteralPath $LogPath -Encoding utf8 -Append
    if ($toolExitCode -ne 0) {
        throw "The libass dependency command failed with exit code $toolExitCode. See $LogPath"
    }
}

function Get-AssDependencyResult([object] $Receipt) {
    return [pscustomobject]@{
        NativeRoot = $assNativeRoot
        RuntimeRoot = $assRuntimeRoot
        ReceiptPath = $assReceiptPath
        ReceiptSha256 = Get-AssSha256 $assReceiptPath
        DependencyFingerprint = $Receipt.sourceFingerprint
        LockSha256 = $Receipt.lockSha256
        RuntimeFiles = $Receipt.runtimeFiles
        Libraries = $Receipt.libraries
    }
}

function Get-AssDllImports([string] $Dumpbin, [string] $Path, [string] $LogRoot) {
    $headers = & $Dumpbin /NOLOGO /HEADERS $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $headers -notmatch '(?im)^\s*8664\s+machine') { throw "Expected an x64 libass dependency DLL: $Path" }
    $imports = & $Dumpbin /NOLOGO /DEPENDENTS $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "The libass runtime import table could not be read: $Path" }
    Set-Content -LiteralPath (Join-Path $LogRoot ((Split-Path -Leaf $Path) + '.imports.txt')) -Value $imports -Encoding utf8
    return @([regex]::Matches($imports, '(?im)^\s+([a-z0-9_.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
}

try {
    $assWaitStarted = [DateTime]::UtcNow
    while (!$assMutexAcquired) {
        try { $assMutexAcquired = $assBuildMutex.WaitOne(1000) }
        catch [Threading.AbandonedMutexException] { $assMutexAcquired = $true }
        if (!$assMutexAcquired -and ([DateTime]::UtcNow - $assWaitStarted).TotalMinutes -gt 20) { throw 'Timed out waiting for another libass dependency build.' }
    }

    if (!$VisualStudioRoot) {
        $assVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (!(Test-Path -LiteralPath $assVswhere)) { throw 'Visual Studio with the x64 C++ build tools is required for libass.' }
        $VisualStudioRoot = & $assVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (!$VisualStudioRoot) { throw 'Visual Studio x64 C++ build tools were not found for libass.' }
    }
    if (!$MsvcVersion) { $MsvcVersion = (Get-Content -LiteralPath (Join-Path $VisualStudioRoot 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim() }
    if (!$WindowsSdkVersion) {
        $assDecoderLock = Get-Content -LiteralPath (Join-Path $assRepositoryRoot 'native/FFmpegInteropX/dependencies.lock.json') -Raw | ConvertFrom-Json
        $WindowsSdkVersion = $assDecoderLock.toolchain.windowsSdkVersion
    }
    $assCompiler = Join-Path $VisualStudioRoot "VC/Tools/MSVC/$MsvcVersion/bin/Hostx64/x64/cl.exe"
    $assDumpbin = Join-Path $VisualStudioRoot "VC/Tools/MSVC/$MsvcVersion/bin/Hostx64/x64/dumpbin.exe"
    foreach ($assTool in @($assCompiler, $assDumpbin)) { if (!(Test-Path -LiteralPath $assTool)) { throw "The selected libass compiler tool was not found: $assTool" } }

    $assSourceFiles = @(Get-ChildItem -LiteralPath $assConfigurationRoot -Recurse -File)
    $assSourceFiles += Get-Item -LiteralPath $assLockPath, $PSCommandPath, (Join-Path $assRepositoryRoot 'native/FFmpegInteropX/AssNative.props')
    $assSourceFiles += Get-ChildItem -LiteralPath (Join-Path $assRepositoryRoot 'licenses/third-party/AssNative-0.17.5') -Recurse -File
    $assSourceInventory = @($assSourceFiles | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($assRepositoryRoot, $_.FullName).Replace('\', '/'); sha256 = Get-AssSha256 $_.FullName }
    })
    $assFingerprintInput = ($assSourceInventory | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
    $assFingerprintInput += "`n$VisualStudioRoot`n$MsvcVersion`n$WindowsSdkVersion`n$(Get-AssSha256 $assCompiler)`n$($assLock.vcpkg.tool.sha256)"
    $assFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($assFingerprintInput))).ToLowerInvariant()

    if (!$Force -and (Test-Path -LiteralPath $assReceiptPath)) {
        $assReceipt = Get-Content -LiteralPath $assReceiptPath -Raw | ConvertFrom-Json
        if ($assReceipt.sourceFingerprint -ceq $assFingerprint) {
            $assPayloadValid = $true
            foreach ($assFile in @($assReceipt.runtimeFiles)) {
                if ([IO.Path]::GetFileName($assFile.name) -ne $assFile.name) { throw 'Cached libass runtime file names must be basenames.' }
                $assPath = Join-Path $assRuntimeRoot $assFile.name
                if (!(Test-Path -LiteralPath $assPath) -or (Get-AssSha256 $assPath) -cne $assFile.sha256) { $assPayloadValid = $false; break }
            }
            foreach ($assFile in @($assReceipt.developmentFiles)) {
                $assPath = [IO.Path]::GetFullPath((Join-Path $assNativeRoot $assFile.path))
                if (!$assPath.StartsWith($assNativeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'A cached libass development path escapes its installation.' }
                if (!(Test-Path -LiteralPath $assPath) -or (Get-AssSha256 $assPath) -cne $assFile.sha256) { $assPayloadValid = $false; break }
            }
            if ($assPayloadValid) { return Get-AssDependencyResult $assReceipt }
        }
    }

    $assLogRoot = Join-Path $assPayloadRoot 'logs'
    New-Item -ItemType Directory -Path $DependencyRoot, $assPayloadRoot, $assLogRoot, $assRuntimeRoot -Force | Out-Null
    $assGit = (Get-Command git.exe -ErrorAction Stop).Source
    $assVcpkgRoot = Join-Path $DependencyRoot ('vcpkg-' + $assLock.vcpkg.commit.Substring(0, 12))
    $assGitLog = Join-Path $assLogRoot 'vcpkg-source.log'
    if (!(Test-Path -LiteralPath (Join-Path $assVcpkgRoot '.git'))) {
        if ((Test-Path -LiteralPath $assVcpkgRoot) -and @(Get-ChildItem -LiteralPath $assVcpkgRoot -Force).Count -ne 0) { throw 'The vcpkg cache directory contains an unrecognized checkout.' }
        New-Item -ItemType Directory -Path $assVcpkgRoot -Force | Out-Null
        Invoke-AssBuildTool $assGit @('init', $assVcpkgRoot) $assGitLog
        Invoke-AssBuildTool $assGit @('-C', $assVcpkgRoot, 'remote', 'add', 'origin', $assLock.vcpkg.repository) $assGitLog
        Invoke-AssBuildTool $assGit @('-C', $assVcpkgRoot, '-c', 'core.autocrlf=false', 'fetch', '--depth=1', '--no-tags', 'origin', $assLock.vcpkg.commit) $assGitLog
        Invoke-AssBuildTool $assGit @('-C', $assVcpkgRoot, '-c', 'core.autocrlf=false', 'checkout', '--detach', 'FETCH_HEAD') $assGitLog
    }
    $assGitHead = (& $assGit -C $assVcpkgRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $assGitHead -cne $assLock.vcpkg.commit) { throw 'The vcpkg checkout does not match the locked Git commit.' }
    $assGitChanges = (& $assGit -C $assVcpkgRoot status --porcelain --untracked-files=no | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $assGitChanges) { throw 'The locked vcpkg checkout has modified tracked files.' }
    $assManifest = Get-Content -LiteralPath (Join-Path $assConfigurationRoot 'vcpkg.json') -Raw | ConvertFrom-Json
    if ($assManifest.'builtin-baseline' -cne $assLock.vcpkg.commit) { throw 'The libass manifest baseline differs from its dependency lock.' }
    foreach ($assLibrary in $assLock.libraries) {
        $assPortRoot = Join-Path $assVcpkgRoot ('ports/' + $assLibrary.id)
        if ($assLibrary.id -eq 'libass') { $assPortRoot = Join-Path $assConfigurationRoot 'ports/libass' }
        $assPort = Get-Content -LiteralPath (Join-Path $assPortRoot 'vcpkg.json') -Raw | ConvertFrom-Json
        $assVersionProperty = @($assPort.PSObject.Properties | Where-Object Name -in 'version', 'version-semver', 'version-string', 'version-date')
        $assPortVersion = 0
        if ($assPort.PSObject.Properties['port-version']) { $assPortVersion = $assPort.'port-version' }
        if ($assVersionProperty.Count -ne 1 -or $assVersionProperty[0].Value -cne $assLibrary.version -or $assPortVersion -ne $assLibrary.portVersion -or $assPort.license -cne $assLibrary.license) {
            throw "The pinned vcpkg port metadata differs from the library lock: $($assLibrary.id)"
        }
        $assPortScript = Get-Content -LiteralPath (Join-Path $assPortRoot 'portfile.cmake') -Raw
        if ($assPortScript -notmatch ('SHA512\s+' + [regex]::Escape($assLibrary.source.sha512))) { throw "The pinned source checksum differs from the library lock: $($assLibrary.id)" }
    }
    $assVcpkg = Join-Path $assVcpkgRoot $assLock.vcpkg.tool.name
    Get-AssLockedDownload $assLock.vcpkg.tool $assVcpkg
    $assEnvironment = @{
        VCPKG_DISABLE_METRICS = '1'
        VCPKG_VISUAL_STUDIO_PATH = $VisualStudioRoot
        VCPKG_FEATURE_FLAGS = 'manifests,versions'
        EMBY_ASS_MSVC_VERSION = $MsvcVersion
        EMBY_ASS_WINDOWS_SDK_VERSION = $WindowsSdkVersion
    }
    foreach ($assEntry in $assEnvironment.GetEnumerator()) {
        $assSavedEnvironment[$assEntry.Key] = [Environment]::GetEnvironmentVariable($assEntry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($assEntry.Key, $assEntry.Value, 'Process')
    }
    $assArguments = @(
        'install', "--triplet=$($assLock.vcpkg.triplet)", "--host-triplet=$($assLock.vcpkg.hostTriplet)",
        "--x-manifest-root=$assConfigurationRoot", "--x-install-root=$assInstallRoot",
        "--overlay-ports=$(Join-Path $assConfigurationRoot 'ports')", "--overlay-triplets=$(Join-Path $assConfigurationRoot 'triplets')",
        "--x-buildtrees-root=$(Join-Path $assPayloadRoot 'buildtrees')", "--x-packages-root=$(Join-Path $assPayloadRoot 'packages')",
        "--downloads-root=$(Join-Path $DependencyRoot 'ass-downloads')", '--binarysource=clear'
    )
    Write-Host "Building the locked libass $($assLock.libraries[0].version) Windows x64 dependency graph."
    Invoke-AssBuildTool $assVcpkg $assArguments (Join-Path $assLogRoot 'vcpkg-build.log')

    $assStatus = Get-Content -LiteralPath (Join-Path $assInstallRoot 'vcpkg/status') -Raw
    $assStatusRecords = @($assStatus -split '(?:\r?\n){2,}')
    $assOwners = @{}
    $assLibraryInventory = @()
    foreach ($assLibrary in $assLock.libraries) {
        $assRecords = @($assStatusRecords | Where-Object {
            $_ -match ('(?m)^Package: ' + [regex]::Escape($assLibrary.id) + '\r?$') -and
            $_ -match ('(?m)^Architecture: ' + [regex]::Escape($assLock.vcpkg.triplet) + '\r?$') -and
            ($_ -notmatch '(?m)^Feature:' -or $_ -match '(?m)^Feature: core\r?$')
        })
        if ($assRecords.Count -ne 1 -or $assRecords[0] -notmatch ('(?m)^Version: ' + [regex]::Escape($assLibrary.version) + '\r?$')) { throw "The installed libass dependency version differs from its lock: $($assLibrary.id)" }
        $assInstalledPortVersion = 0
        $assPortVersionMatch = [regex]::Match($assRecords[0], '(?m)^Port-Version: (\d+)\r?$')
        if ($assPortVersionMatch.Success) { $assInstalledPortVersion = [int]$assPortVersionMatch.Groups[1].Value }
        if ($assInstalledPortVersion -ne $assLibrary.portVersion) { throw "The installed libass dependency port revision differs from its lock: $($assLibrary.id)" }
        $assLists = @(Get-ChildItem -LiteralPath (Join-Path $assInstallRoot 'vcpkg/info') -Filter ($assLibrary.id + '_*_' + $assLock.vcpkg.triplet + '.list') -File)
        if ($assLists.Count -ne 1) { throw "Expected one vcpkg file inventory for $($assLibrary.id)." }
        $assLibraryDlls = @()
        $assDllPrefix = $assLock.vcpkg.triplet + '/bin/'
        foreach ($assInstalledPath in Get-Content -LiteralPath $assLists[0].FullName) {
            $assInstalledPath = $assInstalledPath.Trim().Replace('\', '/')
            if ($assInstalledPath.StartsWith($assDllPrefix, [StringComparison]::OrdinalIgnoreCase) -and $assInstalledPath.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
                $assDllName = $assInstalledPath.Substring($assDllPrefix.Length)
                if ([IO.Path]::GetFileName($assDllName) -ne $assDllName -or $assOwners.ContainsKey($assDllName)) { throw 'The libass runtime inventory contains a duplicate or nested DLL.' }
                $assOwners[$assDllName] = $assLibrary.id
                $assLibraryDlls += $assDllName
            }
        }
        $assCopyright = Join-Path $assNativeRoot ('share/' + $assLibrary.id + '/copyright')
        if (!(Test-Path -LiteralPath $assCopyright)) { throw "The installed library notice is missing: $($assLibrary.id)" }
        $assLibraryInventory += [ordered]@{ id = $assLibrary.id; version = $assLibrary.version; portVersion = $assLibrary.portVersion; license = $assLibrary.license; runtimeFiles = @($assLibraryDlls | Sort-Object); installedCopyrightSha256 = Get-AssSha256 $assCopyright }
    }
    foreach ($assRequired in @('include/ass/ass.h', 'include/ass/ass_types.h', ('lib/' + $assLock.runtime.importLibrary), ('bin/' + $assLock.runtime.name))) {
        if (!(Test-Path -LiteralPath (Join-Path $assNativeRoot $assRequired))) { throw "A required libass development/runtime file is missing: $assRequired" }
    }
    $assDllFiles = @(Get-ChildItem -LiteralPath (Join-Path $assNativeRoot 'bin') -Filter '*.dll' -File | Sort-Object Name)
    if ($assDllFiles.Count -ne $assOwners.Count) { throw 'The isolated libass installation contains an unowned runtime DLL.' }
    $assSystemDlls = @('kernel32.dll', 'kernelbase.dll', 'ntdll.dll', 'user32.dll', 'gdi32.dll', 'advapi32.dll', 'ole32.dll', 'oleaut32.dll', 'shell32.dll', 'shlwapi.dll', 'bcrypt.dll', 'ws2_32.dll', 'rpcrt4.dll', 'dwrite.dll', 'msvcrt.dll', 'ucrtbase.dll', 'version.dll', 'winmm.dll', 'imm32.dll', 'combase.dll', 'secur32.dll')
    $assRuntimeInventory = @()
    foreach ($assDll in $assDllFiles) {
        if (!$assOwners.ContainsKey($assDll.Name)) { throw "An installed libass runtime DLL has no locked library owner: $($assDll.Name)" }
        $assImports = @(Get-AssDllImports $assDumpbin $assDll.FullName $assLogRoot)
        foreach ($assImport in $assImports) {
            if (!$assOwners.ContainsKey($assImport) -and $assImport -notin $assSystemDlls -and $assImport -notmatch '^(api-ms-win-|ext-ms-).*\.dll$') {
                throw "The libass runtime closure is incomplete: $($assDll.Name) imports $assImport."
            }
        }
        Copy-Item -LiteralPath $assDll.FullName -Destination $assRuntimeRoot -Force
        $assRuntimeInventory += [ordered]@{ name = $assDll.Name; libraryId = $assOwners[$assDll.Name]; size = $assDll.Length; sha256 = Get-AssSha256 $assDll.FullName; imports = $assImports }
    }
    foreach ($assOldDll in Get-ChildItem -LiteralPath $assRuntimeRoot -Filter '*.dll' -File) {
        if (!$assOwners.ContainsKey($assOldDll.Name)) { Remove-Item -LiteralPath $assOldDll.FullName }
    }
    $assDevelopmentFiles = @(Get-ChildItem -LiteralPath (Join-Path $assNativeRoot 'include/ass'), (Join-Path $assNativeRoot 'lib') -File | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($assNativeRoot, $_.FullName).Replace('\', '/'); sha256 = Get-AssSha256 $_.FullName }
    })
    $assReceipt = [ordered]@{
        schemaVersion = 1
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        sourceFingerprint = $assFingerprint
        lockSha256 = Get-AssSha256 $assLockPath
        sourceFiles = $assSourceInventory
        vcpkgCommit = $assLock.vcpkg.commit
        vcpkgToolSha256 = $assLock.vcpkg.tool.sha256
        triplet = $assLock.vcpkg.triplet
        visualStudio = $VisualStudioRoot
        msvcVersion = $MsvcVersion
        compilerSha256 = Get-AssSha256 $assCompiler
        windowsSdkVersion = $WindowsSdkVersion
        fontProvider = $assLock.runtime.fontProvider
        wrapUnicode = $assLock.runtime.wrapUnicode
        crtLinkage = $assLock.runtime.crtLinkage
        runtimeFiles = $assRuntimeInventory
        developmentFiles = $assDevelopmentFiles
        libraries = $assLibraryInventory
    }
    $assReceipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $assReceiptPath -Encoding utf8NoBOM
    return Get-AssDependencyResult $assReceipt
}
finally {
    foreach ($assEntry in $assSavedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($assEntry.Key, $assEntry.Value, 'Process') }
    if ($assMutexAcquired) { $assBuildMutex.ReleaseMutex() }
    $assBuildMutex.Dispose()
}
