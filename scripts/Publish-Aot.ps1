param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts/aot' }
Push-Location $workspace
try {
    dotnet publish src/EmbyClient.App/EmbyClient.App.csproj -c Release -p:Platform=x64 -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT publishing failed.' }
    $sbomPath = Join-Path $workspace 'artifacts/sbom/emby-client-win-x64.cdx.json'
    & (Join-Path $PSScriptRoot 'Generate-Sbom.ps1') -OutputPath $sbomPath
    Copy-Item -LiteralPath $sbomPath -Destination (Join-Path $OutputDirectory 'emby-client-win-x64.cdx.json') -Force
    Write-Output "Native AOT output: $OutputDirectory"
}
finally { Pop-Location }
