using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryBrowseTests
{
    [Fact]
    public async Task The_first_search_can_commit_its_own_results_while_search_is_pending()
    {
        using var context = await CreateAsync();
        var model = context.Model;
        using var results = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? results.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var search = model.SearchAsync("  Arrival  ", debounce: false, TestContext.Current.CancellationToken);
        var request = await results.WaitForRequestAsync();
        Assert.Equal("Arrival", request.Query["SearchTerm"]);
        Assert.True(model.IsSearch);
        Assert.True(model.HasPendingSearch);
        Assert.True(model.IsInitialLoading);
        Assert.Empty(model.Items);
        results.Return(Page([Movie("arrival", "Arrival")]));
        await search;

        Assert.Equal("arrival", Assert.Single(model.Items).Id);
        Assert.Equal("Arrival", model.SearchText);
        Assert.True(model.HasItems);
        Assert.False(model.HasPendingSearch);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_failed_filter_restores_the_previous_options_and_loads_more_from_the_previous_query()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(72));
        var model = context.Model;
        await model.SetBrowseOptionsAsync("DateCreated", true, WatchStatusFilter.Played, TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        using var filter = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? filter.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var pending = model.SetBrowseOptionsAsync("ProductionYear", false, WatchStatusFilter.Unplayed,
            TestContext.Current.CancellationToken);
        var filterRequest = await filter.WaitForRequestAsync();
        Assert.Equal("ProductionYear,SortName", filterRequest.Query["SortBy"]);
        Assert.Equal("false", filterRequest.Query["IsPlayed"]);
        Assert.Equal("ProductionYear", model.BrowseSortKey);
        Assert.Equal(WatchStatusFilter.Unplayed, model.BrowseWatchFilter);
        AssertSameCards(original, model.Items);
        Assert.True(model.IsBackgroundLoading);
        filter.Return(Failure());
        await pending;

        AssertSameCards(original, model.Items);
        Assert.Equal("DateCreated", model.BrowseSortKey);
        Assert.True(model.BrowseSortDescending);
        Assert.Equal(WatchStatusFilter.Played, model.BrowseWatchFilter);
        Assert.True(model.HasError);
        Assert.True(model.CanLoadMore);

        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var moreRequest = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("48", moreRequest.Query["StartIndex"]);
        Assert.Equal("DateCreated,SortName", moreRequest.Query["SortBy"]);
        Assert.Equal("Descending", moreRequest.Query["SortOrder"]);
        Assert.Equal("true", moreRequest.Query["IsPlayed"]);
        Assert.Equal(72, model.Items.Count);
    }

    [Fact]
    public async Task An_empty_watch_filter_keeps_its_controls_and_clearing_it_preserves_the_sort()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync([Movie("available-a")]);
        var model = context.Model;
        context.Catalog = [];
        await model.SetBrowseOptionsAsync("ProductionYear", true, WatchStatusFilter.Unplayed,
            TestContext.Current.CancellationToken);

        Assert.Empty(model.Items);
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        Assert.Equal(Visibility.Visible, model.FilterEmptyVisibility);
        Assert.Equal(Visibility.Visible, model.BrowseOptionsVisibility);
        Assert.Contains("Clear the filter", model.EmptyMessage);
        Assert.False(model.HasMore);

        context.Catalog = [Movie("available-a")];
        var requestStart = context.Handler.Requests.Length;
        await model.ClearBrowseFilterAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.False(request.Query.ContainsKey("IsPlayed"));
        Assert.Equal("ProductionYear,SortName", request.Query["SortBy"]);
        Assert.Equal("Descending", request.Query["SortOrder"]);
        Assert.Equal(WatchStatusFilter.All, model.BrowseWatchFilter);
        Assert.Equal("available-a", Assert.Single(model.Items).Id);
        Assert.Equal(Visibility.Collapsed, model.FilterEmptyVisibility);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recently_added_starts_newest_first_and_respects_subsequent_sort_and_filter_choices(bool libraryShelf)
    {
        using var context = await CreateAsync();
        context.Catalog = [Movie("recent-a")];
        var model = context.Model;
        var requestStart = context.Handler.Requests.Length;
        if (libraryShelf)
            await model.ShowShelfAsync(new MediaShelfViewModel("Recently added in Movies", false,
                HomeSection.RecentlyAdded, "library-a"), TestContext.Current.CancellationToken);
        else
            await model.ShowHomeSectionAsync(HomeSection.RecentlyAdded, TestContext.Current.CancellationToken);

        var initial = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("DateCreated,SortName", initial.Query["SortBy"]);
        Assert.Equal("Descending", initial.Query["SortOrder"]);
        Assert.Equal("true", initial.Query["Recursive"]);
        Assert.Equal(libraryShelf ? "library-a" : null, initial.Query.GetValueOrDefault("ParentId"));
        Assert.Equal("DateCreated", model.BrowseSortKey);
        Assert.True(model.BrowseSortDescending);

        requestStart = context.Handler.Requests.Length;
        await model.SetBrowseOptionsAsync("ProductionYear", false, WatchStatusFilter.Played,
            TestContext.Current.CancellationToken);

        var changed = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("ProductionYear,SortName", changed.Query["SortBy"]);
        Assert.Equal("Ascending", changed.Query["SortOrder"]);
        Assert.Equal("true", changed.Query["IsPlayed"]);
        Assert.Equal(libraryShelf ? "library-a" : null, changed.Query.GetValueOrDefault("ParentId"));
        Assert.Equal("ProductionYear", model.BrowseSortKey);
        Assert.False(model.BrowseSortDescending);
        Assert.Equal(WatchStatusFilter.Played, model.BrowseWatchFilter);
        Assert.False(model.HasError);
    }
}
