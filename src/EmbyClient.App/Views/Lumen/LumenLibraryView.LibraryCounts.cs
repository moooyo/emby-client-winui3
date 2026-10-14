using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Dictionary<string, int?> _libraryCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<int?>> _libraryCountTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, string? LibraryId), int?> _mediaLibraryCounts = [];
    private readonly Dictionary<(string Type, string? LibraryId), Task<int?>> _mediaLibraryCountTasks = [];
    private readonly SemaphoreSlim _libraryCountSlots = new(3, 3);
    private CancellationTokenSource? _libraryCountLifetime;
    private int _libraryCountRevision;
    private string _wallEpisodeCountContext = string.Empty;
    private int? _wallEpisodeCount;

    private void ResetLibraryCounts(bool start)
    {
        _libraryCountRevision++;
        _libraryCountLifetime?.Cancel();
        _libraryCountLifetime?.Dispose();
        _libraryCountLifetime = start ? new CancellationTokenSource() : null;
        _libraryCounts.Clear();
        _libraryCountTasks.Clear();
        _mediaLibraryCounts.Clear();
        _mediaLibraryCountTasks.Clear();
        _wallEpisodeCountContext = string.Empty;
        _wallEpisodeCount = null;
        _homeSignature = string.Empty;
    }

    private void RenderWallCount()
    {
        var count = (ViewModel.TotalItemsCount ?? ViewModel.Items.Count).ToString("N0", CultureInfo.CurrentCulture);
        if (ViewModel.TotalItemsCount is null && ViewModel.HasMore) count += "+";
        _wallCount.Text = LumenText.Get(ViewModel.IsFavorites ? "{0} favorite items" : "{0} media titles", count);
        var isSeries = ViewModel.BrowseMediaType == "Series";
        if (!isSeries || ViewModel.IsFavorites || ViewModel.BrowseGenre is not null || ViewModel.BrowseWatchFilter != WatchStatusFilter.All
            || ViewModel.BrowseQualityFilter is not null || ViewModel.BrowseNameStartsWith is not null) return;
        var context = $"{_sessionGeneration}:{ViewModel.SelectedLibraryId ?? "series"}";
        if (_wallEpisodeCountContext != context)
        {
            _wallEpisodeCountContext = context;
            _wallEpisodeCount = null;
            if (_session is { } session && _libraryCountLifetime is { } lifetime && !lifetime.IsCancellationRequested)
                _ = QueryWallEpisodeCountAsync(session, ViewModel.SelectedLibraryId, context, _sessionGeneration, _libraryCountRevision, lifetime.Token);
        }
        if (_wallEpisodeCount is { } episodes)
            _wallCount.Text += " \u00b7 " + LumenText.Get("{0} episodes", episodes.ToString("N0", CultureInfo.CurrentCulture));
    }

    private async Task QueryWallEpisodeCountAsync(ConnectedSession session, string? libraryId, string context,
        int generation, int revision, CancellationToken cancellationToken)
    {
        try
        {
            var result = await session.Api.GetItemsAsync(new ItemQuery
            {
                ParentId = libraryId, IncludeItemTypes = ["Episode"], Recursive = true, Limit = 1,
                EnableImages = false, EnableUserData = false
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || generation != _sessionGeneration || revision != _libraryCountRevision
                || context != _wallEpisodeCountContext) return;
            _wallEpisodeCount = result.TotalRecordCount is >= 0 ? result.TotalRecordCount : result.Items.Length == 0 ? 0 : null;
            QueueRender();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
        {
            if (generation == _sessionGeneration && !cancellationToken.IsCancellationRequested) SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException)
        {
            // The title count remains useful when the episode aggregate is unavailable.
        }
    }

    private TextBlock MediaLibraryCountLabel(string type, string? libraryId)
    {
        var label = LumenUi.Text(string.Empty, 12);
        label.Foreground = LumenTheme.Brush("Muted");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap;
        if (_mediaLibraryCounts.TryGetValue((type, libraryId), out var provided) && provided is { } known)
            label.Text = known.ToString("N0", CultureInfo.CurrentCulture);
        CancellationTokenSource? displayLifetime = null;
        label.Loaded += (_, _) =>
        {
            displayLifetime?.Cancel();
            displayLifetime?.Dispose();
            displayLifetime = new CancellationTokenSource();
            var token = displayLifetime.Token;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                if (!token.IsCancellationRequested && label.IsLoaded)
                    await ShowMediaLibraryCountAsync(label, type, libraryId, token);
            });
        };
        label.Unloaded += (_, _) => { displayLifetime?.Cancel(); displayLifetime?.Dispose(); displayLifetime = null; };
        return label;
    }

    private async Task ShowMediaLibraryCountAsync(TextBlock label, string type, string? libraryId, CancellationToken displayToken)
    {
        var session = _session;
        var generation = _sessionGeneration;
        var revision = _libraryCountRevision;
        var key = (type, libraryId);
        if (session is null || _libraryCountLifetime is not { } lifetime || lifetime.IsCancellationRequested) return;
        try
        {
            if (!_mediaLibraryCountTasks.TryGetValue(key, out var task))
            {
                task = QueryMediaLibraryCountAsync(session, type, libraryId, generation, revision, lifetime.Token);
                _mediaLibraryCountTasks[key] = task;
            }
            var count = await task.WaitAsync(displayToken);
            if (!displayToken.IsCancellationRequested && label.IsLoaded && generation == _sessionGeneration
                && revision == _libraryCountRevision && count is { } total)
                label.Text = total.ToString("N0", CultureInfo.CurrentCulture);
        }
        catch (OperationCanceledException) when (displayToken.IsCancellationRequested || generation != _sessionGeneration
            || revision != _libraryCountRevision) { }
    }

    private async Task<int?> QueryMediaLibraryCountAsync(ConnectedSession session, string type, string? libraryId,
        int generation, int revision, CancellationToken cancellationToken)
    {
        int? count = null;
        try
        {
            await _libraryCountSlots.WaitAsync(cancellationToken);
            try
            {
                var result = await session.Api.GetItemsAsync(new ItemQuery
                {
                    ParentId = libraryId, IncludeItemTypes = [type], Recursive = true, Limit = 1, StartIndex = 0,
                    EnableImages = false, EnableUserData = false
                }, cancellationToken);
                count = result.TotalRecordCount is { } total && total >= result.Items.Length ? total
                    : result.TotalRecordCount is null && result.Items.Length == 0 ? 0 : null;
            }
            finally { _libraryCountSlots.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
        {
            if (generation == _sessionGeneration && revision == _libraryCountRevision && !cancellationToken.IsCancellationRequested)
                SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException)
        {
            // An unavailable server aggregate remains unknown instead of using an unfiltered folder count.
        }
        if (generation == _sessionGeneration && revision == _libraryCountRevision && !cancellationToken.IsCancellationRequested)
            _mediaLibraryCounts[(type, libraryId)] = count;
        return count;
    }

    private TextBlock LibraryCountLabel(MediaCardViewModel library)
    {
        var label = LumenUi.Text(string.Empty, 11);
        label.Foreground = LumenTheme.Brush("ImageSub");
        label.Height = 14;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var provided = library.Item.RecursiveItemCount is >= 0 ? library.Item.RecursiveItemCount
            : library.Item.ChildCount is >= 0 ? library.Item.ChildCount : null;
        if (provided is { } count) label.Text = LumenText.Get("{0} items", count.ToString("N0", CultureInfo.CurrentCulture));
        else if (_libraryCounts.TryGetValue(library.Id, out var cached) && cached is { } cachedCount)
            label.Text = LumenText.Get("{0} items", cachedCount.ToString("N0", CultureInfo.CurrentCulture));
        CancellationTokenSource? displayLifetime = null;
        label.Loaded += (_, _) =>
        {
            if (provided is not null) return;
            displayLifetime?.Cancel();
            displayLifetime?.Dispose();
            displayLifetime = new CancellationTokenSource();
            var token = displayLifetime.Token;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                if (token.IsCancellationRequested || !label.IsLoaded) return;
                await ShowLibraryCountAsync(label, library, token);
            });
        };
        label.Unloaded += (_, _) => { displayLifetime?.Cancel(); displayLifetime?.Dispose(); displayLifetime = null; };
        return label;
    }

    private async Task ShowLibraryCountAsync(TextBlock label, MediaCardViewModel library, CancellationToken displayToken)
    {
        var session = _session;
        var generation = _sessionGeneration;
        var revision = _libraryCountRevision;
        if (session is null || _libraryCountLifetime is not { } lifetime || lifetime.IsCancellationRequested) return;
        try
        {
            if (!_libraryCountTasks.TryGetValue(library.Id, out var task))
            {
                task = QueryLibraryCountAsync(session, library, generation, revision, lifetime.Token);
                _libraryCountTasks[library.Id] = task;
            }
            var count = await task.WaitAsync(displayToken);
            if (!displayToken.IsCancellationRequested && label.IsLoaded && generation == _sessionGeneration
                && revision == _libraryCountRevision && count is { } total)
                label.Text = LumenText.Get("{0} items", total.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException) when (displayToken.IsCancellationRequested || generation != _sessionGeneration || revision != _libraryCountRevision) { }
    }

    private async Task<int?> QueryLibraryCountAsync(ConnectedSession session, MediaCardViewModel library,
        int generation, int revision, CancellationToken cancellationToken)
    {
        int? count = null;
        try
        {
            await _libraryCountSlots.WaitAsync(cancellationToken);
            try
            {
                var result = await session.Api.GetItemsAsync(new ItemQuery
                {
                    ParentId = library.Id, Recursive = true, Limit = 1, StartIndex = 0,
                    IncludeItemTypes = LibraryCountTypes(library.Item.CollectionType),
                    EnableImages = false, EnableUserData = false
                }, cancellationToken);
                count = result.TotalRecordCount is { } total && total >= result.Items.Length ? total
                    : result.TotalRecordCount is null && result.Items.Length == 0 ? 0 : null;
            }
            finally { _libraryCountSlots.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
        {
            if (generation == _sessionGeneration && revision == _libraryCountRevision && !cancellationToken.IsCancellationRequested)
                SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException)
        {
            // The count stays unknown when the server cannot provide the aggregate.
        }
        if (generation == _sessionGeneration && revision == _libraryCountRevision && !cancellationToken.IsCancellationRequested)
            _libraryCounts[library.Id] = count;
        return count;
    }

    private static string[]? LibraryCountTypes(string? collectionType) => collectionType?.ToLowerInvariant() switch
    {
        "movies" => ["Movie"],
        "tvshows" => ["Series"],
        "music" => ["MusicAlbum", "Audio"],
        "musicvideos" => ["MusicVideo"],
        "homevideos" => ["Video"],
        "boxsets" => ["BoxSet"],
        "books" => ["Book"],
        "audiobooks" => ["Audio"],
        "photos" => ["Photo"],
        _ => null
    };
}
