using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackNotificationOwnerTests
{
    [Fact]
    public void An_ended_callback_queued_before_new_item_loading_cannot_render_or_advance_that_item()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var first = Guid.NewGuid();
        Start(owner, coordinator, 1, first);
        var ended = owner.Capture(coordinator, first, null, isEnded: true)!.Value;
        var renderCount = 0;
        var advanceCount = 0;
        Action queuedCallback = () =>
        {
            if (!owner.IsCurrent(ended)) return;
            renderCount++;
            advanceCount++;
        };

        owner.BeginPlay(coordinator, 2, null);
        queuedCallback();

        Assert.Equal(0, renderCount);
        Assert.Equal(0, advanceCount);
    }

    [Fact]
    public void An_old_end_emitted_during_details_loading_or_after_handoff_is_not_bound_to_the_new_intent()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var first = Guid.NewGuid();
        Start(owner, coordinator, 1, first);

        owner.BeginPlay(coordinator, 2, null);
        Assert.Null(owner.Capture(coordinator, first, null, isEnded: true));
        owner.Arm(2);
        Assert.Null(owner.Capture(coordinator, first, null, isEnded: true));
        Assert.Null(owner.Capture(coordinator, first, first, isOpening: true));

        var second = Guid.NewGuid();
        var opening = owner.Capture(coordinator, second, second, isOpening: true)!.Value;
        Assert.True(owner.IsCurrent(opening));
        Assert.Null(owner.Capture(coordinator, first, second, isEnded: true));
    }

    [Fact]
    public void A_normal_end_retains_ownership_after_retirement_but_loses_it_before_late_episode_lookup_returns()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var id = Guid.NewGuid();
        Start(owner, coordinator, 1, id);

        var ended = owner.Capture(coordinator, id, null, isEnded: true)!.Value;
        Assert.True(owner.IsCurrent(ended));
        var episodeLookupStarted = owner.TryBeginAdvance(ended);
        Assert.False(owner.TryBeginAdvance(ended));
        owner.BeginPlay(coordinator, 2, null);
        var wouldStartReturnedEpisode = owner.IsCurrent(ended);

        Assert.True(episodeLookupStarted);
        Assert.False(wouldStartReturnedEpisode);
    }

    [Fact]
    public void Source_change_and_fallback_replace_the_bound_id_without_creating_a_new_user_intent()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var original = Guid.NewGuid();
        var changed = Guid.NewGuid();
        var fallback = Guid.NewGuid();
        Start(owner, coordinator, 1, original);
        var oldEnded = owner.Capture(coordinator, original, null, isEnded: true)!.Value;

        var negotiating = owner.Capture(coordinator, null, null, isNegotiating: true)!.Value;
        Assert.False(owner.IsCurrent(oldEnded));
        Assert.Null(owner.Capture(coordinator, original, null, isEnded: true));
        var changedOpening = owner.Capture(coordinator, changed, changed, isOpening: true)!.Value;
        Assert.False(owner.IsCurrent(negotiating));
        owner.Capture(coordinator, null, null, isNegotiating: true);
        var fallbackOpening = owner.Capture(coordinator, fallback, fallback, isOpening: true)!.Value;

        Assert.False(owner.IsCurrent(changedOpening));
        Assert.True(owner.IsCurrent(fallbackOpening));
        Assert.Null(owner.Capture(coordinator, changed, fallback));
        Assert.True(owner.IsCurrent(owner.Capture(coordinator, fallback, null, isEnded: true)!.Value));
    }

    [Fact]
    public void Stop_allows_its_idle_update_without_auto_advance_and_disconnect_rejects_all_late_notifications()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var id = Guid.NewGuid();
        Start(owner, coordinator, 1, id);
        var ended = owner.Capture(coordinator, id, null, isEnded: true)!.Value;

        owner.BeginStop(coordinator, 2, id);
        Assert.False(owner.IsCurrent(ended));
        Assert.Null(owner.Capture(coordinator, id, null, isEnded: true));
        var idle = owner.Capture(coordinator, id, null)!.Value;
        Assert.True(owner.IsCurrent(idle));
        Assert.False(owner.TryBeginAdvance(idle));
        owner.Invalidate(3);

        Assert.False(owner.IsCurrent(idle));
        Assert.Null(owner.Capture(coordinator, id, id, isOpening: true));
    }

    [Fact]
    public void Replay_requires_a_fresh_playback_id_and_an_old_coordinator_cannot_adopt_the_new_session()
    {
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var oldId = Guid.NewGuid();
        Start(owner, coordinator, 1, oldId);
        owner.BeginPlay(coordinator, 2, oldId);
        owner.Arm(2);

        Assert.Null(owner.Capture(coordinator, oldId, oldId, isOpening: true));
        var replayId = Guid.NewGuid();
        Assert.True(owner.IsCurrent(owner.Capture(coordinator, replayId, replayId, isOpening: true)!.Value));
        var nextCoordinator = new object();
        owner.BeginPlay(nextCoordinator, 3, null);
        owner.Arm(3);
        Assert.Null(owner.Capture(coordinator, replayId, replayId, isOpening: true));
        var nextId = Guid.NewGuid();
        Assert.Null(owner.Capture(nextCoordinator, nextId, replayId, isOpening: true));
        Assert.True(owner.IsCurrent(owner.Capture(nextCoordinator, nextId, nextId, isOpening: true)!.Value));
    }

    private static void Start(PlaybackNotificationOwner owner, object coordinator, long intent, Guid id)
    {
        owner.BeginPlay(coordinator, intent, null);
        owner.Arm(intent);
        Assert.True(owner.IsCurrent(owner.Capture(coordinator, id, id, isOpening: true)!.Value));
    }
}
