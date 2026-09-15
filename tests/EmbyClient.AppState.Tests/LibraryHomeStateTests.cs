using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryHomeStateTests
{
    [Fact]
    public async Task Initial_home_presents_continue_watching_while_latest_is_pending_without_a_background_banner()
    {
        using var context = await CreateAsync();
        context.Resume = [Episode("continue-a", 1, 40, 100)];
        context.NextUp = [Episode("next-a", 2, 0, 100)];
        context.Latest = [Movie("latest-a", "Latest movie")];
        using var latest = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Latest")
            ? latest.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var model = context.Model;
        var readyShelvesPresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) =>
        {
            if (model.HasItems && model.HomeRows.Any(row => row.Section == HomeSection.ContinueWatching
                && row.Items.Any(item => item.Id == "continue-a"))
                && model.HomeRows.Any(row => row.Section == HomeSection.NextUp
                    && row.Items.Any(item => item.Id == "next-a")))
                readyShelvesPresented.TrySetResult();
        };

        var initialization = model.SetSessionAsync(context.Api, "server-a", new UserDto { Id = "user-a" },
            TestContext.Current.CancellationToken);
        await latest.WaitForRequestAsync();
        await readyShelvesPresented.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var continueRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var continueCard = Assert.Single(continueRow.Items);
        Assert.Equal("continue-a", continueCard.Id);
        Assert.True(continueCard.CanResume);
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        Assert.True(model.IsHome);
        Assert.True(model.IsBusy);
        Assert.True(model.HasItems);
        Assert.False(model.IsInitialLoading);
        Assert.False(model.IsBackgroundLoading);
        Assert.Equal(Visibility.Collapsed, model.BackgroundLoadingVisibility);
        Assert.False(initialization.IsCompleted);

        latest.Return(LibraryTestContext.Array(context.Latest));
        await initialization;

        Assert.Same(continueRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching));
        Assert.Same(continueCard, Assert.Single(continueRow.Items));
        var latestRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        Assert.Equal("latest-a", Assert.Single(latestRow.Items).Id);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(0L, 100L, false)]
    [InlineData(40L, 100L, true)]
    [InlineData(100L, 100L, false)]
    [InlineData(120L, 100L, false)]
    [InlineData(40L, 0L, true)]
    public async Task Next_up_omits_only_the_same_resumable_episode_and_keeps_other_episodes_from_its_series(
        long positionTicks, long runtimeTicks, bool canResume)
    {
        using var context = await CreateAsync();
        context.Resume = [Episode("episode-a", 1, positionTicks, runtimeTicks)];
        context.NextUp = [Episode("episode-a", 1, 0, runtimeTicks), Episode("episode-b", 2, 0, runtimeTicks)];

        await context.Model.ShowHomeAsync(TestContext.Current.CancellationToken);

        var continueRow = Assert.Single(context.Model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var continueCard = Assert.Single(continueRow.Items);
        Assert.Equal("episode-a", continueCard.Id);
        Assert.Equal(canResume, continueCard.CanResume);
        var nextUpRow = Assert.Single(context.Model.HomeRows, row => row.Section == HomeSection.NextUp);
        Assert.Equal(canResume ? new[] { "episode-b" } : new[] { "episode-a", "episode-b" },
            nextUpRow.Items.Select(item => item.Id));
        Assert.All(nextUpRow.Items, item => Assert.Equal("series-a", item.Item.SeriesId));
        Assert.False(context.Model.HasError);
        Assert.False(context.Model.IsBusy);
    }

    private static BaseItemDto Episode(string id, int number, long positionTicks, long runtimeTicks) => new()
    {
        Id = id, Name = $"Episode {number}", Type = "Episode", MediaType = "Video",
        SeriesId = "series-a", SeriesName = "Test Series", SeasonId = "season-a",
        ParentIndexNumber = 1, IndexNumber = number, RunTimeTicks = runtimeTicks,
        UserData = new UserItemDataDto { PlaybackPositionTicks = positionTicks }
    };
}
