#Requires -Version 7.0

function Invoke-Docker {
    param([string[]] $DockerArguments)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dockerCommand.Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($dockerEndpoint) {
        $startInfo.ArgumentList.Add('--host')
        $startInfo.ArgumentList.Add($dockerEndpoint)
    }
    foreach ($argument in $DockerArguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult().Trim()
        $errorOutput = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "Docker failed: $errorOutput $output"
        }
        if ($errorOutput) {
            Write-Warning $errorOutput
        }
        return $output
    }
    finally {
        $process.Dispose()
    }
}

function Open-ValidationLock {
    param([string] $ValidationRoot)

    New-Item -ItemType Directory -Path $ValidationRoot -Force | Out-Null
    $lockPath = Join-Path $ValidationRoot 'server-validation.lock'
    try {
        # Start and Stop hold this same file exclusively for their entire operation.
        return [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw 'Another server validation operation holds the workspace lock. Wait for it to finish before retrying.'
    }
}
