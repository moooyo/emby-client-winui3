param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$ProcessId,
    [ValidateRange(10, 300)][int]$DurationSeconds = 180,
    [ValidateRange(1, 15)][int]$IntervalSeconds = 5,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$fixtureStatsUri = 'http://127.0.0.1:18962/_fixture/stats'
$process = Get-Process -Id $ProcessId
if ($process.ProcessName -ne 'EmbyClient.App') {
    throw 'Select the actual EmbyClient.App process; this sampler does not inspect unrelated processes.'
}
$initial = Invoke-RestMethod -Uri $fixtureStatsUri -TimeoutSec 5
if (-not $initial.Synthetic -or $initial.LargeLibraryItems -le 0) {
    throw 'Port 18962 must contain the synthetic large-library fixture.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts/large-library-acceptance' }
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sampleId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputPath = Join-Path $outputRoot ('memory-{0}-{1}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $sampleId)
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$samples = 0

while ($clock.Elapsed.TotalSeconds -le $DurationSeconds) {
    $process.Refresh()
    if ($process.HasExited) { break }
    $stats = Invoke-RestMethod -Uri $fixtureStatsUri -TimeoutSec 5
    $lastPage = $stats.Queries | Where-Object { $_.Operation -eq 'Items' -and $_.ParentId -eq 'large-movies' -and -not $_.HasSearch } | Select-Object -Last 1
    [pscustomobject]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ElapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
        ProcessId = $process.Id
        PrivateMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 2)
        PeakWorkingSetMiB = [Math]::Round($process.PeakWorkingSet64 / 1MB, 2)
        HandleCount = $process.HandleCount
        ThreadCount = $process.Threads.Count
        LargeLibraryItems = $stats.LargeLibraryItems
        QueryCount = $stats.QueryCount
        LastLargePageStart = if ($lastPage) { $lastPage.StartIndex } else { $null }
        LastLargePageReturned = if ($lastPage) { $lastPage.ReturnedItems } else { $null }
        ImageRequests = $stats.ImageRequests
        ActiveImages = $stats.ActiveImages
        PeakActiveImages = $stats.PeakActiveImages
        CompletedImages = $stats.CompletedImages
        CanceledImages = $stats.CanceledImages
        ImageBytesServed = $stats.ImageBytesServed
        InjectedPlaybackInfoFailures = $stats.InjectedPlaybackInfoFailures
    } | Export-Csv -LiteralPath $outputPath -NoTypeInformation -Append
    $samples++
    $remainingMilliseconds = [int](($DurationSeconds - $clock.Elapsed.TotalSeconds) * 1000)
    if ($remainingMilliseconds -le 0) { break }
    Start-Sleep -Milliseconds ([Math]::Min($IntervalSeconds * 1000, $remainingMilliseconds))
}

Write-Output ('Collected {0} process/fixture samples: {1}' -f $samples, $outputPath)
Write-Output 'This script does not operate the UI, count XAML objects, force garbage collection, or inspect titles/credentials.'
