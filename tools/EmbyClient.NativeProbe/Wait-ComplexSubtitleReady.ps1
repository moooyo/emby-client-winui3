param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [int]$ProbeProcessId,
    [ValidateSet('Enabled', 'Disabled', 'TailFrame')]
    [string]$Phase = 'Enabled',
    [ValidateRange(1, 55)]
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$readyPath = Join-Path $output ('complex-subtitle-' + $Phase.ToLowerInvariant() + '-ready.json')
$reportPath = Join-Path $output 'complex-subtitle-report.json'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

function Read-SharedJson([string]$LiteralPath) {
    $stream = [System.IO.FileStream]::new($LiteralPath, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try { $text = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
    return ($text | ConvertFrom-Json)
}

do {
    $process = Get-Process -Id $ProbeProcessId -ErrorAction SilentlyContinue
    $running = $null -ne $process
    if ($null -ne $process) { $process.Dispose() }
    if (-not $running) {
        if (Test-Path -LiteralPath $reportPath) {
            $completed = Read-SharedJson $reportPath
            [pscustomobject]@{
                Result = 'ProcessExited'
                Phase = $Phase
                Report = $completed | Select-Object RunId, ProcessId, Status, ErrorCode, FailureStage, ErrorType,
                    ErrorHResult, OutputFailureOperation, FirstPlaybackFailureCode, FinishedUtc
            } | ConvertTo-Json -Depth 5
        }
        else { [pscustomobject]@{ Result = 'ProcessExitedWithoutReport'; Phase = $Phase } | ConvertTo-Json }
        return
    }
    if (Test-Path -LiteralPath $readyPath) {
        [pscustomobject]@{ Result = 'ReadyObserved'; Phase = $Phase; Ready = Read-SharedJson $readyPath } | ConvertTo-Json -Depth 8
        return
    }
    # Observe existence while running; do not repeatedly open the mutable report.
    Start-Sleep -Milliseconds 100
} while ([DateTimeOffset]::UtcNow -lt $deadline)

[pscustomobject]@{ Result = 'WaitExpired'; Phase = $Phase; ProbeProcessId = $ProbeProcessId } | ConvertTo-Json
