using EmbyClient.Api;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private CancellationTokenSource? _homeRefreshCancellation;
    private int _homeRefreshRequestVersion;
    private int _homeReadVersion;
    private int _viewsReadVersion;
    private int _viewsCommittedVersion;
    private bool _homeRowsStale;

    public void InvalidateHomeSnapshots()
    {
        _homeReadVersion++;
        if (_location.IsHome) _homeRowsStale = true;
        RewriteHomeSnapshots(snapshot => snapshot with { HomeRowsStale = true });
    }

    /// <summary>Refreshes home data and history without navigating or canceling the visible page.</summary>
    public async Task<bool> RefreshHomeShelvesAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null || _sessionCancellation is null || cancellationToken.IsCancellationRequested) return false;
        var api = _api;
        var sessionVersion = _sessionVersion;
        var libraryRequestVersion = _libraryRequestVersion;
        var requestVersion = ++_homeRefreshRequestVersion;
        var homeReadVersion = BeginHomeRead();
        var viewsReadVersion = ++_viewsReadVersion;
        var viewsUserDataRevision = _userDataRevision;
        CancelAndDispose(ref _homeRefreshCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _homeRefreshCancellation = source;
        var token = source.Token;
        try
        {
            var views = await api.GetViewsAsync(cancellationToken: token);
            var read = await ReadHomeShelvesAsync(api, views.Items, token, strictResumeRead: true);
            if (!CanCommit()) return false;
            if (read.Results.Select(result => result.Failure).FirstOrDefault(failure => failure is not null && IsSessionExpired(failure)) is { } expired)
            {
                NotifyHomeRefreshSessionExpired(expired);
                return false;
            }
            TryCommitLibraryViews(views.Items, viewsUserDataRevision, viewsReadVersion);
            if (!CanCommit()) return false;
            CommitHomeShelfResults(read.Results, read.UserDataRevision, _location.IsHome);
            return read.Results.All(result => result.Failure is null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommit()) NotifyHomeRefreshSessionExpired(exception);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_homeRefreshCancellation, source)) _homeRefreshCancellation = null;
            source.Dispose();
        }

        bool CanCommit() => requestVersion == _homeRefreshRequestVersion && ReferenceEquals(api, _api)
            && IsCurrentLibraryRequest(sessionVersion, libraryRequestVersion)
            && CanCommitHomeRead(homeReadVersion, sessionVersion, token)
            && viewsReadVersion >= _viewsCommittedVersion;
    }

    private int BeginHomeRead()
    {
        InvalidateHomeSnapshots();
        return _homeReadVersion;
    }

    private bool CanCommitHomeRead(int homeReadVersion, int sessionVersion, CancellationToken cancellationToken) =>
        homeReadVersion == _homeReadVersion && IsCurrentSession(sessionVersion) && !cancellationToken.IsCancellationRequested;

    private void ResetHomeRefreshState()
    {
        _homeRefreshRequestVersion++;
        _homeReadVersion++;
        _viewsCommittedVersion = ++_viewsReadVersion;
        _homeRowsStale = false;
        CancelAndDispose(ref _homeRefreshCancellation);
    }

    private bool TryCommitLibraryViews(BaseItemDto[] views, long userDataRevision, int readVersion)
    {
        if (readVersion < _viewsCommittedVersion) return false;
        _viewsCommittedVersion = readVersion;
        ReconcileCards(Libraries, PreserveNewerUserData(views, userDataRevision));
        _librariesReady = Task.FromResult(true);
        OnPropertyChanged(nameof(HomeLibrariesVisibility));
        return true;
    }

    private async Task<HomeShelvesRead> ReadHomeShelvesAsync(EmbyApiClient api, IEnumerable<BaseItemDto> libraries,
        CancellationToken cancellationToken, Action<HomeShelfResult[], long>? progress = null, bool strictResumeRead = false)
    {
        await AwaitResumeMutationAsync(cancellationToken);
        var userDataRevision = _userDataRevision;
        var resumeRevision = _resumeRevision;
        var results = await QueryHomeShelvesAsync(api, libraries, cancellationToken,
            completed => progress?.Invoke(completed, userDataRevision));
        await AwaitResumeMutationAsync(cancellationToken);
        // Background refreshes require a post-mutation server read; foreground shelves retain their original read revision.
        while (strictResumeRead && resumeRevision != _resumeRevision)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resumeRevision = _resumeRevision;
            var resumeReadRevision = _userDataRevision;
            HomeShelfResult resumed;
            try
            {
                var resume = await api.GetResumeItemsAsync(new ItemQuery
                {
                    Limit = HomeShelfSize, MediaTypes = ["Video"], Fields = ListFields
                }, cancellationToken);
                resumed = new(new("Continue watching", true, HomeSection.ContinueWatching), resume.Items, null, resumeReadRevision);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                resumed = new(new("Continue watching", true, HomeSection.ContinueWatching), [], exception, resumeReadRevision);
            }
            await AwaitResumeMutationAsync(cancellationToken);
            results = results.Select(result => result.Shelf.Section == HomeSection.ContinueWatching ? resumed : result).ToArray();
        }
        return new(results, userDataRevision);
    }

    private void CommitHomeShelfResults(HomeShelfResult[] results, long userDataRevision, bool updateVisible)
    {
        var stale = results.Any(result => result.Failure is not null);
        var successful = results.Any(result => result.Failure is null);
        if (updateVisible)
        {
            ApplyHomeShelves(results, userDataRevision);
            _homeRowsStale = stale;
            HasError = stale;
            ErrorMessage = stale ? "Some home sections could not be refreshed. Previously loaded content is still shown." : string.Empty;
            SetLoadOutcome(successful || HomeRows.Count > 0 ? PageLoadOutcome.Succeeded : PageLoadOutcome.Failed);
        }
        RewriteHomeSnapshots(snapshot =>
        {
            var rows = MergeHomeShelves(snapshot.Rows, results, userDataRevision);
            return snapshot with
            {
                Rows = rows,
                HasItems = Libraries.Count > 0 || rows.Length > 0,
                HasError = stale,
                ErrorMessage = stale ? "Some home sections could not be refreshed. Previously loaded content is still shown." : string.Empty,
                Outcome = successful || rows.Length > 0 ? PageLoadOutcome.Succeeded : PageLoadOutcome.Failed,
                HomeRowsStale = stale
            };
        });
    }

    private void RewriteHomeSnapshots(Func<BrowseSnapshot, BrowseSnapshot> transform)
    {
        Rewrite(_history);
        Rewrite(_forwardHistory);

        void Rewrite(Stack<BrowseSnapshot> history)
        {
            var snapshots = history.ToArray();
            history.Clear();
            foreach (var snapshot in snapshots.Reverse())
                history.Push(snapshot.Location.IsHome ? transform(snapshot) : snapshot);
        }
    }

    private void NotifyHomeRefreshSessionExpired(Exception exception)
    {
        if (_sessionExpiredNotified || !IsSessionExpired(exception)) return;
        _sessionExpiredNotified = true;
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

    private sealed record HomeShelvesRead(HomeShelfResult[] Results, long UserDataRevision);
}
