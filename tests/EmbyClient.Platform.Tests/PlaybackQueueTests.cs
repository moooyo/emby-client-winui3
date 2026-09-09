using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackQueueTests
{
    [Fact]
    public void The_queue_rejects_invalid_items_and_stops_at_its_declared_capacity()
    {
        var queue = new TransientPlaybackQueue();
        var changes = 0;
        queue.Changed += (_, _) => changes++;
        Assert.Equal(QueueAddResult.InvalidItem, queue.TryAdd(new BaseItemDto { Name = "Missing ID" }));
        Assert.Equal(QueueAddResult.InvalidItem, queue.TryAdd(Item("folder") with { IsFolder = true }));

        for (var index = 0; index < TransientPlaybackQueue.MaximumItems; index++)
            Assert.Equal(QueueAddResult.Added, queue.TryAdd(Item(index.ToString())));

        Assert.Equal(QueueAddResult.Full, queue.TryAdd(Item("overflow")));
        Assert.Equal(TransientPlaybackQueue.MaximumItems, queue.Count);
        Assert.Equal(TransientPlaybackQueue.MaximumItems, changes);
        Assert.DoesNotContain(queue.Items, entry => entry.Item.Id == "overflow");
    }

    [Fact]
    public void Duplicate_media_entries_have_independent_identity_and_removal()
    {
        var queue = new TransientPlaybackQueue();
        var item = Item("same-item");
        queue.TryAdd(item);
        queue.TryAdd(item);
        var first = queue.Items[0];
        var second = queue.Items[1];
        Assert.NotEqual(first.EntryId, second.EntryId);

        Assert.True(queue.Remove(second.EntryId));

        Assert.Same(first, Assert.Single(queue.Items));
        Assert.False(queue.Remove(second.EntryId));
        Assert.True(queue.TryConsume(first.EntryId, out var played));
        Assert.Same(item, played);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void Moving_items_preserves_identity_and_respects_both_list_boundaries()
    {
        var queue = new TransientPlaybackQueue();
        foreach (var id in new[] { "a", "b", "c" }) queue.TryAdd(Item(id));
        var entries = queue.Items.ToArray();

        Assert.False(queue.MoveUp(entries[0].EntryId));
        Assert.False(queue.MoveDown(entries[2].EntryId));
        Assert.False(queue.MoveDown(Guid.NewGuid()));
        Assert.True(queue.MoveDown(entries[0].EntryId));
        Assert.True(queue.MoveUp(entries[2].EntryId));

        Assert.Equal(new[] { "b", "c", "a" }, queue.Items.Select(entry => entry.Item.Id));
        Assert.Equal(new[] { entries[1], entries[2], entries[0] }, queue.Items);
        Assert.Equal(1, queue.IndexOf(entries[2].EntryId));
    }

    [Fact]
    public void Consumption_cannot_remove_a_different_head_after_reordering()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        queue.TryAdd(Item("b"));
        var first = queue.Next!;
        var second = queue.Items[1];
        Assert.False(queue.TryConsume(second.EntryId, out _));
        Assert.True(queue.MoveUp(second.EntryId));

        Assert.False(queue.TryConsume(first.EntryId, out var stale));
        Assert.Null(stale);
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryConsume(second.EntryId, out var next));
        Assert.Equal("b", next?.Id);
        Assert.True(queue.TryConsume(first.EntryId, out next));
        Assert.Equal("a", next?.Id);
        Assert.False(queue.TryConsume(first.EntryId, out next));
        Assert.Null(next);
        Assert.Null(queue.Next);
    }

    [Fact]
    public void Clearing_the_queue_invalidates_old_entry_actions_even_when_the_same_media_is_added_again()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        var oldEntry = queue.Next!;
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        queue.Clear();
        queue.Clear();
        Assert.Equal(1, changes);
        Assert.Null(queue.Next);
        queue.TryAdd(Item("a"));

        Assert.False(queue.Remove(oldEntry.EntryId));
        Assert.False(queue.MoveUp(oldEntry.EntryId));
        Assert.False(queue.MoveDown(oldEntry.EntryId));
        Assert.False(queue.TryConsume(oldEntry.EntryId, out _));
        Assert.NotEqual(oldEntry.EntryId, Assert.Single(queue.Items).EntryId);
    }

    [Fact]
    public void Stale_or_duplicate_ended_notifications_cannot_consume_another_queued_item()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("next-a"));
        queue.TryAdd(Item("next-b"));
        var owner = new PlaybackNotificationOwner();
        var coordinator = new object();
        var firstPlayback = Guid.NewGuid();
        owner.BeginPlay(coordinator, 1, null);
        owner.Arm(1);
        owner.Capture(coordinator, firstPlayback, firstPlayback, isOpening: true);
        var oldEnded = owner.Capture(coordinator, firstPlayback, null, isEnded: true)!.Value;
        owner.BeginPlay(coordinator, 2, null);

        bool Consume(PlaybackNotificationOwner.Ticket completion)
        {
            if (!owner.TryBeginAdvance(completion) || queue.Next is not { } entry) return false;
            return queue.TryConsume(entry.EntryId, out _);
        }

        Assert.False(Consume(oldEnded));
        Assert.Equal(2, queue.Count);
        owner.Arm(2);
        var currentPlayback = Guid.NewGuid();
        owner.Capture(coordinator, currentPlayback, currentPlayback, isOpening: true);
        var ended = owner.Capture(coordinator, currentPlayback, null, isEnded: true)!.Value;
        Assert.True(Consume(ended));
        Assert.False(Consume(ended));
        Assert.Equal("next-b", Assert.Single(queue.Items).Item.Id);
        owner.BeginStop(coordinator, 3, currentPlayback);
        var idle = owner.Capture(coordinator, currentPlayback, null)!.Value;
        Assert.False(Consume(idle));
        Assert.Single(queue.Items);
    }

    [Fact(Timeout = 15000)]
    public async Task Preparing_a_head_does_not_remove_it_until_details_succeed()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        queue.TryAdd(Item("b"));
        var head = queue.Next!;
        var ready = new TaskCompletionSource<BaseItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparation = queue.TryPrepareAndConsumeAsync(head.EntryId, (_, _) => ready.Task, () => true,
            TestContext.Current.CancellationToken);

        Assert.Same(head, queue.Next);
        Assert.Equal(2, queue.Count);
        var detail = Item("a") with { Name = "Resolved title" };
        ready.SetResult(detail);
        Assert.Same(detail, await preparation);
        Assert.Equal("b", Assert.Single(queue.Items).Item.Id);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_detail_request_keeps_the_head_and_every_queue_edit()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        queue.TryAdd(Item("b"));
        var head = queue.Next!;
        var ready = new TaskCompletionSource<BaseItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = queue.TryPrepareAndConsumeAsync(head.EntryId, (_, _) => ready.Task, () => true,
            TestContext.Current.CancellationToken);
        queue.TryAdd(Item("c"));
        queue.Remove(queue.Items[1].EntryId);

        ready.SetException(new HttpRequestException("The detail request failed."));
        await Assert.ThrowsAsync<HttpRequestException>(() => preparation);

        Assert.Same(head, queue.Next);
        Assert.Equal(new[] { "a", "c" }, queue.Items.Select(entry => entry.Item.Id));
    }

    [Theory(Timeout = 15000)]
    [InlineData("Remove")]
    [InlineData("Reorder")]
    [InlineData("Clear")]
    [InlineData("ClearAndReadd")]
    public async Task Edits_during_detail_preparation_cannot_commit_the_obsolete_head(string edit)
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        queue.TryAdd(Item("b"));
        var head = queue.Next!;
        var ready = new TaskCompletionSource<BaseItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = queue.TryPrepareAndConsumeAsync(head.EntryId, (_, _) => ready.Task, () => true,
            TestContext.Current.CancellationToken);
        switch (edit)
        {
            case "Remove": queue.Remove(head.EntryId); break;
            case "Reorder": queue.MoveDown(head.EntryId); break;
            case "Clear": queue.Clear(); break;
            case "ClearAndReadd": queue.Clear(); queue.TryAdd(Item("a")); break;
        }
        var edited = queue.Items.ToArray();

        ready.SetResult(Item("a"));
        Assert.Null(await preparation);

        Assert.Equal(edited, queue.Items);
        if (edit == "ClearAndReadd") Assert.NotEqual(head.EntryId, queue.Next?.EntryId);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_or_superseded_preparation_cannot_consume_the_next_item(bool cancel)
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        var head = queue.Next!;
        var ownsRequest = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource<BaseItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = queue.TryPrepareAndConsumeAsync(head.EntryId, (_, _) => ready.Task, () => ownsRequest, lifetime.Token);
        if (cancel) lifetime.Cancel();
        else ownsRequest = false;

        ready.SetResult(Item("a"));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        else Assert.Null(await preparation);

        Assert.Same(head, Assert.Single(queue.Items));
    }

    private static BaseItemDto Item(string id) => new() { Id = id, Name = id, Type = "Movie", MediaType = "Video" };
}
