Set-StrictMode -Version Latest

function Get-LumenAbsolutePath {
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Acceptance paths must be absolute.' }
    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
}

function Test-LumenChildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent)) + [IO.Path]::DirectorySeparatorChar
    [IO.Path]::GetFullPath($Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LumenRunDirectory {
    param([Parameter(Mandatory)][string]$RunDirectory, [Parameter(Mandatory)][string]$WorkspaceRoot)
    $artifactRoot = Join-Path $WorkspaceRoot 'artifacts/lumen-acceptance'
    if (-not (Test-LumenChildPath -Path $RunDirectory -Parent $artifactRoot)) {
        throw 'The run directory must stay inside this workspace acceptance artifacts.'
    }
    $ancestor = [IO.DirectoryInfo]::new($RunDirectory)
    while ($null -ne $ancestor -and (Test-LumenChildPath -Path $ancestor.FullName -Parent $WorkspaceRoot)) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Acceptance output cannot traverse a reparse point.'
        }
        $ancestor = $ancestor.Parent
    }
}

function Assert-LumenExecutable {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$WorkspaceRoot,
          [Parameter(Mandatory)][string]$ExpectedName)
    if (-not (Test-LumenChildPath -Path $Executable -Parent $WorkspaceRoot) -or
        -not (Test-Path -LiteralPath $Executable -PathType Leaf) -or
        [IO.Path]::GetFileName($Executable) -ne $ExpectedName) {
        throw 'The selected executable is not the expected tool built in this workspace.'
    }
}

function Get-LumenOwnedProcess {
    param([Parameter(Mandatory)]$Ownership)
    $process = Get-Process -Id ([int]$Ownership.FixtureProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $expectedTime = if ($Ownership.FixtureStartedAtUtc -is [DateTime]) {
        $Ownership.FixtureStartedAtUtc.ToUniversalTime()
    }
    elseif ($Ownership.FixtureStartedAtUtc -is [DateTimeOffset]) {
        $Ownership.FixtureStartedAtUtc.UtcDateTime
    }
    else { [DateTimeOffset]::Parse([string]$Ownership.FixtureStartedAtUtc).UtcDateTime }
    if (-not [string]::Equals($process.Path, [string]$Ownership.FixtureExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $process.StartTime.ToUniversalTime() -ne $expectedTime -or
        (Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash -ne $Ownership.FixtureExecutableSha256) {
        throw 'The recorded PID no longer identifies this owned fixture. No process was stopped.'
    }
    $process
}
