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
    private readonly SemaphoreSlim _libraryCountSlots = new(3, 3);
    private CancellationTokenSource? _libraryCountLifetime;
    private int _libraryCountRevision;

    private void ResetLibraryCounts(bool start)
    {
        _libraryCountRevision++;
        _libraryCountLifetime?.Cancel();
        _libraryCountLifetime?.Dispose();
        _libraryCountLifetime = start ? new CancellationTokenSource() : null;
        _libraryCounts.Clear();
        _libraryCountTasks.Clear();
        _homeSignature = string.Empty;
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
