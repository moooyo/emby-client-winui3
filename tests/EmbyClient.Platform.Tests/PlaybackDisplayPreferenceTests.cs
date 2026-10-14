using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackDisplayPreferenceTests
{
    [Fact]
    public void Disabling_sleep_prevention_releases_active_ownership_immediately()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var playback = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(playback, playback, isPlayingVideo: true);

        owner.SetEnabled(false);
        owner.Update(playback, playback, isPlayingVideo: true);

        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.ReleaseCalls);
        Assert.False(owner.NeedsReleaseRetry);
    }

    [Fact]
    public void Reenabling_sleep_prevention_waits_for_a_current_playback_update()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var playback = Guid.NewGuid();
        owner.ResumeTracking();
        owner.SetEnabled(false);
        owner.Update(playback, playback, isPlayingVideo: true);

        owner.SetEnabled(true);
        Assert.Equal(0, native.ActiveCalls);
        owner.Update(playback, Guid.NewGuid(), isPlayingVideo: true);
        Assert.Equal(0, native.ActiveCalls);
        owner.Update(playback, playback, isPlayingVideo: true);

        Assert.Equal(1, native.ActiveCalls);
    }

    [Fact]
    public void Failed_release_is_retried_while_disabled_before_another_request_can_be_acquired()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 2 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var playback = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(playback, playback, isPlayingVideo: true);

        owner.SetEnabled(false);
        Assert.True(owner.NeedsReleaseRetry);
        owner.Update(playback, playback, isPlayingVideo: true);
        Assert.True(owner.NeedsReleaseRetry);
        Assert.Equal(1, native.ActiveCalls);
        owner.SetEnabled(true);
        owner.Update(playback, playback, isPlayingVideo: true);

        Assert.False(owner.NeedsReleaseRetry);
        Assert.Equal(3, native.ReleaseCalls);
        Assert.Equal(2, native.ActiveCalls);
    }

    [Fact]
    public void Preference_changes_do_not_revive_suspended_or_disposed_owners()
    {
        var native = new FakeDisplayRequest();
        var owner = new PlaybackDisplayRequest(() => native);
        var playback = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(playback, playback, isPlayingVideo: true);
        owner.Suspend();

        owner.SetEnabled(false);
        owner.SetEnabled(true);
        owner.Update(playback, playback, isPlayingVideo: true);
        Assert.Equal(1, native.ActiveCalls);
        owner.Dispose();
        owner.ResumeTracking();
        owner.SetEnabled(false);
        owner.SetEnabled(true);
        owner.Update(playback, playback, isPlayingVideo: true);

        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.ReleaseCalls);
    }

    private sealed class FakeDisplayRequest : IPlaybackDisplayRequest
    {
        public int ActiveCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int ReleaseFailures { get; set; }

        public void RequestActive() => ActiveCalls++;
        public void RequestRelease()
        {
            ReleaseCalls++;
            if (ReleaseFailures-- > 0) throw new InvalidOperationException("Synthetic display release failure.");
        }
    }
}
