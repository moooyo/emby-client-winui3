[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Inspect')][string] $Action = 'Start',
    [string] $RemoteWork,
    [ValidateRange(1024, 65535)][int] $LocalPort = 19096,
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (-not $ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $artifactRoot 'lumen-official-validation'
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (-not $ArtifactDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'An ignored artifact directory inside this repository is required.'
}
$receiptPath = Join-Path $ArtifactDirectory 'owned-backend.json'

function Write-PrivateJson([string] $Path, [object] $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Convert-ReceiptTimestampUtc([object] $Value) {
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { throw 'An unambiguous UTC receipt timestamp is required.' }
        return [DateTimeOffset] ($Value.ToUniversalTime())
    }
    if ($Value -isnot [string] -or $Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$') {
        throw 'An explicit UTC receipt timestamp is required.'
    }
    return [DateTimeOffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Read-OwnedTunnel([object] $Receipt) {
    if ($Receipt.Scope -ne 'OwnedOfficialEmbySyntheticAcceptance' -or $Receipt.LocalTunnelLoopbackOnly -ne $true) {
        throw 'An owned loopback tunnel receipt is required.'
    }
    $process = Get-Process -Id $Receipt.LocalTunnelPid -ErrorAction Stop
    $started = Convert-ReceiptTimestampUtc $Receipt.LocalTunnelStartedAtUtc
    if ($process.Path -ne $Receipt.LocalTunnelExecutable -or
        $process.StartTime.ToUniversalTime().Ticks -ne $started.UtcDateTime.Ticks -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $process.Path).Hash -ne $Receipt.LocalTunnelExecutableSha256) {
        throw 'The tunnel process identity does not match the receipt.'
    }
    return $process
}

if ($Action -in @('Stop', 'Inspect')) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $process = Read-OwnedTunnel $receipt
    if ($Action -eq 'Inspect') {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $receipt.LocalTunnelPort -ErrorAction Stop |
            Where-Object { $_.OwningProcess -eq $process.Id })
        if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1') {
            throw 'The owned tunnel listener does not match its loopback receipt.'
        }
        [ordered]@{ Outcome = 'OwnershipMatched'; TunnelPid = $process.Id; LoopbackOnly = $true;
            ProcessStartTicksMatched = $true; ExecutableHashMatched = $true; ReceiptPath = $receiptPath } | ConvertTo-Json -Compress
        return
    }
    Stop-Process -Id $process.Id
    $process.WaitForExit(10000) | Out-Null
    $receipt | Add-Member -NotePropertyName LocalTunnelStoppedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
    Write-PrivateJson $receiptPath $receipt
    [ordered]@{ Outcome = 'Stopped'; TunnelPid = $process.Id; ReceiptPath = $receiptPath } | ConvertTo-Json -Compress
    return
}

if ($RemoteWork -notmatch '^/tmp/lumen-official-[0-9a-f]{32}$') {
    throw 'The exact owned remote directory is required.'
}
if (Test-Path -LiteralPath $receiptPath) {
    throw 'This artifact directory already has an ownership receipt; reuse or stop its existing tunnel explicitly.'
}
if (Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue) {
    throw 'The requested loopback port is already in use.'
}
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$scp = (Get-Command scp.exe -ErrorAction Stop).Source
$ssh = (Get-Command ssh.exe -ErrorAction Stop).Source
& $scp -q "test-env:$RemoteWork/owned-backend.json" $receiptPath
if ($LASTEXITCODE -ne 0) { throw 'The remote ownership receipt could not be copied.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$expiry = Convert-ReceiptTimestampUtc $receipt.ExpiresUtc
$expectedImage = 'emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a'
if ($receipt.Scope -ne 'OwnedOfficialEmbySyntheticAcceptance' -or $receipt.OfficialServer -ne $true -or
    $receipt.SyntheticDataOnly -ne $true -or $receipt.WorkDirectory -ne $RemoteWork -or
    $receipt.OfficialImage -ne $expectedImage -or $receipt.ServerVersion -ne '4.9.5.0' -or
    $receipt.RemoteServerUrl -ne 'http://127.0.0.1:19096' -or $receipt.RemoteRelayLoopbackOnly -ne $true -or
    $receipt.InternalBridge -ne $true -or $receipt.DockerPublishedPorts -ne $false -or
    $receipt.ServerId -notmatch '^[0-9a-fA-F]{32}$' -or $expiry -le [DateTimeOffset]::UtcNow) {
    throw 'The safe official-server receipt did not match the expected isolated backend.'
}
foreach ($name in @('credentials.json', 'admin-credentials.json', 'catalog.json', 'summary.json')) {
    & $scp -q "test-env:$RemoteWork/$name" (Join-Path $ArtifactDirectory $name)
    if ($LASTEXITCODE -ne 0) { throw 'A private backend artifact could not be copied.' }
}
$forward = "127.0.0.1:${LocalPort}:127.0.0.1:19096"
$arguments = @('-N', '-o', 'BatchMode=yes', '-o', 'ExitOnForwardFailure=yes', '-o', 'ServerAliveInterval=30',
    '-o', 'ServerAliveCountMax=3', '-L', $forward, 'test-env')
$process = Start-Process -FilePath $ssh -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $ArtifactDirectory 'ssh-tunnel-output.log') `
    -RedirectStandardError (Join-Path $ArtifactDirectory 'ssh-tunnel-error.log')
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $listener = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw 'The owned SSH tunnel exited before binding its loopback port.' }
        $listener = Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -eq $process.Id }
        if ($listener) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $listener -or @($listener | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -ne 0) {
        throw 'The owned SSH tunnel did not establish exactly a loopback listener.'
    }
    $serverUrl = "http://127.0.0.1:$LocalPort"
    foreach ($pair in @{
        LocalTunnelPid = $process.Id
        LocalTunnelPort = $LocalPort
        LocalTunnelLoopbackOnly = $true
        LocalTunnelAddress = '127.0.0.1'
        LocalTunnelStartedAtUtc = $process.StartTime.ToUniversalTime().ToString('o')
        LocalTunnelExecutable = $ssh
        LocalTunnelExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ssh).Hash
        LocalTunnelForward = $forward
        ServerUrl = $serverUrl
    }.GetEnumerator()) {
        $receipt | Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force
    }
    Write-PrivateJson $receiptPath $receipt
    foreach ($name in @('credentials.json', 'admin-credentials.json')) {
        $path = Join-Path $ArtifactDirectory $name
        $credentials = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($credentials.ServerId -ne $receipt.ServerId -or $credentials.ExpectedServerVersion -ne $receipt.ServerVersion) {
            throw 'The private credentials belong to a different server identity.'
        }
        $credentials.ServerUrl = $serverUrl
        $credentials | Add-Member -NotePropertyName OwnershipReceiptPath -NotePropertyValue $receiptPath -Force
        Write-PrivateJson $path $credentials
    }
    [ordered]@{ Outcome = 'Ready'; ServerUrl = $serverUrl; TunnelPid = $process.Id; ReceiptPath = $receiptPath;
        CredentialsPath = (Join-Path $ArtifactDirectory 'credentials.json') } | ConvertTo-Json -Compress
}
catch {
    $process.Refresh()
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    throw
}
