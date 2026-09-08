param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    $projects = Get-ChildItem -LiteralPath tests -Recurse -Filter '*.Tests.csproj' | Sort-Object FullName
    foreach ($project in $projects) {
        dotnet test --project $project.FullName --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.Name)" }
    }
}
finally { Pop-Location }
