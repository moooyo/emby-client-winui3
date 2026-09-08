param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    dotnet test --project tests/EmbyClient.Api.Tests/EmbyClient.Api.Tests.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'API contract tests failed.' }
}
finally { Pop-Location }
