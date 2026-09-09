param(
    [Parameter(Mandatory = $true)]
    [string]$MediaPath,
    [ValidateSet('Aot', 'JitBaseline')]
    [string]$Mode = 'Aot',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'EmbyClient.VlcProbe.csproj'
$fixturePath = (Resolve-Path -LiteralPath $MediaPath).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot ('artifacts\' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$runDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $runDirectory) {
    throw 'The output directory already exists. Select a new directory to preserve previous evidence.'
}
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$publishDirectory = Join-Path $runDirectory 'publish'
$arguments = @('publish', $projectPath, '-c', 'Release', '-p:Platform=x64', '-p:TrimmerSingleWarn=false', '-o', $publishDirectory, '-v:minimal')
if ($Mode -eq 'JitBaseline') {
    # This comparison cannot establish NativeAOT or trimming compatibility.
    $arguments = @('publish', $projectPath, '-c', 'JitBaseline', '-p:Platform=x64', '-p:PublishAot=false', '-p:PublishTrimmed=false', '-o', $publishDirectory, '-v:minimal')
}
& dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'publish.log')
if ($LASTEXITCODE -ne 0) {
    throw "Publication failed with exit code $LASTEXITCODE. Read publish.log."
}

$probeExe = Join-Path $publishDirectory 'EmbyClient.VlcProbe.exe'
$processArguments = @('--media', ('"{0}"' -f $fixturePath), '--output-dir', ('"{0}"' -f $runDirectory))
$probeProcess = Start-Process -FilePath $probeExe -ArgumentList $processArguments -WindowStyle Hidden -PassThru
$timedOut = $false
try {
    $probeProcess.Id | Set-Content -LiteralPath (Join-Path $runDirectory 'process-id.txt')
    Write-Output "Probe process ID: $($probeProcess.Id)"
    if (-not $probeProcess.WaitForExit(45000)) {
        $timedOut = $true
        Stop-Process -InputObject $probeProcess
        $probeProcess.WaitForExit()
    }
    $resultPath = Join-Path $runDirectory 'result.json'
    $run = [ordered]@{
        Mode = $Mode
        ProcessId = $probeProcess.Id
        ProcessExited = $probeProcess.HasExited
        ExitCode = $probeProcess.ExitCode
        TimedOut = $timedOut
        ExecutableSha256 = (Get-FileHash -LiteralPath $probeExe -Algorithm SHA256).Hash
        FixtureSha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
        CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $run | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'process-result.json')
    if (Test-Path -LiteralPath $resultPath) {
        Get-Content -LiteralPath $resultPath
    }
    if ($timedOut -or $probeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw 'The runtime probe failed. Read result.json and process-result.json.'
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.Status -ne 'Passed') {
        throw 'The runtime result did not pass. Read result.json.'
    }
}
finally {
    if (-not $probeProcess.HasExited) {
        Stop-Process -InputObject $probeProcess
        $probeProcess.WaitForExit()
    }
    $probeProcess.Dispose()
}
