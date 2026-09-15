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
    private Task? _initialLoad;
    private int _nextIndex;
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
    internal CancellationToken LifetimeToken => _lifetimeToken;

    [ObservableProperty]
    public partial MediaCardViewModel Portrait { get; set; }

    [ObservableProperty]
    public partial string Biography { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BiographyErrorVisibility))]
    public partial string BiographyError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorksErrorVisibility), nameof(EmptyWorksVisibility))]
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
    public partial int WorkCount { get; set; }

    public Visibility BiographyErrorVisibility => BiographyError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorksErrorVisibility => WorksError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BiographyLoadingVisibility => IsLoadingBiography ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorksLoadingVisibility => IsLoadingWorks ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyWorksVisibility => !IsLoadingWorks && WorkCount == 0 && WorksError.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadMoreVisibility => HasMore ? Visibility.Visible : Visibility.Collapsed;
    public bool CanLoadMore => HasMore && !IsLoadingWorks;

    public Task LoadAsync() => EnsureLoadedAsync();

    public Task EnsureLoadedAsync() => _initialLoad ??= Task.WhenAll(LoadBiographyAsync(), LoadMoreAsync());

    public async Task LoadBiographyAsync()
    {
        if (_disposed || IsLoadingBiography || _lifetime.IsCancellationRequested) return;
        IsLoadingBiography = true;
        BiographyError = string.Empty;
        var token = _lifetime.Token;
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

    public async Task LoadMoreAsync()
    {
        if (_disposed || IsLoadingWorks || _lifetime.IsCancellationRequested || (_nextIndex > 0 && !HasMore)) return;
        IsLoadingWorks = true;
        WorksError = string.Empty;
        var token = _lifetime.Token;
        try
        {
            var result = await _api.GetItemsAsync(new ItemQuery
            {
                PersonIds = [Person.Id],
                Recursive = true,
                IncludeItemTypes = ["Movie", "Series", "Video", "MusicVideo"],
                StartIndex = _nextIndex,
                Limit = PageSize,
                SortBy = ["ProductionYear", "SortName"],
                SortOrder = ["Descending", "Ascending"],
                Fields = ["PrimaryImageAspectRatio"],
                EnableImageTypes = ["Primary"],
                ImageTypeLimit = 1
            }, token);
            token.ThrowIfCancellationRequested();
            var ids = Works.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in result.Items)
                if (!string.IsNullOrWhiteSpace(item.Id) && ids.Add(item.Id)) Works.Add(new(item));
            _nextIndex += result.Items.Length;
            WorkCount = Works.Count;
            HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? _nextIndex < total : result.Items.Length == PageSize);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!token.IsCancellationRequested)
            {
                WorksError = ErrorLabel(exception, "library works");
                _reportSessionFailure(exception);
            }
        }
        finally { if (!_disposed) IsLoadingWorks = false; }
    }

    internal async Task<byte[]?> LoadArtworkAsync(MediaCardViewModel item, int width, int height, CancellationToken cancellationToken)
    {
        if (_disposed) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
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
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
