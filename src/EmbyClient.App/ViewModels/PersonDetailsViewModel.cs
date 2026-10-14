using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public sealed partial class PersonDetailsViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 24;
    private readonly EmbyApiClient _api;
    private readonly ImageCache _images;
    private readonly string _serverId;
    private readonly string _userId;
    private readonly Action<Exception> _reportSessionFailure;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private readonly HashSet<string> _completedCountQueries = new(StringComparer.Ordinal);
    private CancellationTokenSource? _worksCancellation;
    private Task? _initialLoad;
    private Task? _worksPageLoad;
    private Task? _workCountsLoad;
    private string _selectedWorkType = "all";
    private string _selectedWorkSort = "ProductionYear";
    private int _nextIndex;
    private int _worksRequestVersion;
    private bool _disposed;

    internal PersonDetailsViewModel(PersonCardViewModel person, string sourceTitle, EmbyApiClient api,
        ImageCache images, string serverId, string userId, CancellationToken sessionToken, Action<Exception> reportSessionFailure)
    {
        Person = person;
        SourceLabel = $"From {sourceTitle}";
        _api = api;
        _images = images;
        _serverId = serverId;
        _userId = userId;
        _reportSessionFailure = reportSessionFailure;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _lifetimeToken = _lifetime.Token;
        Portrait = person.Media;
    }

    public PersonCardViewModel Person { get; }
    public string SourceLabel { get; }
    public ObservableCollection<MediaCardViewModel> Works { get; } = [];
    public double ScrollOffset { get; set; }
    public string? FocusedWorkId { get; set; }
    public bool IsBiographyExpanded { get; set; }
    public string SelectedWorkType => _selectedWorkType;
    public string SelectedWorkSort => _selectedWorkSort;
    public int? FilteredWorkCount => GetWorkCount(SelectedWorkType);
    // Legacy views use WorkCount for loaded cards, not the server's library total.
    public int WorkCount => Works.Count;
    public int LoadedWorkCount => Works.Count;
    internal CancellationToken LifetimeToken => _lifetimeToken;

    [ObservableProperty]
    public partial MediaCardViewModel Portrait { get; set; }

    [ObservableProperty]
    public partial string Biography { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BiographyErrorVisibility))]
    public partial string BiographyError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorksErrorVisibility), nameof(EmptyWorksVisibility), nameof(CanLoadMore))]
    public partial string WorksError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BiographyLoadingVisibility))]
    public partial bool IsLoadingBiography { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorksLoadingVisibility), nameof(EmptyWorksVisibility), nameof(CanLoadMore))]
    public partial bool IsLoadingWorks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMoreVisibility), nameof(CanLoadMore))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyWorksVisibility))]
    public partial bool HasLoadedWorks { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredWorkCount))]
    public partial int? TotalWorkCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredWorkCount))]
    public partial int? MovieWorkCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredWorkCount))]
    public partial int? SeriesWorkCount { get; set; }

    public Visibility BiographyErrorVisibility => BiographyError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorksErrorVisibility => WorksError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BiographyLoadingVisibility => IsLoadingBiography ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorksLoadingVisibility => IsLoadingWorks ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyWorksVisibility => HasLoadedWorks && !IsLoadingWorks && Works.Count == 0 && WorksError.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadMoreVisibility => HasMore ? Visibility.Visible : Visibility.Collapsed;
    public bool CanLoadMore => HasMore && !IsLoadingWorks && WorksError.Length == 0;

    public Task LoadAsync() => EnsureLoadedAsync();

    public Task EnsureLoadedAsync()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return Task.CompletedTask;
        return _initialLoad ??= Task.WhenAll(LoadBiographyAsync(), LoadMoreAsync());
    }

    public Task SetWorksOptionsAsync(string type, string sort)
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return Task.CompletedTask;
        type = type switch
        {
            "Movie" => "Movie",
            "Series" => "Series",
            _ => "all"
        };
        sort = sort switch
        {
            "CommunityRating" => "CommunityRating",
            "SortName" => "SortName",
            _ => "ProductionYear"
        };
        if (type == SelectedWorkType && sort == SelectedWorkSort)
        {
            OnPropertyChanged(nameof(SelectedWorkType));
            OnPropertyChanged(nameof(SelectedWorkSort));
            return IsLoadingWorks ? _worksPageLoad ?? Task.CompletedTask : Task.CompletedTask;
        }

        _worksRequestVersion++;
        var previous = _worksCancellation;
        _worksCancellation = null;
        previous?.Cancel();
        _worksPageLoad = null;
        HasLoadedWorks = false;
        HasMore = false;
        IsLoadingWorks = false;
        WorksError = string.Empty;
        _nextIndex = 0;
        ScrollOffset = 0;
        FocusedWorkId = null;
        Works.Clear();
        SetProperty(ref _selectedWorkType, type, nameof(SelectedWorkType));
        SetProperty(ref _selectedWorkSort, sort, nameof(SelectedWorkSort));
        OnPropertyChanged(nameof(FilteredWorkCount));
        OnPropertyChanged(nameof(WorkCount));
        OnPropertyChanged(nameof(LoadedWorkCount));
        return LoadMoreAsync();
    }

    public async Task LoadBiographyAsync()
    {
        if (_disposed || IsLoadingBiography || _lifetimeToken.IsCancellationRequested) return;
        IsLoadingBiography = true;
        BiographyError = string.Empty;
        var token = _lifetimeToken;
        try
        {
            var item = await _api.GetItemAsync(Person.Id, token);
            token.ThrowIfCancellationRequested();
            Biography = string.IsNullOrWhiteSpace(item.Overview)
                ? "No biography has been provided by the server." : item.Overview;
            Portrait = new(item with
            {
                Id = Person.Id,
                Name = Person.Name,
                Type = "Person",
                ImageTags = item.ImageTags ?? Person.Media.Item.ImageTags
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!token.IsCancellationRequested)
            {
                BiographyError = ErrorLabel(exception, "person information");
                _reportSessionFailure(exception);
            }
        }
        finally { if (!_disposed) IsLoadingBiography = false; }
    }

    public Task LoadMoreAsync()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return Task.CompletedTask;
        Task pageLoad;
        if (IsLoadingWorks)
            pageLoad = _worksPageLoad ?? Task.CompletedTask;
        else if (!HasLoadedWorks || HasMore || WorksError.Length > 0)
            pageLoad = _worksPageLoad = LoadWorksPageAsync();
        else
            pageLoad = Task.CompletedTask;
        return Task.WhenAll(pageLoad, EnsureWorkCountsAsync());
    }

    private async Task LoadWorksPageAsync()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        var version = ++_worksRequestVersion;
        _worksCancellation = source;
        var token = source.Token;
        var type = SelectedWorkType;
        var sort = SelectedWorkSort;
        var startIndex = _nextIndex;
        IsLoadingWorks = true;
        WorksError = string.Empty;
        try
        {
            var (sortBy, sortOrder) = GetWorkSort(sort);
            var result = await _api.GetItemsAsync(new ItemQuery
            {
                PersonIds = [Person.Id],
                Recursive = true,
                IncludeItemTypes = GetWorkTypes(type),
                StartIndex = startIndex,
                Limit = PageSize,
                SortBy = sortBy,
                SortOrder = sortOrder,
                Fields = ["PrimaryImageAspectRatio", "People"],
                EnableImageTypes = ["Primary"],
                ImageTypeLimit = 1
            }, token);
            if (!CanCommitWorks(version, token)) return;
            var ids = Works.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var item in result.Items)
            {
                if (!CanCommitWorks(version, token)) return;
                if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id)) continue;
                Works.Add(new(item, GetWorkRole(item)));
                added++;
            }
            if (!CanCommitWorks(version, token)) return;
            SetKnownWorkCount(type, result.TotalRecordCount);
            HasLoadedWorks = true;
            OnPropertyChanged(nameof(WorkCount));
            OnPropertyChanged(nameof(LoadedWorkCount));
            if (result.Items.Length > 0 && added == 0)
            {
                HasMore = false;
                WorksError = "The server did not return any new library works. Try again.";
                return;
            }
            _nextIndex = startIndex + result.Items.Length;
            HasMore = result.Items.Length > 0
                && (result.TotalRecordCount is { } total && total >= 0 ? _nextIndex < total : true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitWorks(version, token))
            {
                WorksError = ErrorLabel(exception, "library works");
                _reportSessionFailure(exception);
            }
        }
        finally
        {
            if (!_disposed && version == _worksRequestVersion)
            {
                _worksCancellation = null;
                IsLoadingWorks = false;
            }
        }
    }

    private Task EnsureWorkCountsAsync()
    {
        if (_disposed || _lifetimeToken.IsCancellationRequested) return Task.CompletedTask;
        if (_workCountsLoad is { IsCompleted: false }) return _workCountsLoad;
        string[] types = ["all", "Movie", "Series"];
        var missing = types.Where(type => GetWorkCount(type) is null && !_completedCountQueries.Contains(type)).ToArray();
        if (missing.Length == 0) return Task.CompletedTask;
        return _workCountsLoad = Task.WhenAll(missing.Select(LoadWorkCountAsync));
    }

    private async Task LoadWorkCountAsync(string type)
    {
        var token = _lifetimeToken;
        try
        {
            var result = await _api.GetItemsAsync(new ItemQuery
            {
                PersonIds = [Person.Id],
                Recursive = true,
                IncludeItemTypes = GetWorkTypes(type),
                StartIndex = 0,
                Limit = 1,
                EnableImages = false,
                EnableUserData = false
            }, token);
            if (_disposed || token.IsCancellationRequested) return;
            _completedCountQueries.Add(type);
            if (GetWorkCount(type) is null) SetKnownWorkCount(type, result.TotalRecordCount);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!_disposed && !token.IsCancellationRequested) _reportSessionFailure(exception);
        }
    }

    private bool CanCommitWorks(int version, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && version == _worksRequestVersion;

    private int? GetWorkCount(string type) => type switch
    {
        "Movie" => MovieWorkCount,
        "Series" => SeriesWorkCount,
        _ => TotalWorkCount
    };

    private void SetKnownWorkCount(string type, int? count)
    {
        if (count is not { } total || total < 0) return;
        switch (type)
        {
            case "Movie": MovieWorkCount = total; break;
            case "Series": SeriesWorkCount = total; break;
            default: TotalWorkCount = total; break;
        }
    }

    private static string[] GetWorkTypes(string type) => type == "all" ? ["Movie", "Series"] : [type];

    private static (string[] Fields, string[] Orders) GetWorkSort(string sort) => sort switch
    {
        "SortName" => (["SortName"], ["Ascending"]),
        "CommunityRating" => (["CommunityRating", "SortName"], ["Descending", "Ascending"]),
        _ => (["ProductionYear", "SortName"], ["Descending", "Ascending"])
    };

    private string GetWorkRole(BaseItemDto item) => string.Join(" · ", (item.People ?? [])
        .Where(person => string.Equals(person.Id, Person.Id, StringComparison.Ordinal))
        .Select(person => !string.IsNullOrWhiteSpace(person.Role) ? person.Role : person.Type ?? string.Empty)
        .Where(role => role.Length > 0)
        .Distinct(StringComparer.Ordinal));

    internal async Task<byte[]?> LoadArtworkAsync(MediaCardViewModel item, int width, int height, CancellationToken cancellationToken)
    {
        if (_disposed) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
        try
        {
            return await _images.GetAsync(_api, _serverId, _userId, item.Item, width, height, linked.Token);
        }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
        {
            if (!linked.IsCancellationRequested) _reportSessionFailure(exception);
            return null;
        }
    }

    private static bool IsExpectedFailure(Exception exception) => exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException;
    private static string ErrorLabel(Exception exception, string content) => exception switch
    {
        EmbyApiException { IsAuthenticationFailure: true } => "Your session has expired. Connect again to view this content.",
        EmbyApiException { IsPermissionDenied: true } => "This account is not allowed to access this content.",
        EmbyApiException { StatusCode: System.Net.HttpStatusCode.NotFound } => "This information is no longer available on the server.",
        _ => $"Unable to load {content}. Try again."
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _worksRequestVersion++;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
