using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackItemPreparationTests
{
    [Fact(Timeout = 15000)]
    public async Task A_failed_detail_request_retires_before_restoring_without_admitting_the_old_end()
    {
        var coordinator = new object();
        var owner = new PlaybackNotificationOwner();
        var oldPlayback = Guid.NewGuid();
        owner.BeginPlay(coordinator, 1, null);
        owner.Arm(1);
        owner.Capture(coordinator, oldPlayback, oldPlayback, isOpening: true);
        owner.BeginPlay(coordinator, 2, oldPlayback);
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("The details could not be loaded.");
        PlaybackItemPreparation.Outcome? restored = null;
        var starts = 0;

        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => Task.FromException<BaseItemDto?>(failure),
            (_, _) => { starts++; return Task.CompletedTask; }, () => true,
            () => { retirementStarted.SetResult(); return retirement.Task; },
            outcome => restored = outcome, TestContext.Current.CancellationToken);
        await retirementStarted.Task;

        Assert.Null(restored);
        Assert.Null(owner.Capture(coordinator, oldPlayback, null, isEnded: true));
        retirement.SetResult();
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => operation));
        Assert.Equal(new PlaybackItemPreparation.Outcome(true, false), restored);
        Assert.Equal(0, starts);
        Assert.Null(owner.Capture(coordinator, oldPlayback, null, isEnded: true));
    }

    [Fact(Timeout = 15000)]
    public async Task Editing_the_head_during_preparation_restores_idle_without_consuming_or_starting_an_item()
    {
        var queue = new TransientPlaybackQueue();
        queue.TryAdd(Item("a"));
        queue.TryAdd(Item("b"));
        var head = queue.Next!;
        var details = new TaskCompletionSource<BaseItemDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaybackItemPreparation.Outcome? restored = null;
        var retirements = 0;
        var starts = 0;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            token => queue.TryPrepareAndConsumeAsync(head.EntryId, (_, _) => details.Task, () => true, token),
            (_, _) => { starts++; return Task.CompletedTask; }, () => true,
            () => { retirements++; return Task.CompletedTask; }, outcome => restored = outcome,
            TestContext.Current.CancellationToken);

        queue.MoveDown(head.EntryId);
        details.SetResult(Item("a"));
        await operation;

        Assert.Equal(new[] { "b", "a" }, queue.Items.Select(entry => entry.Item.Id));
        Assert.Equal(0, starts);
        Assert.Equal(1, retirements);
        Assert.Equal(new PlaybackItemPreparation.Outcome(false, false), restored);
    }

    [Fact(Timeout = 15000)]
    public async Task A_superseded_preparation_cannot_stop_or_restore_the_new_request()
    {
        var details = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentIntent = 1;
        var retirements = 0;
        var restorations = 0;
        var starts = 0;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => details.Task, (_, _) => { starts++; return Task.CompletedTask; }, () => currentIntent == 1,
            () => { retirements++; return Task.CompletedTask; }, _ => restorations++,
            TestContext.Current.CancellationToken);

        currentIntent = 2;
        details.SetException(new HttpRequestException("The obsolete details request failed."));
        await Assert.ThrowsAsync<HttpRequestException>(() => operation);

        Assert.Equal(0, starts);
        Assert.Equal(0, retirements);
        Assert.Equal(0, restorations);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelled_details_that_arrive_after_replacement_cannot_start_or_retire_playback()
    {
        var details = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ownsRequest = true;
        var starts = 0;
        var retirements = 0;
        var restorations = 0;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => details.Task, (_, _) => { starts++; return Task.CompletedTask; }, () => ownsRequest,
            () => { retirements++; return Task.CompletedTask; }, _ => restorations++, request.Token);

        ownsRequest = false;
        request.Cancel();
        details.SetResult(Item("a"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(0, starts);
        Assert.Equal(0, retirements);
        Assert.Equal(0, restorations);
    }

    [Fact(Timeout = 15000)]
    public async Task A_new_request_during_retirement_keeps_its_notification_owner_and_presentation()
    {
        var coordinator = new object();
        var owner = new PlaybackNotificationOwner();
        var currentIntent = 1;
        owner.BeginPlay(coordinator, currentIntent, null);
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restorations = 0;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => Task.FromResult<BaseItemDto?>(null), (_, _) => Task.CompletedTask, () => currentIntent == 1,
            () => { retirementStarted.SetResult(); return retirement.Task; },
            _ => { restorations++; owner.Invalidate(1); }, TestContext.Current.CancellationToken);
        await retirementStarted.Task;

        currentIntent = 2;
        owner.BeginPlay(coordinator, currentIntent, null);
        owner.Arm(currentIntent);
        var playbackId = Guid.NewGuid();
        var opening = owner.Capture(coordinator, playbackId, playbackId, isOpening: true)!.Value;
        retirement.SetResult();
        await operation;

        Assert.Equal(0, restorations);
        Assert.True(owner.IsCurrent(opening));
    }

    [Fact(Timeout = 15000)]
    public async Task A_coordinator_failure_after_handoff_keeps_the_coordinator_in_charge_of_recovery()
    {
        var failure = new InvalidOperationException("The coordinator owns this failure.");
        var retirements = 0;
        var restorations = 0;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => Task.FromResult<BaseItemDto?>(Item("a")), (_, _) => Task.FromException(failure), () => true,
            () => { retirements++; return Task.CompletedTask; }, _ => restorations++,
            TestContext.Current.CancellationToken);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
        Assert.Equal(0, retirements);
        Assert.Equal(0, restorations);
    }

    [Fact(Timeout = 15000)]
    public async Task A_cleanup_failure_preserves_the_original_error_and_restores_a_failed_terminal_state()
    {
        var failure = new HttpRequestException("The details could not be loaded.");
        PlaybackItemPreparation.Outcome? restored = null;
        var operation = PlaybackItemPreparation.RunAsync<BaseItemDto>(
            _ => Task.FromException<BaseItemDto?>(failure), (_, _) => Task.CompletedTask, () => true,
            () => Task.FromException(new InvalidOperationException("Retirement failed.")),
            outcome => restored = outcome, TestContext.Current.CancellationToken);

        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => operation));
        Assert.Equal(new PlaybackItemPreparation.Outcome(true, true), restored);
    }

    private static BaseItemDto Item(string id) => new() { Id = id, Name = id, Type = "Movie", MediaType = "Video" };
}
