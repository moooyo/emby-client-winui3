using EmbyClient.Api;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private readonly Dictionary<string, long> _hiddenResumeChanges = new(StringComparer.Ordinal);
    private long _resumeRevision;
    private TaskCompletionSource? _pendingResumeMutation;

    public async Task RemoveFromContinueWatchingAsync(MediaCardViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsMutating || _api is null || _sessionCancellation is null || string.IsNullOrWhiteSpace(item.Id)) return;
        var sessionVersion = _sessionVersion;
        var api = _api;
        var id = item.Id;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        var mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResumeMutation = mutation;
        IsMutating = true;
        try
        {
            // HideFromResume is not returned in UserItemDataDto and must not change playback state.
            _ = await api.RemoveFromResumeAsync(id, linked.Token);
            if (!IsCurrentSession(sessionVersion)) return;
            ApplyHiddenResume(id);
        }
        finally
        {
            if (ReferenceEquals(_pendingResumeMutation, mutation)) _pendingResumeMutation = null;
            if (IsCurrentSession(sessionVersion)) IsMutating = false;
            mutation.TrySetResult();
        }
    }

    private void ResetResumeState()
    {
        var mutation = _pendingResumeMutation;
        _pendingResumeMutation = null;
        _hiddenResumeChanges.Clear();
        _resumeRevision = 0;
        mutation?.TrySetResult();
    }

    private async Task AwaitResumeMutationAsync(CancellationToken cancellationToken)
    {
        while (_pendingResumeMutation is { } mutation)
            await mutation.Task.WaitAsync(cancellationToken);
    }

    private void ApplyHiddenResume(string itemId)
    {
        _hiddenResumeChanges[itemId] = _resumeRevision = ++_userDataRevision;
        var retainedRows = PruneResumeRows(HomeRows, itemId);
        for (var index = HomeRows.Count - 1; index >= 0; index--)
            if (!retainedRows.Contains(HomeRows[index])) HomeRows.RemoveAt(index);

        if (_location.Kind == LocationKind.Resume)
        {
            var removed = RemoveResumeCards(Items, itemId);
            _nextIndex = Math.Max(0, _nextIndex - removed);
            UpdateCount(removed == 0 && HasMore ? null : DecreaseResumeCount(TotalItemsCount, removed));
            HasItems = Items.Count > 0;
            if (TotalItemsCount is { } total) HasMore = HasMore && _nextIndex < total;
            if (removed > 0) NotifyCollectionCommitted();
        }
        else if (IsHome) HasItems = Libraries.Count > 0 || HomeRows.Count > 0;

        var snapshots = _history.ToArray();
        _history.Clear();
        foreach (var snapshot in snapshots.Reverse()) _history.Push(PruneResumeSnapshot(snapshot, itemId));
        if (_searchOriginSnapshot is { } searchOrigin) _searchOriginSnapshot = PruneResumeSnapshot(searchOrigin, itemId);
        NotifyPresentation();
    }

    private BrowseSnapshot PruneResumeSnapshot(BrowseSnapshot snapshot, string itemId)
    {
        var rows = PruneResumeRows(snapshot.Rows, itemId);
        var isResume = snapshot.Location.Kind == LocationKind.Resume;
        var items = isResume ? snapshot.Items.Where(card => card.Id != itemId).ToArray() : snapshot.Items;
        var removed = snapshot.Items.Length - items.Length;
        var total = isResume ? removed == 0 && snapshot.HasMore ? null : DecreaseResumeCount(snapshot.TotalCount, removed) : snapshot.TotalCount;
        var nextIndex = isResume ? Math.Max(0, snapshot.NextIndex - removed) : snapshot.NextIndex;
        return snapshot with
        {
            Items = items,
            Rows = rows,
            HasItems = snapshot.Location.IsHome ? Libraries.Count > 0 || rows.Length > 0 : isResume ? items.Length > 0 : snapshot.HasItems,
            HasMore = isResume && total is { } count ? snapshot.HasMore && nextIndex < count : snapshot.HasMore,
            NextIndex = nextIndex,
            TotalCount = total,
            Subtitle = isResume && (removed > 0 || total != snapshot.TotalCount) ? ResumeCountSubtitle(total, items.Length) : snapshot.Subtitle,
            SearchOrigin = snapshot.SearchOrigin is { } origin ? PruneResumeSnapshot(origin, itemId) : null
        };
    }

    private static MediaShelfViewModel[] PruneResumeRows(IEnumerable<MediaShelfViewModel> source, string itemId)
    {
        var rows = source.ToArray();
        foreach (var row in rows.Where(row => row.Section == HomeSection.ContinueWatching)) RemoveResumeCards(row.Items, itemId);
        return rows.Where(row => row.Section != HomeSection.ContinueWatching || row.Items.Count > 0).ToArray();
    }

    private static int RemoveResumeCards(ObservableCollection<MediaCardViewModel> cards, string itemId)
    {
        var removed = 0;
        for (var index = cards.Count - 1; index >= 0; index--)
        {
            if (cards[index].Id != itemId) continue;
            cards.RemoveAt(index);
            removed++;
        }
        return removed;
    }

    private BaseItemDto[] PreserveHiddenResumeItems(IEnumerable<BaseItemDto> items, long readRevision) =>
        items.Where(item => item.Id is not { } id || !_hiddenResumeChanges.TryGetValue(id, out var revision)
            || revision <= readRevision).ToArray();

    private sealed record ResumePageRead(QueryResult<BaseItemDto> Page, long ReadRevision);

    private async Task<ResumePageRead> QueryStableResumePageAsync(EmbyApiClient api, BrowseLocation location,
        Func<int> offset, int pageVersion, CancellationToken cancellationToken)
    {
        // Hiding a consumed item shifts server offsets, so an in-flight page must be read again.
        while (true)
        {
            if (location.Kind == LocationKind.Resume) await AwaitResumeMutationAsync(cancellationToken);
            var revision = _resumeRevision;
            var readRevision = _userDataRevision;
            var page = await QueryItemsAsync(api, location, offset(), cancellationToken);
            if (location.Kind == LocationKind.Resume) await AwaitResumeMutationAsync(cancellationToken);
            if (location.Kind != LocationKind.Resume || revision == _resumeRevision
                || !CanCommitPage(pageVersion, cancellationToken) || HasPendingSearch) return new(page, readRevision);
        }
    }

    private QueryResult<BaseItemDto> PreserveHiddenResumePage(BrowseLocation location, QueryResult<BaseItemDto> page, long readRevision)
    {
        if (location.Kind != LocationKind.Resume) return page;
        var items = PreserveHiddenResumeItems(page.Items, readRevision);
        return page with { Items = items, TotalRecordCount = DecreaseResumeCount(page.TotalRecordCount, page.Items.Length - items.Length) };
    }

    private PageWindow PreserveHiddenResumeWindow(BrowseLocation location, PageWindow page, long readRevision)
    {
        if (location.Kind != LocationKind.Resume) return page;
        var items = PreserveHiddenResumeItems(page.Items, readRevision);
        var removed = page.Items.Length - items.Length;
        return page with
        {
            Items = items,
            NextIndex = Math.Max(0, page.NextIndex - removed),
            TotalCount = DecreaseResumeCount(page.TotalCount, removed)
        };
    }

    private static int? DecreaseResumeCount(int? total, int removed) => total is >= 0 ? Math.Max(0, total.Value - removed) : total;

    private static string ResumeCountSubtitle(int? total, int loadedCount)
    {
        var count = Math.Max(total ?? loadedCount, loadedCount);
        return count == 1 ? "1 item" : $"{count:N0} items";
    }
}
