using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace EmbyClient.App.Services;

/// <summary>Decodes and applies a poster on the caller's XAML UI context, then releases the input stream.</summary>
internal static class NativePosterDecoder
{
    // The optional observer must not retain the bitmap or change decode behavior.
    // This file is also linked into NativeProbe, which leaves the observer unset.
    internal static Action<NativePosterDecodeObservation>? DiagnosticObserver { get; set; }

    internal static async Task DecodeAndApplyAsync(byte[] bytes, int width, int height, CancellationToken cancellationToken,
        Action<BitmapImage> apply)
    {
        var observer = DiagnosticObserver;
        if (observer is not null) Notify(observer, new(NativePosterDecodeStage.Started, width, height, bytes.Length));
        try
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
            if (observer is not null) Notify(observer, new(NativePosterDecodeStage.BitmapCreated, width, height, bytes.Length, bitmap));
            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
            if (observer is not null) Notify(observer, new(NativePosterDecodeStage.SourceSet, width, height, bytes.Length, bitmap));
            apply(bitmap);
            if (observer is not null) Notify(observer, new(NativePosterDecodeStage.Completed, width, height, bytes.Length));
        }
        catch (OperationCanceledException)
        {
            if (observer is not null) Notify(observer, new(NativePosterDecodeStage.Canceled, width, height, bytes.Length));
            throw;
        }
        catch (Exception exception)
        {
            if (observer is not null) Notify(observer, new(NativePosterDecodeStage.Failed, width, height, bytes.Length, Error: exception));
            throw;
        }
    }

    private static void Notify(Action<NativePosterDecodeObservation> observer, NativePosterDecodeObservation observation)
    {
        try { observer(observation); }
        catch (Exception) { /* Optional diagnostics must not change artwork behavior. */ }
    }
}

internal enum NativePosterDecodeStage { Started, BitmapCreated, SourceSet, Completed, Canceled, Failed }

// Bitmap and Error are borrowed only for the synchronous notification.
internal readonly record struct NativePosterDecodeObservation(NativePosterDecodeStage Stage, int RequestedWidth,
    int RequestedHeight, int InputCompressedBytes, BitmapImage? Bitmap = null, Exception? Error = null);
