using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpegInteropX;
using Windows.Graphics.Imaging;
using WinRT;

namespace EmbyClient.App.Playback;

internal static unsafe partial class NativeAssInterop
{
    private const uint FrameVersion = 1;
    private const int FrameAbiSize = 96;
    private const long MaximumBitmapPixels = 64L * 1024 * 1024;

    internal static bool IsBitmapSelectionAvailable
    {
        get
        {
            try { return IsFrameBridgeAvailable && BitmapSelectionApiVersion() == 1; }
            catch (Exception exception) when (IsUnavailable(exception)) { return false; }
        }
    }

    internal static NativeAssFrameMetadata ReadFrame(FFmpegMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var frame = NewFrameAbi();
            Marshal.ThrowExceptionForHR(GetFrame(pointer, &frame, (uint)sizeof(FrameAbi)));
            return ReadFrameMetadata(frame);
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    /// <summary>Acquires an independently owned snapshot only if the requested native frame is still current.</summary>
    internal static NativeAssFrameLease? AcquireFrame(FFmpegMediaSource source, ulong expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(source);
        nint pointer = 0;
        nint bitmapPointer = 0;
        SoftwareBitmap? bitmap = null;
        var retained = false;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var frame = NewFrameAbi();
            var result = AcquireFrameSnapshot(pointer, expectedSequence, &frame, (uint)sizeof(FrameAbi), &bitmapPointer);
            Marshal.ThrowExceptionForHR(result);
            var metadata = ReadFrameMetadata(frame);
            // The decoder advanced between metadata and acquisition. Polling will read its latest state.
            if (result == 1 || !metadata.HasImage) return null;
            if (metadata.Sequence != expectedSequence || bitmapPointer == 0)
                throw new InvalidOperationException("The native ASS frame snapshot has inconsistent ownership.");
            bitmap = MarshalInspectable<SoftwareBitmap>.FromAbi(bitmapPointer);
            var pixels = (long)bitmap.PixelWidth * bitmap.PixelHeight;
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || pixels > MaximumBitmapPixels
                || metadata.NonzeroAlphaPixels > (ulong)pixels
                || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied
                || !bitmap.IsReadOnly)
                throw new InvalidOperationException("The native ASS snapshot has an incompatible bitmap layout.");
            var lease = new NativeAssFrameLease(metadata, bitmap);
            retained = true;
            return lease;
        }
        finally
        {
            try { if (!retained) bitmap?.Dispose(); }
            finally
            {
                if (bitmapPointer != 0) MarshalInspectable<SoftwareBitmap>.DisposeAbi(bitmapPointer);
                if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
            }
        }
    }

    private static FrameAbi NewFrameAbi()
    {
        if (sizeof(FrameAbi) != FrameAbiSize)
            throw new InvalidOperationException("The managed ASS frame ABI size is invalid.");
        return new() { Size = (uint)sizeof(FrameAbi), Version = FrameVersion };
    }

    private static NativeAssFrameMetadata ReadFrameMetadata(FrameAbi value)
    {
        var geometry = new AssSubtitleGeometry(value.VideoWidth, value.VideoHeight, value.PixelAspectRatio,
            value.X, value.Y, value.Width, value.Height);
        if (value.Size != FrameAbiSize || value.Version != FrameVersion || value.HasImage > 1
            || value.PresentationMode > 2
            || value.HasImage != 0 && (value.TrackId == 0 || value.Generation == 0 || value.Sequence == 0
                || value.PresentationMode == 0 || value.MediaPositionTicks < 0 || !AssSubtitleLayoutPolicy.IsValid(geometry)))
            throw new InvalidOperationException("The native ASS frame metadata is invalid.");
        return new(value.HasImage != 0, value.PresentationMode, value.TrackId, value.Generation, value.Sequence,
            value.MediaPositionTicks, value.ScriptPositionTicks, geometry, value.NonzeroAlphaPixels);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FrameAbi
    {
        public uint Size;
        public uint Version;
        public uint HasImage;
        public uint PresentationMode;
        public ulong TrackId;
        public ulong Generation;
        public ulong Sequence;
        public long MediaPositionTicks;
        public long ScriptPositionTicks;
        public int VideoWidth;
        public int VideoHeight;
        public double PixelAspectRatio;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public ulong NonzeroAlphaPixels;
    }

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyAssSubtitleFrameApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint FrameApiVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyBitmapSubtitleSelectionApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint BitmapSelectionApiVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyGetAssSubtitleFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetFrame(nint source, FrameAbi* frame, uint size);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyAcquireAssSubtitleFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int AcquireFrameSnapshot(nint source, ulong expectedSequence,
        FrameAbi* frame, uint size, nint* bitmap);
}

internal readonly record struct NativeAssFrameMetadata(bool HasImage, uint PresentationMode, ulong TrackId,
    ulong Generation, ulong Sequence, long MediaPositionTicks, long ScriptPositionTicks,
    AssSubtitleGeometry Geometry, ulong NonzeroAlphaPixels);

/// <summary>Owns a separate read-only subtitle bitmap copy, never the decoder's current bitmap.</summary>
internal sealed partial class NativeAssFrameLease(NativeAssFrameMetadata metadata, SoftwareBitmap bitmap) : IDisposable
{
    private SoftwareBitmap? _bitmap = bitmap;

    internal NativeAssFrameMetadata Metadata { get; } = metadata;
    internal SoftwareBitmap Bitmap => Volatile.Read(ref _bitmap) ?? throw new ObjectDisposedException(nameof(NativeAssFrameLease));

    public void Dispose() => Interlocked.Exchange(ref _bitmap, null)?.Dispose();
}
