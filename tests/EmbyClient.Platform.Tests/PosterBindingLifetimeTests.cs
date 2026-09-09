using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PosterBindingLifetimeTests
{
    [Fact]
    public void Recycling_and_reusing_the_same_item_rejects_the_old_deferred_callback()
    {
        var realization = new PosterRealization<object>();
        var item = new object();
        var beforeRecycle = realization.Activate(item);
        realization.Retire();
        var afterRecycle = realization.Activate(item);

        Assert.False(realization.Owns(beforeRecycle, item));
        Assert.True(realization.Owns(afterRecycle, item));
        Assert.Equal(afterRecycle, realization.Activate(item));
    }

    [Fact]
    public void Assigning_a_different_item_invalidates_the_old_deferred_callback()
    {
        var realization = new PosterRealization<object>();
        var oldItem = new object();
        var newItem = new object();
        var delayed = realization.Activate(oldItem);
        var current = realization.Activate(newItem);

        Assert.False(realization.Owns(delayed, oldItem));
        Assert.False(realization.TryGetVersion(oldItem, out _));
        Assert.True(realization.Owns(current, newItem));
    }

    [Fact]
    public void Tag_change_loaded_and_phase_callback_share_one_load_for_a_realized_binding()
    {
        var load = new PosterLoadState<object>();
        var item = new object();
        var owner = new object();
        Assert.True(load.TryBegin(item, owner, 1, 264, 396, false, out var tagChange));
        Assert.False(load.TryBegin(item, owner, 1, 264, 396, false, out _));
        load.Complete(tagChange, assigned: true);

        Assert.False(load.TryBegin(item, owner, 1, 264, 396, true, out _));
        Assert.True(load.TryBegin(item, owner, 2, 264, 396, false, out _));
    }

    [Fact]
    public void A_late_cancelled_completion_cannot_release_or_overwrite_the_replacement_load()
    {
        var load = new PosterLoadState<object>();
        var item = new object();
        var retiredOwner = new object();
        var currentOwner = new object();
        Assert.True(load.TryBegin(item, retiredOwner, 1, 264, 396, false, out var retired));
        load.Reset();
        Assert.True(load.TryBegin(item, currentOwner, 1, 264, 396, false, out var current));
        load.Complete(retired, assigned: true);

        Assert.False(load.Owns(retired));
        Assert.False(load.IsOwnedBy(retiredOwner));
        Assert.True(load.IsOwnedBy(currentOwner));
        Assert.True(load.Owns(current));
        Assert.False(load.TryBegin(item, currentOwner, 1, 264, 396, false, out _));
    }

    [Fact]
    public void Failure_is_not_retried_by_a_duplicate_phase_but_explicit_reset_allows_a_new_attempt()
    {
        var load = new PosterLoadState<object>();
        var item = new object();
        var owner = new object();
        Assert.True(load.TryBegin(item, owner, 1, 264, 396, false, out var first));
        load.Complete(first, assigned: false);

        Assert.False(load.TryBegin(item, owner, 1, 264, 396, false, out _));
        load.Reset();
        Assert.True(load.TryBegin(item, owner, 1, 264, 396, false, out var retry));
        Assert.True(load.Owns(retry));
        Assert.False(load.Owns(first));
    }

    [Fact]
    public void A_cleared_source_or_different_decode_size_can_be_loaded_again()
    {
        var load = new PosterLoadState<object>();
        var item = new object();
        var owner = new object();
        Assert.True(load.TryBegin(item, owner, 1, 176, 264, false, out var original));
        load.Complete(original, assigned: true);
        Assert.True(load.TryBegin(item, owner, 1, 176, 264, false, out var cleared));
        load.Complete(cleared, assigned: true);

        Assert.True(load.TryBegin(item, owner, 1, 264, 396, true, out var resized));
        Assert.True(load.Owns(resized));
    }
}
