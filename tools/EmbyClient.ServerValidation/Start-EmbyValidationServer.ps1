#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validationRoot = Join-Path $repositoryRoot 'artifacts/emby-validation'
$statePath = Join-Path $validationRoot 'container-state.json'
$fixtureRoot = Join-Path $repositoryRoot 'tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds'
$ownerLabel = 'org.embyclient.validation-root'
$image = 'emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a'

$dockerEndpoint = $null
$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
. (Join-Path $PSScriptRoot 'Docker-ValidationHelpers.ps1')
if (-not $dockerCommand) {
    throw 'Docker is unavailable. No server was started. Do not fall back to the Windows portable server without verified loopback binding.'
}
$operationLock = Open-ValidationLock -ValidationRoot $validationRoot
try {
    if (Test-Path -LiteralPath $statePath) {
        throw 'A validation container state already exists. Inspect it and use Stop-EmbyValidationServer.ps1 before starting another instance.'
    }

    # Refuse remote daemon endpoints because the published port must belong to this machine.
    $contextName = Invoke-Docker -DockerArguments @('context', 'show')
    $context = (Invoke-Docker -DockerArguments @('context', 'inspect', $contextName) | ConvertFrom-Json)[0]
    $daemonHost = $context.Endpoints.docker.Host
    if ($env:DOCKER_HOST -and -not $env:DOCKER_CONTEXT) {
        $daemonHost = $env:DOCKER_HOST
    }
    if ($daemonHost -notmatch '^npipe:/{2,}\.(/|\\)pipe(/|\\)') {
        throw "Only a local Windows named-pipe Docker daemon is accepted; configured endpoint: $daemonHost"
    }
    $dockerEndpoint = $daemonHost
    $engine = Invoke-Docker -DockerArguments @('version', '--format', '{{json .Server}}') | ConvertFrom-Json
    if ($engine.Os -ne 'linux') {
        throw 'The official Emby image requires an existing Linux-container Docker engine.'
    }
    if ([version]($engine.Version -replace '-.*$', '') -lt [version]'28.0.0') {
        throw 'Docker Engine 28.0.0 or later is required for localhost port-publishing isolation.'
    }
    if (Get-NetTCPConnection -State Listen -LocalPort 19096 -ErrorAction SilentlyContinue) {
        throw 'TCP port 19096 is already in use. No existing listener will be stopped.'
    }

    $fixture = Get-Content -LiteralPath (Join-Path $fixtureRoot 'fixture-h264-aac.json') -Raw | ConvertFrom-Json
    $fixturePath = Join-Path $fixtureRoot 'fixture-h264-aac.mp4'
    if ($fixture.Synthetic -ne $true -or (Get-Item -LiteralPath $fixturePath).Length -ne $fixture.FileLength) {
        throw 'Generate the sixty-second synthetic fixture and its matching metadata before starting this server.'
    }

    $runId = [Guid]::NewGuid().ToString('N')
    $runRoot = Join-Path $validationRoot "runs/$runId"
    $configRoot = Join-Path $runRoot 'config'
    $mediaRoot = Join-Path $runRoot 'media'
    New-Item -ItemType Directory -Path (Join-Path $configRoot 'config'), $mediaRoot -Force | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination (Join-Path $mediaRoot 'Fixture (2026).mp4')

    # Docker supplies the binding boundary. These settings also disable discovery-related
    # port mapping, remote access, and updates before the initial setup wizard runs.
    @'
<?xml version="1.0" encoding="utf-8"?>
<ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <IsStartupWizardCompleted>false</IsStartupWizardCompleted>
  <EnableAutoUpdate>false</EnableAutoUpdate>
  <EnableAutomaticRestart>false</EnableAutomaticRestart>
  <EnableUPnP>false</EnableUPnP>
  <EnableRemoteAccess>false</EnableRemoteAccess>
  <HttpServerPortNumber>8096</HttpServerPortNumber>
  <PublicPort>8096</PublicPort>
</ServerConfiguration>
'@ | Set-Content -LiteralPath (Join-Path $configRoot 'config/system.xml') -Encoding utf8

    $null = Invoke-Docker -DockerArguments @('pull', $image)
    $containerName = "emby-client-validation-$runId"
    $containerId = Invoke-Docker -DockerArguments @(
        'create', '--name', $containerName,
        '--label', "$ownerLabel=$validationRoot",
        '--restart', 'no', '--network', 'bridge',
        '--publish', '127.0.0.1:19096:8096/tcp',
        '--mount', "type=bind,source=$configRoot,target=/config",
        '--mount', "type=bind,source=$mediaRoot,target=/mnt/validation-media,readonly",
        $image
    )
    if ($containerId -notmatch '^[0-9a-f]{64}$') {
        throw "Docker did not return a valid container ID. Inspect the owned container named $containerName before retrying."
    }

    $state = [ordered]@{
        ContainerId = $containerId
        ContainerName = $containerName
        Image = $image
        ServerVersion = '4.9.5.0'
        ValidationRoot = $validationRoot
        RunRoot = $runRoot
        Url = 'http://127.0.0.1:19096'
        CreatedUtc = [DateTime]::UtcNow.ToString('O')
        DockerContext = $contextName
        DockerEndpoint = $daemonHost
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8

    # Inspect the container before any process can start. Do not trust the CLI request alone.
    $container = (Invoke-Docker -DockerArguments @('inspect', $containerId) | ConvertFrom-Json)[0]
    $bindings = @($container.HostConfig.PortBindings.PSObject.Properties)
    if ($bindings.Count -ne 1 -or $bindings[0].Name -ne '8096/tcp' -or
        @($bindings[0].Value).Count -ne 1 -or
        $bindings[0].Value[0].HostIp -ne '127.0.0.1' -or
        $bindings[0].Value[0].HostPort -ne '19096' -or
        $container.HostConfig.NetworkMode -ne 'bridge' -or
        $container.HostConfig.Privileged -or
        $container.Config.Labels.$ownerLabel -ne $validationRoot) {
        throw 'Container isolation inspection failed. The container was not started; use the stop script to remove this owned container.'
    }

    $null = Invoke-Docker -DockerArguments @('start', $containerId)
    Write-Output 'The isolated official Emby Server container has been started at http://127.0.0.1:19096.'
    Write-Output 'Complete the first-run wizard with a dedicated temporary account and the /mnt/validation-media movie library.'
    Write-Output 'Keep remote access and automatic port mapping disabled. Do not enter an Emby Connect account or a Premiere key.'
    Write-Output "State: $statePath"
}
finally {
    $operationLock.Dispose()
}
