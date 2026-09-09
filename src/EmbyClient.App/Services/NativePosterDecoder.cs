using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace EmbyClient.App.Services;

/// <summary>Decodes and applies a poster on the caller's XAML UI context, then releases the input stream.</summary>
internal static class NativePosterDecoder
{
    internal static async Task DecodeAndApplyAsync(byte[] bytes, int width, int height, CancellationToken cancellationToken,
        Action<BitmapImage> apply)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = width, DecodePixelHeight = height };
        await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
        apply(bitmap);
    }
}
