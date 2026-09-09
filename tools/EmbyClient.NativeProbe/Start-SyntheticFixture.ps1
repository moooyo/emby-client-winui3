param(
    [string]$SourceMediaDirectory,
    [ValidateRange(1, 180)][int]$ExpectedDurationSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/probes'))
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot ('fixture-runtime-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$sourceDirectory = Join-Path $repositoryRoot 'tools/EmbyClient.FixtureServer/bin/Release/net10.0'
$mediaDirectory = if ($SourceMediaDirectory) { [IO.Path]::GetFullPath($SourceMediaDirectory) }
    else { Join-Path $repositoryRoot 'tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds' }
if (-not $runtimeDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The fixture runtime must remain inside the probe artifacts directory.'
}
if (Test-Path -LiteralPath $runtimeDirectory) { throw 'Select a new fixture runtime directory.' }
if (Get-NetTCPConnection -LocalPort 18961 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 18961 is already owned by another listener.' }
$metadataPath = Join-Path $mediaDirectory 'fixture-h264-aac.json'
$mediaPath = Join-Path $mediaDirectory 'fixture-h264-aac.mp4'
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.Synthetic -ne $true -or [Math]::Abs($metadata.DurationTicks - $ExpectedDurationSeconds * 10000000L) -gt 1000000 `
    -or $metadata.FileLength -ne (Get-Item -LiteralPath $mediaPath).Length) { throw 'Expected the generated synthetic media with the requested duration.' }
$sourceExecutable = Join-Path $sourceDirectory 'EmbyClient.FixtureServer.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable)) { throw 'Build the fixture server separately before running this launcher.' }
New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null
Get-ChildItem -LiteralPath $sourceDirectory | Copy-Item -Destination $runtimeDirectory -Recurse
$executable = Join-Path $runtimeDirectory 'EmbyClient.FixtureServer.exe'
$process = Start-Process -FilePath $executable -WorkingDirectory $runtimeDirectory `
    -ArgumentList @('--port', '18961', '--media-dir', ('"' + $mediaDirectory + '"')) -WindowStyle Hidden -PassThru
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($process.HasExited) { break }
    try {
        $stats = Invoke-RestMethod -Uri 'http://127.0.0.1:18961/_fixture/stats' -TimeoutSec 1
        if ($stats.Synthetic -eq $true) { $ready = $true; break }
    }
    catch { Start-Sleep -Milliseconds 100 }
}
if (-not $ready) { throw ('The copied fixture did not become ready. Inspect owned PID ' + $process.Id + '. No other process was stopped.') }
$ownership = [ordered]@{
    ProcessId = $process.Id
    RuntimeDirectory = $runtimeDirectory
    Executable = $executable
    ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    MediaSha256 = (Get-FileHash -LiteralPath $mediaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    MetadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Port = 18961
    Synthetic = $true
}
$ownership | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'fixture-ownership.json') -Encoding utf8
$runtimeDirectory | Set-Content -LiteralPath (Join-Path $artifactRoot 'active-fixture-runtime.txt')
$ownership | ConvertTo-Json
