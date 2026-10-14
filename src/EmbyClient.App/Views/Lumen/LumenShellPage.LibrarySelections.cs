using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private readonly MediaLibrarySelectionStore _mediaLibrarySelections = new();
    private MediaLibrarySelections _selectedMediaLibraries = new();
    private string? _mediaLibrarySelectionAccount;
    private bool _restoringMediaLibrarySelections;
    private Task _pendingMediaLibrarySave = Task.CompletedTask;

    private async Task RestoreMediaLibrarySelectionsAsync(ConnectedSession session)
    {
        _restoringMediaLibrarySelections = true;
        _mediaLibrarySelectionAccount = null;
        try
        {
            await _pendingMediaLibrarySave;
            var selections = session.AllowLocalPersistence
                ? await _mediaLibrarySelections.LoadAsync(session.AccountKey,
                    _sessionLifetime?.Token ?? CancellationToken.None)
                : new MediaLibrarySelections();
            if (!ReferenceEquals(session, _session) || _closing || _transitioning) return;
            _selectedMediaLibraries = selections;
            _library.RestoreMediaLibrarySelection("Movie", selections.MovieLibraryId);
            _library.RestoreMediaLibrarySelection("Series", selections.SeriesLibraryId);
            _mediaLibrarySelectionAccount = session.AccountKey;
            if (session.AllowLocalPersistence && _mediaLibrarySelections.LastIssue == MediaLibrarySelectionPersistenceIssue.InvalidData)
                ShowNotice(LumenText.Get("Some preferences could not be read. Defaults are used without changing the saved file."), InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { }
        catch (MediaLibrarySelectionStoreException exception)
        {
            if (ReferenceEquals(session, _session) && !_closing)
            {
                _selectedMediaLibraries = new();
                _mediaLibrarySelectionAccount = session.AccountKey;
                ShowError(exception);
            }
        }
        finally { _restoringMediaLibrarySelections = false; }
    }

    private void MediaLibrarySelectionChanged(object? sender, MediaLibrarySelectionChangedEventArgs args)
    {
        var session = _session;
        if (session is null || _closing || _transitioning || _restoringMediaLibrarySelections
            || _mediaLibrarySelectionAccount != session.AccountKey) return;
        var selections = args.MediaType switch
        {
            "Movie" => _selectedMediaLibraries with { MovieLibraryId = args.LibraryId },
            "Series" => _selectedMediaLibraries with { SeriesLibraryId = args.LibraryId },
            _ => _selectedMediaLibraries
        };
        if (selections == _selectedMediaLibraries) return;
        _selectedMediaLibraries = selections;
        if (!session.AllowLocalPersistence) return;
        _pendingMediaLibrarySave = SaveMediaLibrarySelectionsAsync(_pendingMediaLibrarySave, session.AccountKey, selections);
    }

    private async Task SaveMediaLibrarySelectionsAsync(Task precedingSave, string accountKey, MediaLibrarySelections selections)
    {
        try { await precedingSave.ConfigureAwait(false); }
        catch (Exception) { /* A previous failure does not prevent a new explicit choice from being saved. */ }
        try
        {
            await _mediaLibrarySelections.SaveAsync(accountKey, selections.MovieLibraryId,
                selections.SeriesLibraryId).ConfigureAwait(false);
        }
        catch (MediaLibrarySelectionStoreException exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closing && _session?.AccountKey == accountKey) ShowError(exception);
            });
        }
    }
}
