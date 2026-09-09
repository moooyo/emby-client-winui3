#Requires -Version 7.0

function Invoke-FixtureTool {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [Parameter(Mandatory)][string] $StateDirectory,
        [Parameter(Mandatory)][string] $LogName,
        [int] $TimeoutSeconds = 120
    )

    $taskHomeDirectory = Join-Path $StateDirectory 'home'
    $temporaryDirectory = Join-Path $StateDirectory 'temp'
    $logDirectory = Join-Path $StateDirectory 'logs'
    New-Item -ItemType Directory -Path $taskHomeDirectory, $temporaryDirectory, $logDirectory -Force | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment.Clear()
    $startInfo.Environment['SystemRoot'] = $env:SystemRoot
    $startInfo.Environment['WINDIR'] = $env:WINDIR
    $startInfo.Environment['PATH'] = (Split-Path -Parent $FilePath) + ';' + (Join-Path $env:SystemRoot 'System32')
    $startInfo.Environment['USERPROFILE'] = $taskHomeDirectory
    $startInfo.Environment['HOME'] = $taskHomeDirectory
    $startInfo.Environment['APPDATA'] = Join-Path $taskHomeDirectory 'AppData/Roaming'
    $startInfo.Environment['LOCALAPPDATA'] = Join-Path $taskHomeDirectory 'AppData/Local'
    $startInfo.Environment['TEMP'] = $temporaryDirectory
    $startInfo.Environment['TMP'] = $temporaryDirectory
    $startInfo.Environment['DOTNET_CLI_HOME'] = $taskHomeDirectory
    $startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        $null = $process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The owned fixture tool exceeded its bounded execution time.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stdout | Set-Content -LiteralPath (Join-Path $logDirectory "$LogName.stdout.txt") -Encoding utf8
        $stderr | Set-Content -LiteralPath (Join-Path $logDirectory "$LogName.stderr.txt") -Encoding utf8
        if ($process.ExitCode -ne 0) {
            throw "Fixture tool failed ($LogName, exit $($process.ExitCode)); inspect its task-local logs."
        }
        return $stdout
    }
    finally { $process.Dispose() }
}
