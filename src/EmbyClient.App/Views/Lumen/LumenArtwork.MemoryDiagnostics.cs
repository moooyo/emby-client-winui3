using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenArtwork
{
    private bool _memoryDiagnosticsTracked;

    private void InitializeMemoryDiagnostics()
    {
        if (LibraryMemoryDiagnostics.Enabled)
            _memoryDiagnosticsTracked = LibraryMemoryDiagnostics.TrackArtwork(this);
    }

    private void ObserveMemoryCache(LibraryViewModel library)
    {
        if (_memoryDiagnosticsTracked) LibraryMemoryDiagnostics.TrackCache(library.Images);
    }

    private void ObserveMemorySourceBound()
    {
        if (_memoryDiagnosticsTracked) LibraryMemoryDiagnostics.SourceBound();
    }

    private void ObserveMemorySourceClearing()
    {
        if (!_memoryDiagnosticsTracked) return;
        try { LibraryMemoryDiagnostics.SourceClearing(Image.Source is not null); }
        catch (Exception exception) { LibraryMemoryDiagnostics.ReadFailure(exception); }
    }

    // Only the dispatcher sampler calls this method. The registry stores a weak reference to this control.
    internal ArtworkMemorySnapshot ReadMemorySnapshot() => new(IsLoaded, Image.Source is not null,
        _request is not null, _subscribed);
}
