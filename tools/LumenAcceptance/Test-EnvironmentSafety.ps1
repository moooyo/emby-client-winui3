Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EnvironmentHelpers.ps1')
$assertions = 0

function Assert-Safety {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertions++
}

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactRoot = Join-Path $workspaceRoot 'artifacts/lumen-acceptance'
$run = Join-Path $artifactRoot 'safety-check-only'
Assert-Safety (Test-LumenChildPath -Path $run -Parent $artifactRoot) 'The legitimate run child was rejected.'
Assert-Safety (-not (Test-LumenChildPath -Path $artifactRoot -Parent $artifactRoot)) 'The artifact root was accepted as a run.'
Assert-Safety (-not (Test-LumenChildPath -Path ($artifactRoot + '-sibling/run') -Parent $artifactRoot)) 'A shared-prefix sibling was accepted.'
Assert-Safety (-not (Test-LumenChildPath -Path (Join-Path $run '../../outside') -Parent $artifactRoot)) 'Parent traversal escaped its boundary.'
Assert-LumenRunDirectory -RunDirectory $run -WorkspaceRoot $workspaceRoot
$assertions++
$rejected = $false
try { Assert-LumenRunDirectory -RunDirectory (Join-Path $workspaceRoot 'outside') -WorkspaceRoot $workspaceRoot }
catch { $rejected = $true }
Assert-Safety $rejected 'An unrelated output directory was accepted.'

# Observe this existing test shell only. No process is launched, signaled, or stopped.
$self = Get-Process -Id $PID
$ownership = [ordered]@{
    FixtureProcessId = $PID
    FixtureExecutable = $self.Path
    FixtureExecutableSha256 = (Get-FileHash -LiteralPath $self.Path -Algorithm SHA256).Hash
    FixtureStartedAtUtc = $self.StartTime.ToUniversalTime().ToString('O')
} | ConvertTo-Json | ConvertFrom-Json
$observed = Get-LumenOwnedProcess $ownership
Assert-Safety ($observed.Id -eq $PID) 'JSON date conversion lost the exact owned process identity.'
$ownership.FixtureExecutableSha256 = 'not-the-owned-hash'
$rejected = $false
try { Get-LumenOwnedProcess $ownership | Out-Null }
catch { $rejected = $true }
Assert-Safety $rejected 'An altered executable hash was accepted.'
$ownership.FixtureExecutableSha256 = (Get-FileHash -LiteralPath $self.Path -Algorithm SHA256).Hash
$ownership.FixtureStartedAtUtc = $self.StartTime.ToUniversalTime().AddSeconds(-1).ToString('O')
$rejected = $false
try { Get-LumenOwnedProcess $ownership | Out-Null }
catch { $rejected = $true }
Assert-Safety $rejected 'An altered process start time was accepted.'
$tokens = $null
$parseErrors = $null
$launcherAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Start-App.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Safety ($parseErrors.Count -eq 0) 'The frozen app launcher did not parse.'
$globalEnvironmentWrites = @($launcherAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.IsDriveQualified -and $node.Left.VariablePath.DriveName -eq 'env'
}, $true))
Assert-Safety ($globalEnvironmentWrites.Count -eq 0) 'The launcher assigns a global environment variable.'
$launchCommands = @($launcherAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Start-Process'
}, $true))
Assert-Safety ($launchCommands.Count -eq 1) 'The launcher must have one explicit owned process start.'
Write-Output ("Environment safety checks passed: {0}. No process or desktop state was changed." -f $assertions)
