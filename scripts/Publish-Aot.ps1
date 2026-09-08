param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts/aot' }
Push-Location $workspace
try {
    dotnet publish src/EmbyClient.App/EmbyClient.App.csproj -c Release -r win-x64 -p:Platform=x64 -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT publishing failed.' }
    Write-Output "Native AOT output: $OutputDirectory"
}
finally { Pop-Location }
