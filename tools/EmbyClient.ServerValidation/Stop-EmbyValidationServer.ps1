#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validationRoot = Join-Path $repositoryRoot 'artifacts/emby-validation'
$statePath = Join-Path $validationRoot 'container-state.json'
$ownerLabel = 'org.embyclient.validation-root'

$dockerEndpoint = $null
$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
. (Join-Path $PSScriptRoot 'Docker-ValidationHelpers.ps1')
if (-not $dockerCommand) {
    throw 'Docker is unavailable. No containers or files were changed.'
}
$operationLock = Open-ValidationLock -ValidationRoot $validationRoot
try {
    if (-not (Test-Path -LiteralPath $statePath)) {
        throw 'No owned validation container state exists. No containers were changed.'
    }

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.ValidationRoot -ne $validationRoot -or $state.ContainerId -notmatch '^[0-9a-f]{64}$') {
        throw 'The saved state does not identify a validation container owned by this workspace.'
    }
    $contextName = Invoke-Docker -DockerArguments @('context', 'show')
    $context = (Invoke-Docker -DockerArguments @('context', 'inspect', $contextName) | ConvertFrom-Json)[0]
    $daemonHost = $context.Endpoints.docker.Host
    if ($env:DOCKER_HOST -and -not $env:DOCKER_CONTEXT) {
        $daemonHost = $env:DOCKER_HOST
    }
    if ($contextName -ne $state.DockerContext -or $daemonHost -ne $state.DockerEndpoint) {
        throw 'The Docker context or endpoint changed. Restore the recorded context before stopping the container.'
    }
    $dockerEndpoint = $daemonHost
    $container = (Invoke-Docker -DockerArguments @('inspect', $state.ContainerId) | ConvertFrom-Json)[0]
    if ($container.Config.Labels.$ownerLabel -ne $validationRoot -or
        $container.Name -ne "/$($state.ContainerName)") {
        throw 'The container ownership label or name does not match. No container was changed.'
    }

    if ($container.State.Running) {
        $null = Invoke-Docker -DockerArguments @('stop', '--time', '15', $state.ContainerId)
    }
    $null = Invoke-Docker -DockerArguments @('rm', $state.ContainerId)

    # Only the exact state file is removed. Configuration, media, and logs remain for review.
    Remove-Item -LiteralPath $statePath
    Write-Output 'The owned validation container was stopped and removed. Run artifacts and the downloaded image were preserved.'
}
finally {
    $operationLock.Dispose()
}
