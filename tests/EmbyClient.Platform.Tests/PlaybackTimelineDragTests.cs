using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackTimelineDragTests
{
    [Fact]
    public void The_owning_pointer_can_commit_its_playback_only_once()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        Assert.True(drag.IsActive);
        Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Other_pointer_events_cannot_replace_commit_or_cancel_the_owner()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);
        drag.Begin(2, Guid.NewGuid());
        drag.Begin(1, Guid.NewGuid());

        Assert.Null(drag.Capture(2));
        Assert.Null(drag.Complete(2, null, canSeek: false));
        Assert.True(drag.IsActive);
        Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Replacing_the_playback_cancels_the_drag()
    {
        var drag = new PlaybackTimelineDrag();
        var original = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        drag.Begin(1, original);

        drag.Reconcile(replacement, canSeek: true);

        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, replacement, canSeek: true));
        Assert.Null(drag.Complete(1, original, canSeek: true));
    }

    [Fact]
    public void Removing_the_playback_cancels_the_drag()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        drag.Reconcile(null, canSeek: true);

        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Disabling_seek_cancels_the_drag_even_if_seek_is_later_enabled()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        drag.Reconcile(playbackId, canSeek: false);
        drag.Reconcile(playbackId, canSeek: true);

        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Reconciliation_preserves_a_drag_while_its_playback_remains_seekable()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        drag.Reconcile(playbackId, canSeek: true);

        Assert.True(drag.IsActive);
        Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Completion_rejects_changed_playback_or_disabled_seek_without_prior_reconciliation(
        bool samePlayback, bool canSeek)
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        Assert.Null(drag.Complete(1, samePlayback ? playbackId : Guid.NewGuid(), canSeek));
        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Completion_rejects_a_missing_playback_without_prior_reconciliation()
    {
        var drag = new PlaybackTimelineDrag();
        drag.Begin(1, Guid.NewGuid());

        Assert.Null(drag.Complete(1, null, canSeek: true));
        Assert.False(drag.IsActive);
    }

    [Fact]
    public void Clearing_the_drag_prevents_its_release_from_committing()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);

        drag.Clear();

        Assert.False(drag.IsActive);
        Assert.Null(drag.Capture(1));
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void Capture_loss_cancels_only_the_matching_ticket()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);
        var ticket = drag.Capture(1)!.Value;

        drag.Cancel(ticket with { PointerId = 2 });
        drag.Cancel(ticket with { PlaybackId = Guid.NewGuid() });
        Assert.True(drag.IsActive);

        drag.Cancel(ticket);
        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deferred_capture_loss_cannot_cancel_a_new_drag_with_the_same_pointer_and_playback(bool completeFirst)
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);
        var ticket = drag.Capture(1)!.Value;
        Action deferredCaptureLoss = () => drag.Cancel(ticket);
        if (completeFirst) Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
        else drag.Clear();
        drag.Begin(1, playbackId);

        deferredCaptureLoss();

        Assert.True(drag.IsActive);
        Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
    }

    [Fact]
    public void A_release_can_commit_before_its_deferred_capture_loss_callback_runs()
    {
        var drag = new PlaybackTimelineDrag();
        var playbackId = Guid.NewGuid();
        drag.Begin(1, playbackId);
        var ticket = drag.Capture(1)!.Value;
        Action deferredCaptureLoss = () => drag.Cancel(ticket);

        Assert.Equal(playbackId, drag.Complete(1, playbackId, canSeek: true));
        deferredCaptureLoss();

        Assert.False(drag.IsActive);
        Assert.Null(drag.Complete(1, playbackId, canSeek: true));
    }
}
