using EmbyClient.App.Services;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    public ImageCache Images => _images;

    /// <summary>Clears authenticated cached image bytes without altering account or media data.</summary>
    public void ClearImageCache() => _images.Clear();
}
