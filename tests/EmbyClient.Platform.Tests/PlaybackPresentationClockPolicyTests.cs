using EmbyClient.App.Services;
using EmbyClient.App.Views;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackPresentationClockPolicyTests
{
    [Fact]
    public void Disconnecting_after_two_failed_releases_schedules_cleanup_without_a_coordinator()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 2 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);

        owner.Suspend();
        clock.HasCoordinator = false;
        clock.CurrentPlaybackId = null;
        clock.IsPlayingVideo = false;
        clock.Reconcile();
        Assert.True(clock.IsEnabled);
        owner.Suspend();
        clock.Reconcile();

        Assert.Equal(2, native.ReleaseCalls);
        Assert.Equal(1, native.Balance);
        Assert.True(clock.IsEnabled);
        clock.Tick();

        Assert.Equal(1, clock.TickCount);
        Assert.Equal(0, clock.PresentationTickCount);
        Assert.Equal(3, native.ReleaseCalls);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(0, native.Balance);
        Assert.False(owner.NeedsReleaseRetry);
        Assert.False(clock.IsEnabled);
        clock.Tick();
        Assert.Equal(1, clock.TickCount);
    }

    [Fact]
    public void Detached_cleanup_remains_scheduled_across_multiple_failures_and_stops_after_success()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 5 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);

        owner.Suspend();
        clock.HasCoordinator = false;
        clock.NeedsPlaybackPolling = false;
        clock.CurrentPlaybackId = null;
        clock.IsPlayingVideo = false;
        clock.Reconcile();
        owner.Suspend();
        clock.Reconcile();

        for (var index = 0; index < 3; index++)
        {
            clock.Tick();
            Assert.True(owner.NeedsReleaseRetry);
            Assert.True(clock.IsEnabled);
            Assert.Equal(1, native.Balance);
            Assert.Equal(1, native.ActiveCalls);
        }
        clock.Tick();

        Assert.Equal(4, clock.TickCount);
        Assert.Equal(0, clock.PresentationTickCount);
        Assert.Equal(6, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
        Assert.False(clock.IsEnabled);
        clock.Tick();
        Assert.Equal(4, clock.TickCount);
    }

    [Fact]
    public void A_successful_disconnect_stops_the_clock_even_with_a_stale_playback_polling_flag()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);

        owner.Suspend();
        clock.HasCoordinator = false;
        clock.CurrentPlaybackId = null;
        clock.IsPlayingVideo = false;
        clock.Reconcile();
        owner.Suspend();
        clock.Reconcile();
        clock.Tick();

        Assert.True(clock.NeedsPlaybackPolling);
        Assert.False(clock.IsEnabled);
        Assert.Equal(0, clock.TickCount);
        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void Late_playing_updates_cannot_reacquire_ownership_during_or_after_detached_cleanup()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 3 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);
        var retiredPlaybackId = clock.CurrentPlaybackId;

        owner.Suspend();
        clock.HasCoordinator = false;
        clock.NeedsPlaybackPolling = false;
        clock.CurrentPlaybackId = null;
        clock.IsPlayingVideo = false;
        clock.Reconcile();
        owner.Suspend();
        clock.Reconcile();
        owner.Update(null, retiredPlaybackId, isPlayingVideo: true);
        clock.Reconcile();

        Assert.True(clock.IsEnabled);
        Assert.Equal(1, native.ActiveCalls);
        clock.Tick();
        owner.Update(retiredPlaybackId, retiredPlaybackId, isPlayingVideo: true);
        clock.Reconcile();

        Assert.False(clock.IsEnabled);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(4, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void A_new_session_cannot_acquire_before_detached_release_debt_is_settled()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 4 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);

        owner.Suspend();
        clock.HasCoordinator = false;
        clock.NeedsPlaybackPolling = false;
        clock.CurrentPlaybackId = null;
        clock.IsPlayingVideo = false;
        clock.Reconcile();
        owner.Suspend();
        clock.Reconcile();

        clock.HasCoordinator = true;
        clock.Reconcile();
        Assert.True(clock.IsEnabled);
        Assert.Equal(1, native.ActiveCalls);
        var nextPlaybackId = Guid.NewGuid();
        owner.ResumeTracking();
        clock.CurrentPlaybackId = nextPlaybackId;
        clock.NeedsPlaybackPolling = true;
        clock.IsPlayingVideo = true;
        owner.Update(nextPlaybackId, nextPlaybackId, isPlayingVideo: true);
        clock.Reconcile();
        clock.Tick();

        Assert.True(owner.NeedsReleaseRetry);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.Balance);
        clock.Tick();

        Assert.False(owner.NeedsReleaseRetry);
        Assert.Equal(5, native.ReleaseCalls);
        Assert.Equal(2, native.ActiveCalls);
        Assert.Equal(1, native.MaximumBalance);
        Assert.Equal(new[] { "Active", "Release", "Active" }, native.SuccessfulCalls);
        Assert.True(clock.IsEnabled);
    }

    [Fact]
    public void Idle_and_paused_coordinators_keep_only_their_required_clock_work()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var clock = StartPlaying(owner);

        clock.IsPlayingVideo = false;
        clock.Tick();
        Assert.True(clock.IsEnabled);
        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(1, clock.PresentationTickCount);

        clock.NeedsPlaybackPolling = false;
        clock.CurrentPlaybackId = null;
        clock.Reconcile();
        clock.Tick();

        Assert.False(clock.IsEnabled);
        Assert.Equal(1, clock.TickCount);
        Assert.Equal(0, native.Balance);
    }

    private static ClockDriver StartPlaying(PlaybackDisplayRequest owner)
    {
        var playbackId = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(playbackId, playbackId, isPlayingVideo: true);
        var clock = new ClockDriver(owner)
        {
            HasCoordinator = true,
            NeedsPlaybackPolling = true,
            CurrentPlaybackId = playbackId,
            IsPlayingVideo = true
        };
        clock.Reconcile();
        return clock;
    }

    private sealed class ClockDriver(PlaybackDisplayRequest owner)
    {
        public bool HasCoordinator { get; set; }
        public bool NeedsPlaybackPolling { get; set; }
        public Guid? CurrentPlaybackId { get; set; }
        public bool IsPlayingVideo { get; set; }
        public bool IsEnabled { get; private set; }
        public int TickCount { get; private set; }
        public int PresentationTickCount { get; private set; }

        public void Reconcile() => IsEnabled = PlaybackPresentationClockPolicy.ShouldRun(
            HasCoordinator, NeedsPlaybackPolling, owner.NeedsReleaseRetry);

        public void Tick()
        {
            if (!IsEnabled) return;
            TickCount++;
            owner.Update(CurrentPlaybackId, CurrentPlaybackId, IsPlayingVideo);
            if (HasCoordinator) PresentationTickCount++;
            Reconcile();
        }
    }

    private sealed class FakeDisplayRequest : IPlaybackDisplayRequest
    {
        public int ReleaseFailures { get; set; }
        public int ActiveCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int Balance { get; private set; }
        public int MaximumBalance { get; private set; }
        public List<string> SuccessfulCalls { get; } = [];

        public void RequestActive()
        {
            ActiveCalls++;
            Balance++;
            MaximumBalance = Math.Max(MaximumBalance, Balance);
            SuccessfulCalls.Add("Active");
        }

        public void RequestRelease()
        {
            ReleaseCalls++;
            if (ReleaseFailures-- > 0) throw new InvalidOperationException("Display release failed.");
            Assert.Equal(1, Balance);
            Balance--;
            SuccessfulCalls.Add("Release");
        }
    }
}
