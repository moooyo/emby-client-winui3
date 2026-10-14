using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryEventIntegrationTests
{
    [Fact]
    public void Repeated_events_coalesce_refresh_and_only_unnotified_additions_are_retained()
    {
        var accumulator = new LibraryEventAccumulator();
        accumulator.Add(["episode-a", "episode-a", "episode-b"], hasChanges: true);
        Assert.Equal(["episode-a", "episode-b"], accumulator.TakeAdditions().Order(StringComparer.Ordinal));
        Assert.True(accumulator.NeedsRefresh);
        Assert.True(accumulator.MarkNotified("episode-a"));
        Assert.False(accumulator.MarkNotified("episode-a"));
        accumulator.MarkRefreshed();
        accumulator.Add(["episode-a", "episode-c"], hasChanges: true);
        Assert.Equal("episode-c", Assert.Single(accumulator.TakeAdditions()));
        Assert.True(accumulator.NeedsRefresh);
    }

    [Fact]
    public void Refresh_completion_does_not_discard_an_event_received_during_the_request()
    {
        var accumulator = new LibraryEventAccumulator();
        accumulator.Add(["episode-a"], hasChanges: true);
        var requestedRevision = accumulator.Revision;
        accumulator.Add(["episode-b"], hasChanges: true);
        accumulator.MarkRefreshed(requestedRevision);
        Assert.True(accumulator.NeedsRefresh);
        accumulator.MarkRefreshed(accumulator.Revision);
        Assert.False(accumulator.NeedsRefresh);
        Assert.Equal(2, accumulator.TakeAdditions().Length);
    }

    [Fact]
    public void Account_boundary_clears_all_pending_and_notified_identifiers()
    {
        var accumulator = new LibraryEventAccumulator();
        accumulator.MarkNotified("episode-a");
        accumulator.Add(["episode-b"], hasChanges: true);
        accumulator.Clear();
        Assert.False(accumulator.NeedsRefresh);
        Assert.False(accumulator.HasPendingAdditions);
        Assert.Empty(accumulator.TakeAdditions());
        Assert.True(accumulator.MarkNotified("episode-a"));
    }

    [Fact]
    public void Large_library_events_keep_refresh_pending_with_bounded_notification_work()
    {
        var accumulator = new LibraryEventAccumulator();
        accumulator.Add(Enumerable.Range(0, 1_000).Select(index => $"episode-{index}"), hasChanges: true);
        Assert.Equal(64, accumulator.TakeAdditions().Length);
        Assert.True(accumulator.NeedsRefresh);
    }

    [Fact]
    public void Notification_activation_rejects_paths_and_never_crosses_server_or_user_scope()
    {
        var arguments = new Dictionary<string, string>
        {
            ["action"] = EpisodeNotificationTarget.Action,
            ["serverId"] = "server-a", ["userId"] = "user-a", ["itemId"] = "episode-a"
        };
        Assert.True(EpisodeNotificationTarget.TryParse(arguments, out var target));
        Assert.True(target!.Matches("server-a", "user-a"));
        Assert.False(target.Matches("server-b", "user-a"));
        Assert.False(target.Matches("server-a", "user-b"));
        arguments["itemId"] = "../../other.exe";
        Assert.False(EpisodeNotificationTarget.TryParse(arguments, out _));
        arguments["itemId"] = "https://other.invalid/episode";
        Assert.False(EpisodeNotificationTarget.TryParse(arguments, out _));
    }
}
