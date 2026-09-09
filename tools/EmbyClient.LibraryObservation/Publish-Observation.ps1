#requires -Version 7.0
param(
    [string]$OutputDirectory,
    [switch]$VerifyNormalBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$observationRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$observationArtifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $OutputDirectory) {
    $name = 'publish-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $observationArtifacts $name
}
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $observationRepository $OutputDirectory }
$observationPublish = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $observationPublish.StartsWith($observationArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Observation output must be under tools/EmbyClient.LibraryObservation/artifacts.'
}
if (Test-Path -LiteralPath $observationPublish) { throw 'Select a new observation publish directory; existing outputs are never overwritten.' }
$ancestor = Split-Path -Parent $observationPublish
while ($ancestor -and $ancestor.StartsWith($observationRepository, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Observation outputs cannot traverse a reparse point.'
    }
    $ancestor = Split-Path -Parent $ancestor
}
$observationRun = Join-Path $observationArtifacts ('build-' + [IO.Path]::GetFileName($observationPublish))
if (Test-Path -LiteralPath $observationRun) { throw 'The matching build directory already exists. Select a new publish directory.' }
[void](New-Item -ItemType Directory -Path $observationRun -Force)

function Get-ObservationSources {
    $paths = @(& rg --files src tools/EmbyClient.LibraryObservation `
        -g '*.cs' -g '*.xaml' -g '*.csproj' -g '*.manifest' -g '*.appxmanifest' -g '*.ps1' -g 'packages.lock.json' -g '*.png' -g '*.ico')
    if ($LASTEXITCODE -ne 0) { throw 'Observation source enumeration failed.' }
    $paths += @('global.json', 'Directory.Build.props', 'Directory.Packages.props')
    foreach ($relativePath in ($paths | Sort-Object -Unique)) {
        [pscustomobject]@{
            Path = $relativePath.Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath (Join-Path $observationRepository $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

function Get-CompileObservation([string]$Mode) {
    $arguments = @('msbuild', 'src/EmbyClient.App/EmbyClient.App.csproj', '-nologo', '-verbosity:quiet',
        '-p:Configuration=Release', '-p:Platform=x64',
        '-getProperty:LibraryObservation,PublishAot,TargetFramework,RuntimeIdentifier,DefineConstants', '-getItem:Compile')
    if ($Mode) { $arguments += "-p:LibraryObservation=$Mode" }
    $text = @(& dotnet @arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Compile item evaluation failed.' }
    $result = ($text -join [Environment]::NewLine) | ConvertFrom-Json
    $identities = @($result.Items.Compile | ForEach-Object { $_.Identity.Replace('\', '/') } | Sort-Object)
    [ordered]@{
        LibraryObservation = [string]$result.Properties.LibraryObservation
        PublishAot = [string]$result.Properties.PublishAot
        TargetFramework = [string]$result.Properties.TargetFramework
        RuntimeIdentifier = [string]$result.Properties.RuntimeIdentifier
        ObservationSymbolDefined = @(([string]$result.Properties.DefineConstants -split ';') | Where-Object { $_ -eq 'LIBRARY_OBSERVATION' }).Count -eq 1
        ObservationSourceCount = @($identities | Where-Object { $_.EndsWith('/LibraryView.Observation.cs', [StringComparison]::OrdinalIgnoreCase) }).Count
        CompileItems = $identities
    }
}

Push-Location -LiteralPath $observationRepository
try {
    $sourcesBefore = @(Get-ObservationSources)
    $beforeJson = ConvertTo-Json -InputObject $sourcesBefore -Depth 5 -Compress
    $defaultCompile = Get-CompileObservation ''
    $enabledCompile = Get-CompileObservation 'true'
    if ($defaultCompile.LibraryObservation -ne 'false' -or $defaultCompile.ObservationSourceCount -ne 0 -or $defaultCompile.ObservationSymbolDefined) {
        throw 'The default app unexpectedly includes observation code.'
    }
    if ($enabledCompile.LibraryObservation -ne 'true' -or $enabledCompile.ObservationSourceCount -ne 1 -or $enabledCompile.PublishAot -ne 'true' -or -not $enabledCompile.ObservationSymbolDefined) {
        throw 'The enabled Release build does not contain exactly one observation source and Native AOT.'
    }
    [ordered]@{ Default = $defaultCompile; Enabled = $enabledCompile } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $observationRun 'compile-inputs.json') -Encoding utf8

    if ($VerifyNormalBuild) {
        & dotnet build src/EmbyClient.App/EmbyClient.App.csproj -c Release -p:Platform=x64 -p:LibraryObservation=false `
            --artifacts-path (Join-Path $observationRun 'normal-build') -p:RestoreLockedMode=true --nologo 2>&1 |
            Tee-Object -FilePath (Join-Path $observationRun 'normal-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'The normal app Release build failed.' }
    }

    $enabledArtifacts = Join-Path $observationRun 'observation-build'
    & dotnet publish src/EmbyClient.App/EmbyClient.App.csproj -c Release -p:Platform=x64 -p:LibraryObservation=true `
        --artifacts-path $enabledArtifacts -o $observationPublish -p:RestoreLockedMode=true --nologo 2>&1 |
        Tee-Object -FilePath (Join-Path $observationRun 'observation-publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Observation Native AOT publication failed.' }
    $sourcesAfter = @(Get-ObservationSources)
    if ($beforeJson -cne (ConvertTo-Json -InputObject $sourcesAfter -Depth 5 -Compress)) {
        throw 'Source inputs changed during verification; this output is not an auditable observation build.'
    }
    $assets = @(Get-ChildItem -LiteralPath $enabledArtifacts -Recurse -Filter project.assets.json | ForEach-Object {
        $candidate = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($candidate.project.restore.projectName -eq 'EmbyClient.App') { $candidate }
    })
    if ($assets.Count -ne 1) { throw 'Exactly one restored app asset graph is required.' }
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'SDK version collection failed.' }
    $executable = Get-Item -LiteralPath (Join-Path $observationPublish 'EmbyClient.App.exe')
    $manifest = [ordered]@{
        FormatVersion = 1
        Purpose = 'Conditional library lifetime observation; not a distribution or acceptance build'
        GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        LibraryObservation = $true
        NormalReleaseBuildVerified = [bool]$VerifyNormalBuild
        DefaultCompileExcludesObservation = $true
        EnabledCompileObservationSourceCount = $enabledCompile.ObservationSourceCount
        NativeAotRequested = $true
        DotNetSdk = $sdk
        RuntimeIdentifier = $enabledCompile.RuntimeIdentifier
        TargetFramework = $enabledCompile.TargetFramework
        SourceInputsUnchangedDuringBuild = $true
        Sources = $sourcesBefore
        ResolvedDependencies = @($assets[0].libraries.PSObject.Properties.Name | Sort-Object)
        Executable = [ordered]@{
            FileName = $executable.Name
            SizeBytes = $executable.Length
            Sha256 = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        RuntimeObservation = [ordered]@{
            SchemaVersion = 2
            FileName = 'library-observation.jsonl'
            MaximumBytes = 1048576
            MaximumSeconds = 1800
            SampleIntervalSeconds = 5
            WeakCapacityPerType = 2048
            ForcedGc = $false
            AutomaticInteraction = $false
        }
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $observationPublish 'library-observation-build.json') -Encoding utf8
    Write-Output "Observation publish: $observationPublish"
    Write-Output "Build and compile evidence: $observationRun"
    Write-Output "Executable SHA256: $($manifest.Executable.Sha256)"
    Write-Output 'The observation application was not started. Runtime counters are observations, not acceptance results.'
}
finally { Pop-Location }
