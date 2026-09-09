using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackDisplayRequestTests
{
    [Fact]
    public void Repeated_playing_notifications_acquire_only_one_request()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();

        for (var index = 0; index < 20; index++) owner.Update(id, id, true);

        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.Balance);
        owner.Suspend();
        owner.Suspend();
        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void Nonplaying_or_nonvideo_state_releases_once_and_can_resume()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(id, id, true);

        owner.Update(id, id, false);
        owner.Update(id, id, false);

        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
        owner.Update(id, id, true);
        Assert.Equal(2, native.ActiveCalls);
        Assert.Equal(1, native.MaximumBalance);
    }

    [Fact]
    public void Missing_active_context_never_acquires_and_releases_an_existing_request()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        owner.ResumeTracking();
        owner.Update(null, null, true);
        Assert.Equal(0, native.ActiveCalls);
        var id = Guid.NewGuid();
        owner.Update(id, id, true);

        owner.Update(null, null, false);

        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void A_new_playback_releases_the_previous_ownership_before_acquiring()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(first, first, true);

        owner.Update(second, second, true);

        Assert.Equal(new[] { "Active", "Release", "Active" }, native.Calls);
        Assert.Equal(1, native.MaximumBalance);
    }

    [Fact]
    public void Delayed_old_playing_or_stop_notifications_cannot_change_the_new_playback_request()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var oldId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        owner.ResumeTracking();

        owner.Update(currentId, oldId, true);
        owner.Update(currentId, null, true);
        Assert.Equal(0, native.ActiveCalls);
        owner.Update(currentId, currentId, true);
        owner.Update(currentId, oldId, false);
        owner.Update(currentId, oldId, true);

        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(0, native.ReleaseCalls);
        Assert.Equal(1, native.Balance);
    }

    [Fact]
    public void Stop_suspends_tracking_until_an_explicit_new_play_action()
    {
        var native = new FakeDisplayRequest();
        using var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.Update(id, id, true);
        Assert.Equal(0, native.ActiveCalls);
        owner.ResumeTracking();
        owner.Update(id, id, true);

        owner.Suspend();
        owner.Update(id, id, true);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(0, native.Balance);

        owner.ResumeTracking();
        var next = Guid.NewGuid();
        owner.Update(next, next, true);
        Assert.Equal(2, native.ActiveCalls);
        Assert.Equal(1, native.MaximumBalance);
    }

    [Fact]
    public void Request_creation_failure_is_isolated_and_retried()
    {
        var native = new FakeDisplayRequest();
        var attempts = 0;
        using var owner = new PlaybackDisplayRequest(() => ++attempts == 1
            ? throw new InvalidOperationException("Display requests unavailable.") : native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();

        owner.Update(id, id, true);
        owner.Update(id, id, true);

        Assert.Equal(2, attempts);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.Balance);
    }

    [Fact]
    public void Activation_failure_does_not_create_a_release_debt_and_can_retry()
    {
        var native = new FakeDisplayRequest { ActiveFailures = 1 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(id, id, true);
        owner.Update(id, id, false);
        Assert.Equal(0, native.ReleaseCalls);

        owner.Update(id, id, true);

        Assert.Equal(2, native.ActiveCalls);
        Assert.Equal(1, native.Balance);
        owner.Suspend();
        Assert.Equal(1, native.ReleaseCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void Failed_release_blocks_new_ownership_until_the_old_request_is_released()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 2 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(first, first, true);

        owner.Update(second, second, true);
        owner.Update(second, second, true);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(1, native.Balance);
        owner.Update(second, second, true);

        Assert.Equal(3, native.ReleaseCalls);
        Assert.Equal(2, native.ActiveCalls);
        Assert.Equal(1, native.MaximumBalance);
    }

    [Fact]
    public void Suspended_updates_retry_a_failed_release_without_reacquiring()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 1 };
        using var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(id, id, true);

        owner.Suspend();
        owner.Update(id, id, true);

        Assert.Equal(2, native.ReleaseCalls);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void Dispose_retries_failed_release_but_permanently_rejects_late_playing()
    {
        var native = new FakeDisplayRequest { ReleaseFailures = 1 };
        var owner = new PlaybackDisplayRequest(() => native);
        var id = Guid.NewGuid();
        owner.ResumeTracking();
        owner.Update(id, id, true);

        owner.Dispose();
        owner.Dispose();
        owner.ResumeTracking();
        owner.Update(id, id, true);
        owner.Dispose();

        Assert.Equal(2, native.ReleaseCalls);
        Assert.Equal(1, native.ActiveCalls);
        Assert.Equal(0, native.Balance);
    }

    [Fact]
    public void Disposing_an_unused_owner_never_constructs_a_platform_request()
    {
        var constructions = 0;
        var owner = new PlaybackDisplayRequest(() => { constructions++; return new FakeDisplayRequest(); });

        owner.Dispose();
        owner.ResumeTracking();
        var id = Guid.NewGuid();
        owner.Update(id, id, true);

        Assert.Equal(0, constructions);
    }

    private sealed class FakeDisplayRequest : IPlaybackDisplayRequest
    {
        public int ActiveFailures { get; set; }
        public int ReleaseFailures { get; set; }
        public int ActiveCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int Balance { get; private set; }
        public int MaximumBalance { get; private set; }
        public List<string> Calls { get; } = [];

        public void RequestActive()
        {
            ActiveCalls++;
            if (ActiveFailures-- > 0) throw new InvalidOperationException("Display activation failed.");
            Calls.Add("Active");
            Balance++;
            MaximumBalance = Math.Max(MaximumBalance, Balance);
        }

        public void RequestRelease()
        {
            ReleaseCalls++;
            if (ReleaseFailures-- > 0) throw new InvalidOperationException("Display release failed.");
            Calls.Add("Release");
            Assert.Equal(1, Balance);
            Balance--;
        }
    }
}
