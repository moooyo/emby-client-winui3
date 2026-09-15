using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryPageLoadStateTests
{
    [Fact]
    public async Task Dismissing_an_initial_failure_shows_retry_until_a_refresh_confirms_the_list_is_empty()
    {
        using var context = await CreateAsync();
        var model = context.Model;
        using var initial = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? initial.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var navigation = model.ShowLibraryAsync(Assert.Single(model.Libraries), TestContext.Current.CancellationToken);
        await initial.WaitForRequestAsync();
        Assert.Equal(PageLoadOutcome.Loading, model.LoadOutcome);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);
        initial.Return(Failure());
        await navigation;

        Assert.True(model.HasError);
        Assert.Equal(PageLoadOutcome.Failed, model.LoadOutcome);
        Assert.Equal(Visibility.Collapsed, model.InitialFailureVisibility);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);

        model.HasError = false;

        Assert.Equal(PageLoadOutcome.Failed, model.LoadOutcome);
        Assert.Equal(Visibility.Visible, model.InitialFailureVisibility);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);

        using var retry = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? retry.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await retry.WaitForRequestAsync();
        Assert.Equal(Visibility.Collapsed, model.InitialFailureVisibility);
        Assert.Equal(Visibility.Collapsed, model.EmptyVisibility);
        retry.Return(Page([]));
        await refresh;

        Assert.Empty(model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.Equal(Visibility.Collapsed, model.InitialFailureVisibility);
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(48, 24, 72, true, PageLoadMoreOutcome.Appended)]
    [InlineData(48, 0, 48, false, PageLoadMoreOutcome.Exhausted)]
    [InlineData(0, 48, 48, true, PageLoadMoreOutcome.Repeated)]
    public async Task Load_more_distinguishes_new_cards_empty_pages_and_repeated_pages(
        int responseStart, int responseCount, int expectedCount, bool expectedHasMore, PageLoadMoreOutcome expectedOutcome)
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(144));
        var model = context.Model;
        var original = model.Items.ToArray();
        var requestStart = context.Handler.Requests.Length;
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems
            ? Page(context.Catalog.Skip(responseStart).Take(responseCount).ToArray(), 144)
            : context.DefaultResponse(request));

        var outcome = await model.LoadMoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal("48", Assert.Single(context.Handler.Requests.Skip(requestStart),
            request => request.IsItems).Query["StartIndex"]);
        Assert.Equal(context.Catalog.Take(expectedCount).Select(item => item.Id), model.Items.Select(card => card.Id));
        AssertSameCards(original, model.Items.Take(original.Length).ToArray());
        Assert.Equal(expectedHasMore, model.HasMore);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.HasError);
        Assert.False(model.IsLoadingMore);
    }

    [Theory]
    [InlineData(PageLoadMoreOutcome.Failed)]
    [InlineData(PageLoadMoreOutcome.Canceled)]
    public async Task An_interrupted_page_ignores_duplicate_loads_and_retries_without_losing_cards_or_its_offset(
        PageLoadMoreOutcome expectedOutcome)
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(72));
        var model = context.Model;
        var original = model.Items.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var pending = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? pending.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var loadMore = model.LoadMoreAsync(cancellation.Token);
        var request = await pending.WaitForRequestAsync();
        Assert.Equal("48", request.Query["StartIndex"]);
        Assert.True(model.IsLoadingMore);
        var pendingRequestCount = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.NotStarted, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal(pendingRequestCount, context.Handler.Requests.Length);
        Assert.True(model.IsLoadingMore);
        AssertSameCards(original, model.Items);

        if (expectedOutcome == PageLoadMoreOutcome.Canceled)
        {
            cancellation.Cancel();
            Assert.True(request.CancellationToken.IsCancellationRequested);
            pending.Return(Page([Movie("discarded-a", "Discarded response")], 72));
        }
        else pending.Return(Failure());

        Assert.Equal(expectedOutcome, await loadMore);
        AssertSameCards(original, model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.Equal(expectedOutcome == PageLoadMoreOutcome.Failed, model.HasError);
        Assert.True(model.HasMore);
        Assert.True(model.CanLoadMore);
        Assert.False(model.IsBusy);

        context.Handler.RespondAsync = observed => Task.FromResult(context.DefaultResponse(observed));
        var retryStart = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal("48", Assert.Single(context.Handler.Requests.Skip(retryStart),
            observed => observed.IsItems).Query["StartIndex"]);
        Assert.Equal(context.Catalog.Select(item => item.Id), model.Items.Select(card => card.Id));
        AssertSameCards(original, model.Items.Take(original.Length).ToArray());
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }
}
