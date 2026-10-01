param(
    [ValidateSet('Release_Desktop', 'Debug_Desktop')]
    [string] $Configuration = 'Release_Desktop',
    [string] $NativeRoot,
    [string] $DependencyRoot,
    [string] $NuGetRoot,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (!$NativeRoot) { $NativeRoot = Join-Path $repositoryRoot 'artifacts/decoder-apis/native' }
$NativeRoot = [IO.Path]::GetFullPath($NativeRoot)
$mutexName = 'Local\EmbyDecoderNative-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($NativeRoot.ToLowerInvariant()))).Substring(0, 24)
$buildMutex = [Threading.Mutex]::new($false, $mutexName)
$mutexAcquired = $false
try {
    $waitStarted = [DateTime]::UtcNow
    while (!$mutexAcquired) {
        try { $mutexAcquired = $buildMutex.WaitOne(1000) }
        catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
        if (!$mutexAcquired -and ([DateTime]::UtcNow - $waitStarted).TotalMinutes -gt 10) { throw 'Timed out waiting for another native decoder build.' }
    }
$dependencies = & (Join-Path $PSScriptRoot 'Prepare-DecoderDependencies.ps1') -DependencyRoot $DependencyRoot -NuGetRoot $NuGetRoot
$lock = Get-Content (Join-Path $repositoryRoot 'native/FFmpegInteropX/dependencies.lock.json') -Raw | ConvertFrom-Json
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with the x64 C++ build tools is required.' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsRoot) { throw 'Visual Studio x64 C++ build tools were not found.' }
$msbuild = Join-Path $vsRoot 'MSBuild/Current/Bin/MSBuild.exe'
$msvcVersion = (Get-Content (Join-Path $vsRoot 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$compiler = Join-Path $vsRoot "VC/Tools/MSVC/$msvcVersion/bin/Hostx64/x64/cl.exe"
$dumpbin = Join-Path $vsRoot "VC/Tools/MSVC/$msvcVersion/bin/Hostx64/x64/dumpbin.exe"
$sdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdkMetadata = Join-Path $sdkRoot "UnionMetadata/$($lock.toolchain.windowsSdkVersion)/Windows.winmd"
if (!(Test-Path -LiteralPath $sdkMetadata)) { throw "Windows SDK $($lock.toolchain.windowsSdkVersion) is required." }
$cppWinRt = Join-Path $dependencies.NuGetRoot "microsoft.windows.cppwinrt/$($lock.toolchain.cppWinRtVersion)/bin/cppwinrt.exe"
$winmd = Join-Path $dependencies.NuGetRoot 'ffmpeginteropx.desktop.lib/2.1.0/runtimes/win-x64/native/FFmpegInteropX.winmd'
$interactiveMetadata = Join-Path $dependencies.NuGetRoot "microsoft.windowsappsdk.interactiveexperiences/$($lock.toolchain.windowsAppSdkInteractiveExperiencesVersion)/metadata/10.0.18362.0"
$foundationMetadata = Join-Path $dependencies.NuGetRoot "microsoft.windowsappsdk.foundation/$($lock.toolchain.windowsAppSdkFoundationVersion)/metadata"
$winUiMetadata = Join-Path $dependencies.NuGetRoot "microsoft.windowsappsdk.winui/$($lock.toolchain.windowsAppSdkWinUiVersion)/metadata"
$metadata = @(Get-ChildItem -LiteralPath $interactiveMetadata,$foundationMetadata,$winUiMetadata -Filter '*.winmd' -File | Select-Object -ExpandProperty FullName)
$metadata += Join-Path $dependencies.NuGetRoot "microsoft.web.webview2/$($lock.toolchain.webView2Version)/lib/Microsoft.Web.WebView2.Core.winmd"
$sourceRoot = Join-Path $repositoryRoot 'native/FFmpegInteropX/Source'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object Extension -in '.cpp', '.h', '.idl', '.def', '.rc', '.vcxproj')
$sourceFiles += Get-Item (Join-Path $repositoryRoot 'native/FFmpegInteropX/DecoderNative.props'), (Join-Path $repositoryRoot 'native/FFmpegInteropX/dependencies.lock.json'), $PSCommandPath, (Join-Path $PSScriptRoot 'Prepare-DecoderDependencies.ps1')
$fingerprintInput = ($sourceFiles | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($repositoryRoot.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
$fingerprintInput += "`n$Configuration`n$vsRoot`n$($lock.toolchain.windowsSdkVersion)`n$($dependencies.DependencyFingerprint)"
foreach ($path in @($msbuild, $compiler, $cppWinRt, $sdkMetadata, $winmd) + $metadata) {
    $fingerprintInput += "`n$path`:$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
}
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprintInput))).ToLowerInvariant()
$runtimeRoot = Join-Path $NativeRoot 'runtime'
$receiptPath = Join-Path $NativeRoot 'build-receipt.json'
if (!$Force -and (Test-Path -LiteralPath $receiptPath) -and (Test-Path (Join-Path $runtimeRoot 'FFmpegInteropX.dll'))) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.sourceFingerprint -ceq $fingerprint) {
        $payloadValid = $true
        foreach ($file in $receipt.runtimeFiles) {
            $path = Join-Path $runtimeRoot $file.name
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { $payloadValid = $false; break }
        }
        if ($payloadValid) { Write-Output "Native decoder build is current: $runtimeRoot"; return }
    }
}

New-Item -ItemType Directory -Path $NativeRoot,$runtimeRoot -Force | Out-Null
$generatedRoot = Join-Path $NativeRoot 'generated'
New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
# Generate consuming and component projections with the same locked cppwinrt version.
$projectionArguments = @('-input', $sdkMetadata)
foreach ($path in $metadata) { $projectionArguments += @('-input', $path) }
$projectionArguments += @('-output', $generatedRoot)
& $cppWinRt @projectionArguments
if ($LASTEXITCODE -ne 0) { throw 'C++/WinRT consuming projection generation failed.' }
$componentArguments = @('-input', $winmd, '-reference', $sdkMetadata)
foreach ($path in $metadata) { $componentArguments += @('-reference', $path) }
$componentArguments += @('-component', '-optimize', '-pch', 'pch.h', '-output', $generatedRoot)
& $cppWinRt @componentArguments
if ($LASTEXITCODE -ne 0) { throw 'C++/WinRT component projection generation failed.' }

$ffmpeg = Join-Path $dependencies.FFmpegRoot 'bin/ffmpeg.exe'
$versionOutput = & $ffmpeg -hide_banner -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'The locked FFmpeg binary could not be loaded.' }
$decodersOutput = & $ffmpeg -hide_banner -decoders 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'The locked FFmpeg decoder inventory could not be read.' }
foreach ($decoder in $lock.ffmpeg.requiredDecoders) {
    if ($decodersOutput -notmatch "(?m)\s$([regex]::Escape($decoder))\s") { throw "The locked FFmpeg build is missing $decoder." }
}
Set-Content -LiteralPath (Join-Path $NativeRoot 'ffmpeg-version.txt') -Value $versionOutput
Set-Content -LiteralPath (Join-Path $NativeRoot 'ffmpeg-decoders.txt') -Value $decodersOutput
$logPath = Join-Path $NativeRoot 'native-build.log'
$arguments = @((Join-Path $sourceRoot 'FFmpegInteropX.vcxproj'), '/m', '/nologo', '/verbosity:minimal', "/p:Configuration=$Configuration", '/p:Platform=x64', "/p:DecoderNativeRoot=$NativeRoot\", "/p:DecoderFFmpegRoot=$($dependencies.FFmpegRoot)\", "/p:WindowsTargetPlatformVersion=$($lock.toolchain.windowsSdkVersion)")
& $msbuild @arguments 2>&1 | Tee-Object -FilePath $logPath
if ($LASTEXITCODE -ne 0) { throw "Native decoder build failed. See $logPath" }
$exports = & $dumpbin /NOLOGO /EXPORTS (Join-Path $runtimeRoot 'FFmpegInteropX.dll') | Out-String
if ($LASTEXITCODE -ne 0) { throw 'The custom decoder export inventory could not be read.' }
foreach ($symbol in 'EmbyDecoderApiVersion', 'EmbyConfigureDecoderApi', 'EmbyGetDecoderObservation') {
    if ($exports -notmatch "(?m)\s$([regex]::Escape($symbol))(?:\s|$)") { throw "The custom native decoder is missing $symbol." }
}
Set-Content -LiteralPath (Join-Path $NativeRoot 'native-exports.txt') -Value $exports
# Copy every DLL from this archive and preserve the unchanged managed projection ABI.
Get-ChildItem (Join-Path $dependencies.FFmpegRoot 'bin') -Filter '*.dll' -File | Copy-Item -Destination $runtimeRoot -Force
Copy-Item -LiteralPath $winmd -Destination $runtimeRoot -Force
Copy-Item (Join-Path $dependencies.NuGetRoot 'ffmpeginteropx.desktop.lib/2.1.0/runtimes/win-x64/native/FFmpegInteropX.xml') -Destination $runtimeRoot -Force
$runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeRoot -File | Where-Object Extension -in '.dll', '.winmd', '.xml' | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
[ordered]@{
    schemaVersion = 1
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceFingerprint = $fingerprint
    sourceFiles = @($sourceFiles | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($repositoryRoot.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    visualStudio = $vsRoot
    msbuildVersion = (Get-Item -LiteralPath $msbuild).VersionInfo.FileVersion
    msvcVersion = $msvcVersion
    compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    windowsSdkVersion = $lock.toolchain.windowsSdkVersion
    cppWinRtVersion = $lock.toolchain.cppWinRtVersion
    ffmpegArchiveSha256 = $dependencies.ArchiveSha256
    ffmpegDevelopmentArchiveSha256 = $dependencies.DevelopmentArchiveSha256
    ffmpegDependencyFingerprint = $dependencies.DependencyFingerprint
    runtimeFiles = $runtimeFiles
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath
Write-Output "Native decoder runtime: $runtimeRoot"
}
finally {
    if ($mutexAcquired) { $buildMutex.ReleaseMutex() }
    $buildMutex.Dispose()
}
