param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $publishDirectory 'build-manifest.json')) {
    throw 'A build manifest already exists in this output directory. Select a new directory.'
}

function Get-ProbeSourceHashes {
    $paths = @(& rg --files src/EmbyClient.Api src/EmbyClient.Playback src/EmbyClient.App/Playback tools/EmbyClient.NativeProbe `
        -g '*.cs' -g '*.csproj' -g '*.xaml' -g '*.manifest' -g 'packages.lock.json' -g '*.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Source enumeration failed.' }
    $paths += @('global.json', 'Directory.Build.props', 'Directory.Packages.props', 'src/EmbyClient.App/Services/NativePosterDecoder.cs')
    foreach ($relativePath in ($paths | Sort-Object -Unique)) {
        $absolutePath = Join-Path $repositoryRoot $relativePath
        [pscustomobject]@{
            Path = $relativePath.Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath $absolutePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

Push-Location -LiteralPath $repositoryRoot
try {
    $before = @(Get-ProbeSourceHashes)
    $beforeJson = ConvertTo-Json -InputObject $before -Depth 5 -Compress
    & dotnet publish tools/EmbyClient.NativeProbe/EmbyClient.NativeProbe.csproj -c Release -p:Platform=x64 -o $publishDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'NativeAOT publication failed.' }
    $after = @(Get-ProbeSourceHashes)
    $afterJson = ConvertTo-Json -InputObject $after -Depth 5 -Compress
    if ($beforeJson -cne $afterJson) { throw 'Source inputs changed during publication; this build is not auditable.' }
    $executable = Get-Item -LiteralPath (Join-Path $publishDirectory 'EmbyClient.NativeProbe.exe')
    $assets = Get-Content -LiteralPath 'tools/EmbyClient.NativeProbe/obj/project.assets.json' -Raw | ConvertFrom-Json
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'SDK version collection failed.' }
    $manifest = [ordered]@{
        FormatVersion = 1
        Configuration = 'Release'
        RuntimeIdentifier = 'win-x64'
        TargetFramework = 'net10.0-windows10.0.26100.0'
        NativeAotRequested = $true
        DotNetSdk = $sdkVersion
        GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SourceInputsUnchangedDuringPublish = $true
        Sources = $before
        ResolvedDependencies = @($assets.libraries.PSObject.Properties.Name | Sort-Object)
        Executable = [ordered]@{
            FileName = $executable.Name
            SizeBytes = $executable.Length
            Sha256 = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishDirectory 'build-manifest.json') -Encoding utf8
}
finally { Pop-Location }
