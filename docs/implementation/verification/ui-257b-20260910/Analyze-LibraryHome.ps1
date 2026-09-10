param([string]$OutputFileName = 'library-home-analysis.json')

# Read immutable local evidence only. No application, live log, network, or UI access is used.
if ([IO.Path]::GetFileName($OutputFileName) -cne $OutputFileName) { throw 'Select an output filename in this evidence directory.' }
$evidenceDirectory = $PSScriptRoot
$outputPath = Join-Path $evidenceDirectory $OutputFileName
if (Test-Path -LiteralPath $outputPath) { throw 'Analysis outputs are never overwritten. Select a new output filename.' }
$sourcePath = Join-Path $evidenceDirectory 'library-home-snapshot.jsonl'
$sourceLines = @(Get-Content -LiteralPath $sourcePath)
$sourceRows = @($sourceLines | ForEach-Object { $_ | ConvertFrom-Json })
$metadata = Get-Content -LiteralPath (Join-Path $evidenceDirectory 'library-home-snapshot.json') -Raw | ConvertFrom-Json
$initialMetadata = Get-Content -LiteralPath (Join-Path $evidenceDirectory 'home-before-desktop.json') -Raw | ConvertFrom-Json
$initialLines = @(Get-Content -LiteralPath (Join-Path $evidenceDirectory 'home-before-desktop.jsonl'))
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
if ($sourceHash -cne $metadata.Sha256 -or $sourceRows.Count -ne $metadata.Rows -or (Get-Item -LiteralPath $sourcePath).Length -ne $metadata.Bytes) {
    throw 'The frozen snapshot does not match its metadata.'
}
$prefixMatches = $sourceLines.Count -ge $initialLines.Count
for ($index = 0; $prefixMatches -and $index -lt $initialLines.Count; $index++) {
    if ($sourceLines[$index] -cne $initialLines[$index]) { $prefixMatches = $false }
}
$libraryIndices = @(0..($sourceRows.Count - 1) | Where-Object { $sourceRows[$_].LibraryIsHome -eq 0 -and $sourceRows[$_].ItemsCount -gt 2 })
$libraryRows = @($sourceRows[$libraryIndices[0]..$libraryIndices[-1]])
$returnedHomeRows = @($sourceRows | Where-Object { $_.ElapsedMilliseconds -gt $libraryRows[-1].ElapsedMilliseconds -and $_.LibraryIsHome -eq 1 })
$homeExclusionThreshold = $returnedHomeRows[0].ElapsedMilliseconds + 30000
$settledHomeRows = @($returnedHomeRows | Where-Object ElapsedMilliseconds -ge $homeExclusionThreshold)
$byteNames = @('ProcessPrivateBytes','ManagedBytes','ManagedAllocatedBytesApproximate','ProcessWorkingSetBytes','ObserverCompletedSampleAllocatedBytes')
$stateNames = @('ItemsCount','LibraryIsHome','LibraryHasDetails','LibraryIsBusy','LibraryVisibility','PosterSubscriptionsCount','PosterRequestsCount','BoundPosterSourcesCount','LoadedPosterControlsCount','AttachedPosterControlsCount','RealizedPosterControlsCount','DetailPosterBound','WeakImageTrackedCount','WeakImageAliveCount','WeakImageEvictions','WeakBitmapTrackedCount','WeakBitmapAliveCount','WeakBitmapEvictions','DecodeStartedTotal','DecodeCompletedTotal','DecodeCanceledTotal','DecodeFailedTotal','DecodeActive','DecodePeak','AssignedTotal','ClearedTotal','PosterLoadRequestedTotal','PosterLoadRejectedTotal','PosterLoadDeduplicatedTotal','LoadedTotal','UnloadedTotal','TagChangedTotal','PresentationClockEnabled','PresentationClockTicks','PositionUpdateCalls','Gen0Collections','Gen1Collections','Gen2Collections','LastGcIndex','LastGcHeapSizeBytes','LastGcFragmentedBytes','LastGcCommittedBytes')

function Get-PhaseAnalysis([string]$Name, [object[]]$Rows) {
    $firstSample = $Rows[0]
    $lastSample = $Rows[-1]
    return [ordered]@{
        Name = $Name
        Rows = $Rows.Count
        FirstElapsedMilliseconds = $firstSample.ElapsedMilliseconds
        LastElapsedMilliseconds = $lastSample.ElapsedMilliseconds
        SampledSpanSeconds = ($lastSample.ElapsedMilliseconds - $firstSample.ElapsedMilliseconds) / 1000.0
        FirstUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($firstSample.UtcUnixTimeMilliseconds).ToString('O')
        LastUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($lastSample.UtcUnixTimeMilliseconds).ToString('O')
        UtcSpanSeconds = ($lastSample.UtcUnixTimeMilliseconds - $firstSample.UtcUnixTimeMilliseconds) / 1000.0
        ByteMetrics = @($byteNames | ForEach-Object {
            $field = $_
            [ordered]@{ Field = $field; FirstBytes = $firstSample.$field; LastBytes = $lastSample.$field; FirstMiB = $firstSample.$field / 1MB; LastMiB = $lastSample.$field / 1MB; DeltaBytes = $lastSample.$field - $firstSample.$field; DeltaMiB = ($lastSample.$field - $firstSample.$field) / 1MB; SampledMinimumBytes = ($Rows.$field | Measure-Object -Minimum).Minimum; SampledMaximumBytes = ($Rows.$field | Measure-Object -Maximum).Maximum }
        })
        HandleCountFirst = $firstSample.ProcessHandleCount
        HandleCountLast = $lastSample.ProcessHandleCount
        HandleCountDelta = $lastSample.ProcessHandleCount - $firstSample.ProcessHandleCount
        HandleCountMinimum = ($Rows.ProcessHandleCount | Measure-Object -Minimum).Minimum
        HandleCountMaximum = ($Rows.ProcessHandleCount | Measure-Object -Maximum).Maximum
        CpuSecondsDelta = ($lastSample.ProcessCpuMilliseconds - $firstSample.ProcessCpuMilliseconds) / 1000.0
        AverageCpuPercentOfOneLogicalCore = ($lastSample.ProcessCpuMilliseconds - $firstSample.ProcessCpuMilliseconds) / ($lastSample.ElapsedMilliseconds - $firstSample.ElapsedMilliseconds) * 100.0
        ObserverCompletedSamplesDelta = $lastSample.ObserverCompletedSamples - $firstSample.ObserverCompletedSamples
        StateAndCounterMetrics = @($stateNames | ForEach-Object {
            $field = $_
            [ordered]@{ Field = $field; FirstValue = $firstSample.$field; LastValue = $lastSample.$field; Delta = $lastSample.$field - $firstSample.$field; SampledMinimum = ($Rows.$field | Measure-Object -Minimum).Minimum; SampledMaximum = ($Rows.$field | Measure-Object -Maximum).Maximum; DistinctValues = @($Rows.$field | Sort-Object -Unique) }
        })
    }
}

$gcSampleNames = @('ElapsedMilliseconds','UtcUnixTimeMilliseconds','ItemsCount','LibraryIsHome','Gen0Collections','Gen1Collections','Gen2Collections','LastGcIndex','LastGcHeapSizeBytes','LastGcCommittedBytes','LastGcFragmentedBytes','ManagedBytes','ManagedAllocatedBytesApproximate','ProcessPrivateBytes','ProcessWorkingSetBytes','WeakBitmapTrackedCount','WeakBitmapAliveCount','DecodeStartedTotal','DecodeCompletedTotal','DecodeCanceledTotal','DecodeFailedTotal','DecodeActive')
$gcTransitions = @()
for ($index = 1; $index -lt $sourceRows.Count; $index++) {
    $beforeSample = $sourceRows[$index - 1]
    $afterSample = $sourceRows[$index]
    if ($afterSample.LastGcIndex -ne $beforeSample.LastGcIndex -or $afterSample.Gen0Collections -ne $beforeSample.Gen0Collections -or $afterSample.Gen1Collections -ne $beforeSample.Gen1Collections -or $afterSample.Gen2Collections -ne $beforeSample.Gen2Collections) {
        $gcTransitions += [ordered]@{
            BeforeRowOneBased = $index
            AfterRowOneBased = $index + 1
            SampledBoundarySpanSeconds = ($afterSample.ElapsedMilliseconds - $beforeSample.ElapsedMilliseconds) / 1000.0
            BeforeUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($beforeSample.UtcUnixTimeMilliseconds).ToString('O')
            AfterUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($afterSample.UtcUnixTimeMilliseconds).ToString('O')
            Before = $beforeSample | Select-Object $gcSampleNames
            After = $afterSample | Select-Object $gcSampleNames
            GenerationCountDeltas = [ordered]@{ Gen0 = $afterSample.Gen0Collections - $beforeSample.Gen0Collections; Gen1 = $afterSample.Gen1Collections - $beforeSample.Gen1Collections; Gen2 = $afterSample.Gen2Collections - $beforeSample.Gen2Collections; LastGcIndex = $afterSample.LastGcIndex - $beforeSample.LastGcIndex }
            ByteMetricDeltas = @($byteNames | ForEach-Object { $field = $_; [ordered]@{ Field = $field; DeltaBytes = $afterSample.$field - $beforeSample.$field; DeltaMiB = ($afterSample.$field - $beforeSample.$field) / 1MB } })
            Interpretation = 'These are adjacent sequentially sampled counter states, not the timestamp or isolated before/after state of an exact single GC event. Allocations, decoder callbacks, and native work can also occur between the rows.'
        }
    }
}

$fixtureNames = @('fixture-before.json','fixture-after-six-pages.json','fixture-home-return.json','fixture-after-passive-home.json')
$fixtureFields = @('LoginCount','PlaybackInfoCount','StartCount','ProgressCount','StopCount','MediaRequests','RangeRequests','PartialResponses','EncodingCleanups','AuthenticationFailures','QueryCount','ImageRequests','ActiveImages','PeakActiveImages','CompletedImages','CanceledImages','ImageBytesServed')
$fixtureValues = @{}
$fixtureSummaries = @($fixtureNames | ForEach-Object {
    $fileName = $_
    $fixturePath = Join-Path $evidenceDirectory $fileName
    $fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
    $fixtureValues[$fileName] = $fixture
    [ordered]@{ FileName = $fileName; Sha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash; Counters = $fixture | Select-Object $fixtureFields; ImageAccountingResidual = $fixture.ImageRequests - $fixture.CompletedImages - $fixture.CanceledImages - $fixture.ActiveImages }
})
$fixtureBefore = $fixtureValues['fixture-before.json']
$fixtureSixPages = $fixtureValues['fixture-after-six-pages.json']
$fixtureHomeReturn = $fixtureValues['fixture-home-return.json']
$fixtureHomeEnd = $fixtureValues['fixture-after-passive-home.json']
$newQueries = @($fixtureHomeEnd.Queries | Where-Object Sequence -gt $fixtureBefore.QueryCount)
$largePages = @($newQueries | Where-Object { $_.Operation -eq 'Items' -and $_.ParentId -eq 'large-movies' })
$sixPageQueries = @($fixtureSixPages.Queries | Where-Object { $_.Sequence -gt $fixtureBefore.QueryCount -and $_.Operation -eq 'Items' -and $_.ParentId -eq 'large-movies' })
$fixtureHomeDifferences = @($fixtureFields | ForEach-Object { $field = $_; [ordered]@{ Field = $field; ReturnValue = $fixtureHomeReturn.$field; LaterValue = $fixtureHomeEnd.$field; Delta = $fixtureHomeEnd.$field - $fixtureHomeReturn.$field } })
$observationsPath = Join-Path $evidenceDirectory 'observations.json'
$observations = @(Get-Content -LiteralPath $observationsPath -Raw | ConvertFrom-Json)
$decodeUnbalancedRows = @($sourceRows | Where-Object { $_.DecodeStartedTotal -ne $_.DecodeCompletedTotal + $_.DecodeCanceledTotal + $_.DecodeFailedTotal + $_.DecodeActive })
$result = [ordered]@{
    SchemaVersion = 1
    EvidenceKind = 'FrozenLibraryAndReturnedHomeAttributionAnalysis'
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    SourceFileName = [IO.Path]::GetFileName($sourcePath)
    SourceSha256 = $sourceHash
    SourceRows = $sourceRows.Count
    SourceBytes = (Get-Item -LiteralPath $sourcePath).Length
    SourceMatchesMetadata = $true
    Identity = [ordered]@{ ProcessIds = @($sourceRows.ProcessId | Sort-Object -Unique); ExecutableSha256 = $initialMetadata.ExeSha256; ObserverSchemaValues = @($sourceRows.SchemaVersion | Sort-Object -Unique); StartTriggerValues = @($sourceRows.StartTrigger | Sort-Object -Unique) }
    Reproduction = [ordered]@{ ScriptFileName = [IO.Path]::GetFileName($PSCommandPath); ScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; Command = './Analyze-LibraryHome.ps1 -OutputFileName library-home-analysis-reproduced.json'; Note = 'This script reads only named frozen evidence files and creates a new analysis. Generation time and the selected output filename may differ; product, runtime, and live logs are not accessed.' }
    InitialHomePrefix = [ordered]@{ FileName = 'home-before-desktop.jsonl'; Rows = $initialLines.Count; Sha256 = (Get-FileHash -LiteralPath (Join-Path $evidenceDirectory 'home-before-desktop.jsonl') -Algorithm SHA256).Hash; ExactLineContentPrefixMatches = $prefixMatches; ExistingAnalysisFileName = 'home-before-desktop-analysis.json'; Interpretation = 'The first 61 lines are compared in order without treating newline encoding as content. The earlier initial-Home report is retained separately and was not rewritten or extended into a different baseline.' }
    Segmentation = [ordered]@{
        LibraryRule = 'First non-Home row with ItemsCount greater than two through the last row matching that condition.'
        LibraryFirstRowOneBased = $libraryIndices[0] + 1
        LibraryLastRowOneBased = $libraryIndices[-1] + 1
        AllLibraryRowsMatchRule = @($libraryRows | Where-Object { $_.LibraryIsHome -ne 0 -or $_.ItemsCount -le 2 }).Count -eq 0
        ReturnedHomeRule = 'Rows after the last library row with LibraryIsHome equal to one.'
        ReturnedHomeFirstElapsedMilliseconds = $returnedHomeRows[0].ElapsedMilliseconds
        ReturnedHomeExclusionThresholdMilliseconds = $homeExclusionThreshold
        ActualFirstPostExclusionElapsedMilliseconds = $settledHomeRows[0].ElapsedMilliseconds
        Scope = 'Boundaries are observed sample states, not exact navigation timestamps. The first library row already contains 48 items and ongoing poster requests; it is not a cold app or pre-request baseline. Overlapping returned-Home windows must not have their durations or deltas added.'
    }
    Phases = @((Get-PhaseAnalysis 'Library' $libraryRows), (Get-PhaseAnalysis 'ReturnedHome' $returnedHomeRows), (Get-PhaseAnalysis 'ReturnedHomeAfterThirtySeconds' $settledHomeRows))
    GcObservations = [ordered]@{
        SampledCounterTransitions = $gcTransitions
        FinalCounts = $sourceRows[-1] | Select-Object Gen0Collections,Gen1Collections,Gen2Collections,LastGcIndex
        Interpretation = 'Four sampled LastGcIndex advances are present: a Gen 2-inclusive collection before library sampling, then Gen 2-inclusive, Gen 0-only, and Gen 2-inclusive changes. Inclusive generation counts must not be summed as ten separate collections. No additional GC is recorded after returning Home.'
        LastGcWarning = 'LastGcHeapSizeBytes, LastGcFragmentedBytes, and LastGcCommittedBytes describe the most recently reported GC. They are not current live heap, current native memory, or GPU memory. Zero values before index one mean no last-GC data, not an empty heap.'
    }
    DecoderAccounting = [ordered]@{ RowsChecked = $sourceRows.Count; UnbalancedRows = $decodeUnbalancedRows.Count; Final = $sourceRows[-1] | Select-Object DecodeStartedTotal,DecodeCompletedTotal,DecodeCanceledTotal,DecodeFailedTotal,DecodeActive,DecodePeak; Interpretation = 'Started equals completed plus canceled plus failed plus active in every row. Active is a sampled managed wrapper-call count; cancellation is managed cancellation handling, not confirmation that native resources were released. Peak 40 is decode-wrapper concurrency, not HTTP permit count.' }
    NoVideoObservations = [ordered]@{ PresentationClockEnabledValues = @($sourceRows.PresentationClockEnabled | Sort-Object -Unique); PresentationClockTicksValues = @($sourceRows.PresentationClockTicks | Sort-Object -Unique); PositionUpdateCallsValues = @($sourceRows.PositionUpdateCalls | Sort-Object -Unique); Interpretation = 'All presentation-clock and position-update fields remain zero. The fixture playback/media totals are historical and unchanged in the supplied snapshots; there is no new playback activity in these recorded paths.' }
    FixtureEvidence = [ordered]@{
        Snapshots = $fixtureSummaries
        NewQueriesAfterBeforeSnapshot = $newQueries
        SixPageOffsets = @($sixPageQueries.StartIndex)
        SixPageReturnedRecords = ($sixPageQueries.ReturnedItems | Measure-Object -Sum).Sum
        ActualLargeLibraryOffsets = @($largePages.StartIndex)
        ActualPageCount = $largePages.Count
        ActualReturnedRecords = ($largePages.ReturnedItems | Measure-Object -Sum).Sum
        TotalServerLibraryRecords = @($largePages.TotalRecordCount | Sort-Object -Unique)
        HomeSnapshotCounterComparisons = $fixtureHomeDifferences
        BeforeToFinalCounterDeltas = @($fixtureFields | ForEach-Object { $field = $_; [ordered]@{ Field = $field; Delta = $fixtureHomeEnd.$field - $fixtureBefore.$field } })
        Interpretation = 'The six-page snapshot records offsets 0 through 240 and 288 returned records. The later query at offset 288 adds a seventh page, for 336 records. Operator observation attributes it to automatic incremental loading at the prior list end. Before-to-final query delta nine includes the initial Home Resume query, seven library pages, and the returned-Home Resume query. Fixture snapshot baselines are not the exact process-sample boundaries.'
        HomeScope = 'Both Home fixture snapshots record QueryCount 28, ImageRequests 666, CompletedImages 435, CanceledImages 231, ActiveImages zero, and historical PeakActiveImages five. The unchanged cumulative image/query counters bound new activity in those routes between the snapshots; they do not describe every HTTP route or native allocation. The earlier six-page snapshot has four active images and is not a settled HTTP baseline.'
    }
    ScreenshotMetadata = [ordered]@{ FileName = 'observations.json'; Sha256 = (Get-FileHash -LiteralPath $observationsPath -Algorithm SHA256).Hash; Entries = $observations.Count; RequestedTextValues = @($observations.TextRequested | Sort-Object -Unique); Observations = $observations; Interpretation = 'ArchivedAtUtc is evidence save time, not screenshot capture time or exact action time. Operator descriptions provide separate visual context; this numeric analysis did not inspect or recapture the JPG pixels and does not align screenshots to individual process samples.' }
    InterpretationAndLimits = @(
        'The library phase spans 33 actual rows over 160.391 seconds and grows from 48 to 336 loaded records. Its private-memory delta is 116,232,192 bytes, while cumulative managed allocation grows by 29,576,656 bytes. These different scopes do not identify a managed/native allocation split or a leak root cause.',
        'After the returned-Home thirty-second exclusion, 43 rows span 210.273 seconds. Private bytes decrease by 1,314,816 and working set by 1,269,760, while ManagedBytes increases by 722,256 and approximate cumulative managed allocation by 672,136. This short decline is an observation, not a long-term plateau or a completed memory acceptance gate.',
        'All returned-Home poster callback/decode totals remain unchanged. There are 440 started decoder-helper calls, 376 completions, 64 cancellations, zero failures, and zero sampled active calls. The lack of new callback work is distinct from the lifetime of objects previously created.',
        'The final library sample has 40 bound Sources; every returned-Home row has two. Forty-six images remain subscribed and loaded, so 44 subscribed images have null Source in those Home rows. This supports the scoped old-source cleanup path; it does not prove native texture destruction or a full object census.',
        'AttachedPosterControlsCount only means a GridViewItem ancestor was found. RealizedPosterControlsCount only means that ancestor remains in the application realization table. The Home values 46 attached and two tracked realizations do not establish viewport visibility, attachment to the current MediaGrid/XamlRoot, or an independent native container count.',
        'Forty-seven weakly tracked Image wrappers persist for 336 loaded records, compatible with container reuse rather than one control per record. The weak BitmapImage cohort has 376 tracked entries and 126 resolving targets, with no evictions. The 250 non-resolving targets do not identify native deallocations, and the remaining 126 cannot be classified as leaks or as survivors of the latest Gen 2 collection: new decodes occurred after that collection.',
        'The last reported GC at index four has heap size 5,553,912 bytes, fragmented bytes 9,216, and committed bytes 24,821,760. These remain unchanged during Home because there is no newer GC. The later ManagedBytes values include allocations since that GC and potentially unreachable objects awaiting collection.',
        'During some GC-adjacent sample intervals, new bitmaps are decoded while weak target counts fall. Adjacent sample deltas therefore combine collection, new allocations, asynchronous callbacks, and other process work; they are not isolated per-GC release measurements.',
        'The post-exclusion Home observer allocation estimate rises by 548,880 bytes. It counts prior completed synchronous sampling on the current thread and omits other observer callbacks, asynchronous tasks, native work, and other threads. It must not be subtracted as complete observation overhead.',
        'CPU is whole-process cumulative CPU, reported relative to one logical core. The library phase includes navigation, scrolling, image work, and the observer; it is not a decoder benchmark. Process memory and GC fields are read sequentially and are not atomic snapshots.',
        'The finite source ends at elapsed 766.276 seconds. Later live records were not read. Initial Home, library, and returned Home differ in cached content and prior activity; no strict performance A/B against earlier executables is established.',
        'Only seven pages and the recorded revisit sequence are covered. No claim is made that all 5,000 records were rendered, that five complete revisit cycles were performed, or that playback was exercised. The visual metadata records three top revisits and two middle revisits.',
        'No app, build, test suite, desktop control, network request, or forced GC was run by this frozen-file analysis, and no product or previous evidence file was changed.'
    )
    SupportedConclusion = 'The observed Home return stops new poster/decode work and reduces bound Sources to the two Home posters. Its short process-memory tail falls while managed allocation continues without a new GC. This strengthens scoped cleanup evidence but leaves long-term memory stability and native allocation ownership unproven.'
}
$json = ($result | ConvertTo-Json -Depth 16) -replace "`r`n", "`n"
$outputBytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
$outputStream = [IO.FileStream]::new($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $outputStream.Write($outputBytes, 0, $outputBytes.Length) } finally { $outputStream.Dispose() }
[ordered]@{ OutputPath = $outputPath; OutputSha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash; SourceRows = $sourceRows.Count; Initial61LinePrefixMatches = $prefixMatches; PageOffsets = @($largePages.StartIndex); ReturnedRecords = ($largePages.ReturnedItems | Measure-Object -Sum).Sum; DecodeUnbalancedRows = $decodeUnbalancedRows.Count } | ConvertTo-Json -Depth 5
