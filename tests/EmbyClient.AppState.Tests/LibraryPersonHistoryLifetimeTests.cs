using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Reflection;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryPersonHistoryLifetimeTests
{
    [Fact]
    public async Task Evicting_person_history_cancels_only_owners_beyond_the_twenty_four_entry_limit()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        var people = new List<PersonDetailsViewModel>();
        for (var index = 0; index < 30; index++)
        {
            await model.ShowPersonAsync(Person($"person-{index:D2}"), TestContext.Current.CancellationToken);
            people.Add(Assert.IsType<PersonDetailsViewModel>(model.ActivePerson));
        }

        Assert.All(people.Take(5), person => Assert.True(person.LifetimeToken.IsCancellationRequested));
        Assert.All(people.Skip(5), person => Assert.False(person.LifetimeToken.IsCancellationRequested));
        var requestCount = context.Handler.Requests.Length;
        foreach (var retired in people.Take(5)) await retired.LoadAsync();
        for (var index = 28; index >= 5; index--)
        {
            await model.GoBackAsync(TestContext.Current.CancellationToken);
            Assert.Same(people[index], model.ActivePerson);
            Assert.False(people[index].LifetimeToken.IsCancellationRequested);
        }
        Assert.False(model.CanGoBack);
        for (var index = 6; index < people.Count; index++)
        {
            await model.GoForwardAsync(TestContext.Current.CancellationToken);
            Assert.Same(people[index], model.ActivePerson);
            Assert.False(people[index].LifetimeToken.IsCancellationRequested);
        }
        Assert.False(model.CanGoForward);
        Assert.Equal(requestCount, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task A_new_branch_retires_forward_people_and_preserves_people_still_in_back_history()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        await model.ShowPersonAsync(Person("person-a"), TestContext.Current.CancellationToken);
        var first = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        await model.ShowPersonAsync(Person("person-b"), TestContext.Current.CancellationToken);
        var second = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        await model.ShowPersonAsync(Person("person-c"), TestContext.Current.CancellationToken);
        var discarded = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(second, model.ActivePerson);
        Assert.False(discarded.LifetimeToken.IsCancellationRequested);
        await model.ShowPersonAsync(Person("person-branch"), TestContext.Current.CancellationToken);
        var branch = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        Assert.True(discarded.LifetimeToken.IsCancellationRequested);
        Assert.False(first.LifetimeToken.IsCancellationRequested);
        Assert.False(second.LifetimeToken.IsCancellationRequested);
        Assert.False(branch.LifetimeToken.IsCancellationRequested);
        Assert.False(model.CanGoForward);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(second, model.ActivePerson);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, model.ActivePerson);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(second, model.ActivePerson);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(branch, model.ActivePerson);
    }

    [Fact]
    public async Task Reopening_the_same_person_identity_retires_the_replaced_instance()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        var card = Person("person-a");
        await model.ShowPersonAsync(card, TestContext.Current.CancellationToken);
        var replaced = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);

        await model.ShowPersonAsync(card, TestContext.Current.CancellationToken);
        var current = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);

        Assert.NotSame(replaced, current);
        Assert.True(replaced.LifetimeToken.IsCancellationRequested);
        Assert.False(current.LifetimeToken.IsCancellationRequested);
        Assert.NotEmpty(current.Works);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.False(current.LifetimeToken.IsCancellationRequested);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(current, model.ActivePerson);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("library")]
    [InlineData("favorites")]
    [InlineData("movies")]
    [InlineData("series")]
    public async Task Primary_navigation_retires_people_in_current_back_and_forward_state(string destination)
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        await model.ShowPersonAsync(Person("person-a"), TestContext.Current.CancellationToken);
        var first = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        await model.ShowPersonAsync(Person("person-b"), TestContext.Current.CancellationToken);
        var second = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        await model.ShowPersonAsync(Person("person-c"), TestContext.Current.CancellationToken);
        var third = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(second, model.ActivePerson);

        await NavigatePrimaryAsync(model, destination);

        Assert.All(new[] { first, second, third }, person => Assert.True(person.LifetimeToken.IsCancellationRequested));
        Assert.Null(model.ActivePerson);
        Assert.False(model.CanGoBack);
        Assert.False(model.CanGoForward);
        var requestCount = context.Handler.Requests.Length;
        await first.LoadBiographyAsync();
        await second.LoadMoreAsync();
        await third.LoadAsync();
        Assert.Equal(requestCount, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task Precancelled_person_navigation_creates_no_owner_and_preserves_current_history()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        await model.ShowPersonAsync(Person("person-current"), TestContext.Current.CancellationToken);
        var current = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        var owners = OwnedPeople(model);
        var revision = model.NavigationRevision;
        var requestCount = context.Handler.Requests.Length;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            model.ShowPersonAsync(Person("person-rejected"), canceled.Token));

        Assert.Equal(owners.Length, OwnedPeople(model).Length);
        Assert.Same(current, Assert.Single(OwnedPeople(model)));
        Assert.Same(current, model.ActivePerson);
        Assert.False(current.LifetimeToken.IsCancellationRequested);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(requestCount, context.Handler.Requests.Length);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.False(model.CanGoBack);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(current, model.ActivePerson);
        Assert.False(current.LifetimeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_new_person_is_not_retired_during_a_nested_state_capture_navigation()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        var model = context.Model;
        Task? nested = null;
        void Capturing(object? sender, EventArgs args)
        {
            model.BrowseStateCapturing -= Capturing;
            nested = model.ShowHomeAsync(TestContext.Current.CancellationToken);
        }
        model.BrowseStateCapturing += Capturing;

        await model.ShowPersonAsync(Person("person-pending-capture"), TestContext.Current.CancellationToken);
        await Assert.IsAssignableFrom<Task>(nested);

        var person = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        Assert.False(person.LifetimeToken.IsCancellationRequested);
        Assert.NotEmpty(person.Biography);
        Assert.NotEmpty(person.Works);
    }

    [Fact]
    public async Task Pending_person_loads_remain_alive_in_both_directions_of_retained_history()
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        using var pending = new PendingPersonRequests(context, "person-pending");
        var model = context.Model;
        var loading = model.ShowPersonAsync(Person(pending.Id), TestContext.Current.CancellationToken);
        var person = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        var requests = await pending.WaitForRequestsAsync();

        await model.ShowPersonAsync(Person("person-current"), TestContext.Current.CancellationToken);
        Assert.False(person.LifetimeToken.IsCancellationRequested);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(person, model.ActivePerson);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.False(person.LifetimeToken.IsCancellationRequested);
        Assert.All(requests, request => Assert.False(request.CancellationToken.IsCancellationRequested));
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(person, model.ActivePerson);

        pending.ReturnResponses();
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(person.LifetimeToken.IsCancellationRequested);
        Assert.Equal("Late biography", person.Biography);
        Assert.Equal("late-work", Assert.Single(person.Works).Id);
        Assert.Equal(9, person.TotalWorkCount);
        Assert.Equal(7, person.MovieWorkCount);
        Assert.Equal(2, person.SeriesWorkCount);
    }

    [Theory]
    [InlineData("history-cap")]
    [InlineData("forward-branch")]
    [InlineData("primary-reset")]
    public async Task Retiring_a_pending_person_cancels_requests_and_rejects_late_results(string reason)
    {
        using var context = await CreateAsync();
        ConfigurePersonResponses(context);
        using var pending = new PendingPersonRequests(context, "person-pending");
        var model = context.Model;
        var loading = model.ShowPersonAsync(Person(pending.Id), TestContext.Current.CancellationToken);
        var person = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        var requests = await pending.WaitForRequestsAsync();
        var cancellationCount = 0;
        using var cancellation = person.LifetimeToken.Register(() => Interlocked.Increment(ref cancellationCount));

        if (reason == "history-cap")
        {
            for (var index = 0; index < 25; index++)
                await model.ShowPersonAsync(Person($"person-next-{index:D2}"), TestContext.Current.CancellationToken);
        }
        else if (reason == "forward-branch")
        {
            await model.GoBackAsync(TestContext.Current.CancellationToken);
            Assert.False(person.LifetimeToken.IsCancellationRequested);
            await model.ShowPersonAsync(Person("person-branch"), TestContext.Current.CancellationToken);
        }
        else await model.ShowHomeAsync(TestContext.Current.CancellationToken);

        Assert.True(person.LifetimeToken.IsCancellationRequested);
        Assert.Equal(1, cancellationCount);
        Assert.All(requests, request => Assert.True(request.CancellationToken.IsCancellationRequested));
        var current = model.ActivePerson;
        var revision = model.NavigationRevision;
        var changes = new List<string?>();
        person.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        pending.ReturnResponses();
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Empty(person.Biography);
        Assert.Empty(person.Works);
        Assert.Empty(person.BiographyError);
        Assert.Empty(person.WorksError);
        Assert.Null(person.TotalWorkCount);
        Assert.Null(person.MovieWorkCount);
        Assert.Null(person.SeriesWorkCount);
        Assert.Empty(changes);
        Assert.Same(current, model.ActivePerson);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.False(model.HasError);
        var requestCount = context.Handler.Requests.Length;
        await person.LoadAsync();
        Assert.Equal(requestCount, context.Handler.Requests.Length);
    }

    private static Task NavigatePrimaryAsync(LibraryViewModel model, string destination) => destination switch
    {
        "home" => model.ShowHomeAsync(TestContext.Current.CancellationToken),
        "library" => model.ShowLibraryAsync(Assert.Single(model.Libraries), TestContext.Current.CancellationToken),
        "favorites" => model.ShowFavoritesAsync(TestContext.Current.CancellationToken),
        "movies" => model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken),
        "series" => model.ShowMediaTypeAsync("Series", TestContext.Current.CancellationToken),
        _ => throw new ArgumentException("Unknown primary destination.", nameof(destination))
    };

    private static PersonCardViewModel Person(string id) => Assert.Single(PersonCardViewModel.FromItem(new BaseItemDto
    {
        People = [new PersonInfo { Id = id, Name = id, Type = "Actor" }]
    }));

    private static void ConfigurePersonResponses(LibraryTestContext context)
    {
        context.Catalog = [Movie("person-work")];
        context.Handler.RespondAsync = request => Task.FromResult(PersonResponse(context, request));
    }

    private static HttpResponseMessage PersonResponse(LibraryTestContext context, ObservedRequest request)
    {
        var id = Uri.UnescapeDataString(request.Uri.AbsolutePath[(request.Uri.AbsolutePath.LastIndexOf('/') + 1)..]);
        if (id.StartsWith("person-", StringComparison.Ordinal))
            return Item(new BaseItemDto { Id = id, Name = id, Type = "Person", Overview = "Loaded biography" });
        if (IsPersonCountRequest(request))
        {
            var types = request.Query["IncludeItemTypes"].Split(',');
            var items = context.Catalog.Where(item => item.Type is { } type && types.Contains(type, StringComparer.Ordinal)).ToArray();
            return Page(items.Take(1).ToArray(), items.Length);
        }
        return context.DefaultResponse(request);
    }

    private static bool IsPersonCountRequest(ObservedRequest request) => request.IsItems
        && request.Query.ContainsKey("PersonIds") && request.Query.GetValueOrDefault("Limit") == "1"
        && request.Query.GetValueOrDefault("EnableImages") == "false";

    private static PersonDetailsViewModel[] OwnedPeople(LibraryViewModel model) =>
        Assert.IsType<HashSet<PersonDetailsViewModel>>(typeof(LibraryViewModel)
            .GetField("_people", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)).ToArray();

    private sealed class PendingPersonRequests : IDisposable
    {
        private readonly RequestGate _biography = new();
        private readonly RequestGate _works = new();
        private readonly RequestGate _totalCount = new();
        private readonly RequestGate _movieCount = new();
        private readonly RequestGate _seriesCount = new();

        public PendingPersonRequests(LibraryTestContext context, string id)
        {
            Id = id;
            context.Handler.RespondAsync = request => request.Is($"/Users/user-a/Items/{id}")
                ? _biography.RespondAsync(request) : request.IsItems && request.Query.GetValueOrDefault("PersonIds") == id
                ? Gate(request).RespondAsync(request) : Task.FromResult(PersonResponse(context, request));
        }

        public string Id { get; }

        public Task<ObservedRequest[]> WaitForRequestsAsync() => Task.WhenAll(_biography.WaitForRequestAsync(),
            _works.WaitForRequestAsync(), _totalCount.WaitForRequestAsync(), _movieCount.WaitForRequestAsync(),
            _seriesCount.WaitForRequestAsync());

        public void ReturnResponses()
        {
            _biography.Return(Item(new BaseItemDto { Id = Id, Name = Id, Type = "Person", Overview = "Late biography" }));
            _works.Return(Page([Movie("late-work")], 9));
            _totalCount.Return(Page([], 9));
            _movieCount.Return(Page([], 7));
            _seriesCount.Return(Page([], 2));
        }

        private RequestGate Gate(ObservedRequest request) => !IsPersonCountRequest(request) ? _works
            : request.Query["IncludeItemTypes"] switch { "Movie" => _movieCount, "Series" => _seriesCount, _ => _totalCount };

        public void Dispose()
        {
            _biography.Dispose();
            _works.Dispose();
            _totalCount.Dispose();
            _movieCount.Dispose();
            _seriesCount.Dispose();
        }
    }
}
