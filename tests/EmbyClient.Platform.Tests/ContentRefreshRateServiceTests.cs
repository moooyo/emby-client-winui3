using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ContentRefreshRateServiceTests
{
    [Fact]
    public async Task Only_enabled_fullscreen_actual_video_with_nominal_metadata_can_change_a_mode()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        var inputs = new[]
        {
            active with { Enabled = false },
            active with { IsFullscreen = false },
            active with { Playback = active.Playback with { PlaybackId = null } },
            active with { Playback = active.Playback with { IsPlayingVideo = false } },
            active with { Playback = active.Playback with { FramesPerSecond = null } },
            active with { Playback = active.Playback with { FramesPerSecond = double.NaN } }
        };
        foreach (var input in inputs) await service.UpdateAsync(input);
        Assert.Empty(backend.Changes);
        Assert.Equal(0, backend.WindowQueries);

        var result = await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.Matched, result.State);
        Assert.True(result.HasOwnedMode);
        Assert.Equal(24u, result.RefreshRate);
        Assert.Equal(new uint[] { 24 }, backend.Writes.Select(change => change.Desired.DisplayFrequency));
        Assert.Equal(FakeDisplayBackend.OriginalA with { DisplayFrequency = 24 }, backend.Current("DISPLAY-A"));
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Fullscreen")]
    [InlineData("Setting")]
    [InlineData("Metadata")]
    public async Task Retiring_an_eligible_input_restores_its_exact_original_mode(string retirement)
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        var retired = retirement switch
        {
            "Stop" => active with { Playback = new(null, false, null) },
            "Fullscreen" => active with { IsFullscreen = false },
            "Setting" => active with { Enabled = false },
            _ => active with { Playback = active.Playback with { FramesPerSecond = null } }
        };

        var result = await service.UpdateAsync(retired);

        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
        Assert.False(result.HasOwnedMode);
        Assert.False(service.NeedsRestoreRetry);
        Assert.Equal(new uint[] { 24, 60 }, backend.Writes.Select(change => change.Desired.DisplayFrequency));
    }

    [Fact]
    public async Task Moving_to_another_monitor_restores_the_old_device_before_changing_the_new_device()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        backend.WindowDisplay = "DISPLAY-B";

        var result = await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.Matched, result.State);
        Assert.Equal("DISPLAY-B", result.DisplayName);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
        Assert.Equal(FakeDisplayBackend.OriginalB with { DisplayFrequency = 24 }, backend.Current("DISPLAY-B"));
        Assert.Equal(new[] { "DISPLAY-A:24", "DISPLAY-A:60", "DISPLAY-B:24" },
            backend.Writes.Select(change => change.DisplayName + ":" + change.Desired.DisplayFrequency));
    }

    [Fact]
    public async Task A_pending_restore_blocks_acquisition_on_another_monitor_and_can_be_retried()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        backend.WindowDisplay = "DISPLAY-B";
        backend.RestoreFailures = 1;

        var pending = await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.RestorePending, pending.State);
        Assert.True(pending.HasOwnedMode);
        Assert.True(service.NeedsRestoreRetry);
        Assert.Equal(FakeDisplayBackend.OriginalB, backend.Current("DISPLAY-B"));
        var matched = await service.UpdateAsync(active);
        Assert.Equal(ContentRefreshRateState.Matched, matched.State);
        Assert.False(service.NeedsRestoreRetry);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
        Assert.Equal("DISPLAY-B", matched.DisplayName);
    }

    [Fact]
    public async Task An_external_display_change_is_preserved_and_suppresses_reapplication_for_the_active_clip()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        var external = FakeDisplayBackend.OriginalA with { DisplayFrequency = 75, Width = 2560 };
        backend.SetCurrent("DISPLAY-A", external);

        var changed = await service.UpdateAsync(active);
        await service.UpdateAsync(active);
        await service.RestoreAsync();

        Assert.Equal(ContentRefreshRateState.ExternalChange, changed.State);
        Assert.False(changed.HasOwnedMode);
        Assert.Equal(external, backend.Current("DISPLAY-A"));
        Assert.Single(backend.Writes);
    }

    [Fact]
    public async Task An_external_restore_to_the_original_mode_is_not_immediately_overwritten()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        backend.SetCurrent("DISPLAY-A", FakeDisplayBackend.OriginalA);

        var changed = await service.UpdateAsync(active);
        await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.ExternalChange, changed.State);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
        Assert.Single(backend.Writes);
    }

    [Fact]
    public async Task An_external_change_between_selection_and_the_native_test_is_never_applied_over()
    {
        var backend = new FakeDisplayBackend();
        var external = FakeDisplayBackend.OriginalA with { DisplayFrequency = 75 };
        backend.BeforeChange = (testOnly, _) =>
        {
            if (testOnly) backend.SetCurrent("DISPLAY-A", external);
        };
        await using var service = new ContentRefreshRateService(backend);

        var result = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.ExternalChange, result.State);
        Assert.False(result.HasOwnedMode);
        Assert.Empty(backend.Writes);
        Assert.Equal(external, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task An_external_change_before_restore_is_not_replaced_by_the_saved_original_mode()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        await service.UpdateAsync(Playing());
        var external = FakeDisplayBackend.OriginalA with { DisplayFrequency = 75 };
        backend.BeforeChange = (testOnly, desired) =>
        {
            if (!testOnly && desired.DisplayFrequency == 60) backend.SetCurrent("DISPLAY-A", external);
        };

        var result = await service.RestoreAsync();

        Assert.False(result.HasOwnedMode);
        Assert.False(service.NeedsRestoreRetry);
        Assert.Equal(external, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task An_old_clips_external_change_does_not_suppress_a_new_clip_after_a_temporary_monitor_query_failure()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        await service.UpdateAsync(Playing());
        backend.SetCurrent("DISPLAY-A", FakeDisplayBackend.OriginalA with { DisplayFrequency = 75 });
        var next = Playing();
        backend.WindowQueryFailures = 1;

        var failedQuery = await service.UpdateAsync(next);
        var matched = await service.UpdateAsync(next);

        Assert.Equal(ContentRefreshRateState.ExternalChange, failedQuery.State);
        Assert.Equal(ContentRefreshRateState.Matched, matched.State);
        Assert.True(matched.HasOwnedMode);
        Assert.Equal(24u, matched.RefreshRate);
        Assert.Equal(2, backend.Writes.Count);
    }

    [Fact]
    public async Task A_rejected_single_rate_can_fall_back_to_a_supported_integer_multiple()
    {
        var backend = new FakeDisplayBackend { RejectedTestFrequency = 24 };
        await using var service = new ContentRefreshRateService(backend);

        var result = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.Matched, result.State);
        Assert.Equal(48u, result.RefreshRate);
        Assert.Equal(new uint[] { 24, 48 }, backend.Changes.Where(change => change.TestOnly)
            .Select(change => change.Desired.DisplayFrequency));
    }

    [Fact]
    public async Task No_compatible_mode_remains_unavailable_without_repeated_native_tests()
    {
        var backend = new FakeDisplayBackend();
        backend.SetModes("DISPLAY-A", [FakeDisplayBackend.OriginalA]);
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();

        var first = await service.UpdateAsync(active);
        var second = await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.Unsupported, first.State);
        Assert.Equal(ContentRefreshRateReason.NoCompatibleMode, second.Reason);
        Assert.Empty(backend.Changes);
        Assert.Equal(1, backend.ModeEnumerations);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_failed_native_change_after_application_restores_the_attempted_owned_mode()
    {
        var backend = new FakeDisplayBackend { FailAfterApplyOnce = true };
        await using var service = new ContentRefreshRateService(backend);

        var result = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.Unsupported, result.State);
        Assert.Equal(ContentRefreshRateReason.ChangeFailed, result.Reason);
        Assert.Equal(-1, result.NativeResult);
        Assert.False(result.HasOwnedMode);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
        Assert.Equal(new uint[] { 24, 60 }, backend.Writes.Select(change => change.Desired.DisplayFrequency));
    }

    [Fact]
    public async Task An_exception_after_application_also_restores_before_reporting_failure()
    {
        var backend = new FakeDisplayBackend { ThrowAfterApplyOnce = true };
        await using var service = new ContentRefreshRateService(backend);

        var result = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.Unsupported, result.State);
        Assert.Equal(ContentRefreshRateReason.ChangeFailed, result.Reason);
        Assert.False(result.HasOwnedMode);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task Readback_failure_retains_restore_ownership_instead_of_claiming_a_match()
    {
        var backend = new FakeDisplayBackend { ReadFailuresAfterApply = 2 };
        await using var service = new ContentRefreshRateService(backend);

        var failed = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.RestorePending, failed.State);
        Assert.True(service.NeedsRestoreRetry);
        Assert.True(failed.HasOwnedMode);
        var restored = await service.RestoreAsync();
        Assert.False(restored.HasOwnedMode);
        Assert.False(service.NeedsRestoreRetry);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_success_code_without_confirmed_mode_is_not_reported_as_matched_or_reapplied_every_tick()
    {
        var backend = new FakeDisplayBackend { ReturnToOriginalAfterApply = true };
        await using var service = new ContentRefreshRateService(backend);
        var active = Playing();

        var failed = await service.UpdateAsync(active);
        await service.UpdateAsync(active);

        Assert.Equal(ContentRefreshRateState.Unsupported, failed.State);
        Assert.Equal(ContentRefreshRateReason.ReadbackFailed, failed.Reason);
        Assert.False(failed.HasOwnedMode);
        Assert.Single(backend.Writes);
        Assert.Equal(1, backend.ModeEnumerations);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_later_stop_supersedes_a_blocked_mode_test_without_applying_the_old_request()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        backend.BeforeChange = (testOnly, desired) =>
        {
            if (!testOnly || desired.DisplayFrequency != 24) return;
            entered.Set();
            if (!released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the backend.");
        };
        var active = Playing();
        var opening = service.UpdateAsync(active);
        var signalled = entered.Wait(TimeSpan.FromSeconds(10));
        if (!signalled) { released.Set(); Assert.Fail("The mode test was not reached."); }
        var stopping = service.UpdateAsync(active with { Playback = new(null, false, null) });
        released.Set();

        await Task.WhenAll(opening, stopping);

        Assert.Empty(backend.Writes);
        Assert.Equal(ContentRefreshRateState.Inactive, service.Status.State);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_stop_arriving_during_a_native_change_restores_before_the_pump_completes()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        backend.BeforeChange = (testOnly, desired) =>
        {
            if (testOnly || desired.DisplayFrequency != 24) return;
            entered.Set();
            if (!released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the backend.");
        };
        var active = Playing();
        var opening = service.UpdateAsync(active);
        var signalled = entered.Wait(TimeSpan.FromSeconds(10));
        if (!signalled) { released.Set(); Assert.Fail("The native change was not reached."); }
        var stopping = service.UpdateAsync(active with { Enabled = false });
        released.Set();

        await Task.WhenAll(opening, stopping);

        Assert.Equal(new uint[] { 24, 60 }, backend.Writes.Select(change => change.Desired.DisplayFrequency));
        Assert.Equal(ContentRefreshRateState.Disabled, service.Status.State);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_display_change_notification_during_the_same_request_does_not_cause_a_restore_apply_loop()
    {
        var backend = new FakeDisplayBackend();
        await using var service = new ContentRefreshRateService(backend);
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        backend.BeforeChange = (testOnly, desired) =>
        {
            if (testOnly || desired.DisplayFrequency != 24) return;
            entered.Set();
            if (!released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the backend.");
        };
        var active = Playing();
        var opening = service.UpdateAsync(active);
        var signalled = entered.Wait(TimeSpan.FromSeconds(10));
        if (!signalled) { released.Set(); Assert.Fail("The native change was not reached."); }
        var displayChanged = service.UpdateAsync(active);
        released.Set();

        await Task.WhenAll(opening, displayChanged);

        Assert.Single(backend.Writes);
        Assert.Equal(ContentRefreshRateState.Matched, service.Status.State);
        Assert.True(service.Status.HasOwnedMode);
        Assert.Equal(FakeDisplayBackend.OriginalA with { DisplayFrequency = 24 }, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task Disposal_preserves_failed_restore_state_and_permanently_rejects_late_playing_updates()
    {
        var backend = new FakeDisplayBackend();
        var service = new ContentRefreshRateService(backend);
        var active = Playing();
        await service.UpdateAsync(active);
        backend.RestoreFailures = 1;

        await service.DisposeAsync();

        Assert.Equal(ContentRefreshRateState.RestorePending, service.Status.State);
        Assert.True(service.NeedsRestoreRetry);
        await service.DisposeAsync();
        Assert.Equal(ContentRefreshRateState.Closed, service.Status.State);
        Assert.False(service.NeedsRestoreRetry);
        var writes = backend.Writes.Count;
        await service.UpdateAsync(active);
        Assert.Equal(writes, backend.Writes.Count);
        Assert.Equal(FakeDisplayBackend.OriginalA, backend.Current("DISPLAY-A"));
    }

    [Fact]
    public async Task A_missing_platform_does_not_probe_or_change_a_display()
    {
        var backend = new FakeDisplayBackend { IsSupported = false };
        await using var service = new ContentRefreshRateService(backend);

        var result = await service.UpdateAsync(Playing());

        Assert.Equal(ContentRefreshRateState.Unsupported, result.State);
        Assert.Equal(ContentRefreshRateReason.PlatformUnavailable, result.Reason);
        Assert.Equal(0, backend.WindowQueries);
        Assert.Empty(backend.Changes);
    }

    private static ContentRefreshRateRequest Playing() => new((nint)1, true, true, new(Guid.NewGuid(), true, 23.976));

    private sealed record Change(string DisplayName, ContentDisplayMode Desired, bool TestOnly);

    private sealed class FakeDisplayBackend : IContentRefreshRateDisplayBackend
    {
        public static readonly ContentDisplayMode OriginalA = new(3840, 2160, 32, 60, X: 1920);
        public static readonly ContentDisplayMode OriginalB = new(1920, 1080, 32, 60, X: -1920);
        private readonly object _gate = new();
        private readonly Dictionary<string, ContentDisplayMode> _current = new()
        {
            ["DISPLAY-A"] = OriginalA, ["DISPLAY-B"] = OriginalB
        };
        private readonly Dictionary<string, IReadOnlyList<ContentDisplayMode>> _modes = new()
        {
            ["DISPLAY-A"] = new uint[] { 24, 48, 50, 60, 72 }.Select(rate => OriginalA with { DisplayFrequency = rate }).ToArray(),
            ["DISPLAY-B"] = new uint[] { 24, 48, 50, 60, 72 }.Select(rate => OriginalB with { DisplayFrequency = rate }).ToArray()
        };
        private int _readFailures;
        public bool IsSupported { get; set; } = true;
        public string WindowDisplay { get; set; } = "DISPLAY-A";
        public int WindowQueries { get; private set; }
        public int WindowQueryFailures { get; set; }
        public int ModeEnumerations { get; private set; }
        public uint? RejectedTestFrequency { get; set; }
        public int RestoreFailures { get; set; }
        public bool FailAfterApplyOnce { get; set; }
        public bool ThrowAfterApplyOnce { get; set; }
        public bool ReturnToOriginalAfterApply { get; set; }
        public int ReadFailuresAfterApply { get; set; }
        public Action<bool, ContentDisplayMode>? BeforeChange { get; set; }
        public List<Change> Changes { get; } = [];
        public List<Change> Writes => Changes.Where(change => !change.TestOnly).ToList();

        public ContentDisplayMode Current(string displayName) { lock (_gate) return _current[displayName]; }
        public void SetCurrent(string displayName, ContentDisplayMode mode) { lock (_gate) _current[displayName] = mode; }
        public void SetModes(string displayName, IReadOnlyList<ContentDisplayMode> modes) { lock (_gate) _modes[displayName] = modes; }

        public bool TryGetWindowDisplay(nint windowHandle, out ContentDisplaySnapshot display)
        {
            lock (_gate)
            {
                WindowQueries++;
                if (WindowQueryFailures > 0)
                {
                    WindowQueryFailures--;
                    display = null!;
                    return false;
                }
                display = new(WindowDisplay, _current[WindowDisplay]);
                return true;
            }
        }

        public bool TryReadMode(string displayName, out ContentDisplayMode mode)
        {
            lock (_gate)
            {
                mode = _current[displayName];
                if (_readFailures <= 0) return true;
                _readFailures--;
                return false;
            }
        }

        public IReadOnlyList<ContentDisplayMode> GetModes(string displayName)
        {
            lock (_gate) { ModeEnumerations++; return _modes[displayName]; }
        }

        public ContentDisplayChangeResult TryChangeMode(
            string displayName, ContentDisplayMode expectedCurrent, ContentDisplayMode desired, bool testOnly)
        {
            BeforeChange?.Invoke(testOnly, desired);
            lock (_gate)
            {
                Changes.Add(new(displayName, desired, testOnly));
                if (_current[displayName] != expectedCurrent) return new(false, ExternalChange: true);
                if (testOnly) return RejectedTestFrequency == desired.DisplayFrequency
                    ? new(false, NativeResult: -2) : new(true, NativeResult: 0);
                if (desired.DisplayFrequency == 60 && RestoreFailures > 0)
                {
                    RestoreFailures--;
                    return new(false, NativeResult: -1);
                }
                _current[displayName] = desired;
                if (desired.DisplayFrequency != 60)
                {
                    if (ReturnToOriginalAfterApply)
                        _current[displayName] = displayName == "DISPLAY-A" ? OriginalA : OriginalB;
                    _readFailures = ReadFailuresAfterApply;
                    ReadFailuresAfterApply = 0;
                    if (ThrowAfterApplyOnce)
                    {
                        ThrowAfterApplyOnce = false;
                        throw new InvalidOperationException("The native backend failed after applying the mode.");
                    }
                    if (FailAfterApplyOnce) { FailAfterApplyOnce = false; return new(false, NativeResult: -1); }
                }
                return new(true, NativeResult: 0);
            }
        }
    }
}
