using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using EmbyClient.NativeProbe;
using EmbyClient.Playback;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private int _bitmapProbeOutstandingLeases;

    internal Task<BitmapSubtitleIdentityLease> CaptureBitmapIdentityForProbeAsync(Guid playbackId) => OnDispatcherAsync(() =>
    {
        var session = Find(playbackId);
        if (session is not { IsOpened: true, DecoderSource: not null, MediaSource: not null, Item: not null,
            Player: not null, NativeSession: not null, NativeEmbeddedSubtitle: not null })
            throw new PlaybackException("BitmapIdentityNativeObjectsMissing");
        return BitmapSubtitleIdentityLease.Create(session, session.DecoderSource, session.MediaSource,
            session.Item, session.Player, session.NativeSession);
    });

    internal Task<BitmapSubtitleNativeSample> ObserveBitmapSelectionForProbeAsync(Guid playbackId,
        BitmapSubtitleIdentityLease? identity = null) => OnDispatcherAsync(() =>
    {
        var value = new BitmapSubtitleNativeSample();
        var session = Find(playbackId);
        if (session is not { DecoderSource: not null, NativeSession: not null }) return value;
        value.ActivePlaybackMatches = true;
        value.ProductSessionMatchesBaseline = identity?.MatchesProductSession(session) == true;
        value.ActualNativeDecoderConfigured = session.NativeDecoderConfigured;
        value.DefaultPlayerOwnerPresent = _playerOwner is not null && ReferenceEquals(session.Player, _playerOwner)
            && !ReuseNativeHttpControlPlayer && NativeHttpControlObservation is null;
        value.FullSourceDirectStream = session.Request.DeliveryMethod == PlaybackDeliveryMethod.DirectStream
            && session.Request.TimelineKind == PlaybackTimelineKind.FullSource && session.Request.TimelineOffsetTicks == 0;
        value.HasExternalSubtitle = session.Request.ExternalSubtitleUri is not null || session.TimedText is not null
            || session.LocalSubtitle is not null || session.ExternalAssSubtitle is not null;
        value.NativeState = session.NativeSession.PlaybackState.ToString();
        value.PositionTicks = session.NativeSession.Position.Ticks;
        value.RequestedSubtitleStreamIndex = session.Request.SubtitleStreamIndex;
        value.RetainedNativeSubtitleStreamIndex = session.NativeEmbeddedSubtitle?.StreamIndex;
        value.RetainedNativeSubtitleCodec = NativeBitmapSubtitleCodec(session.NativeEmbeddedSubtitle?.CodecName);
        value.SubtitlePresentationActive = session.NativeSubtitlePresentationActive;
        value.HasPresentationTimer = session.AssFrameTimer is not null;
        value.HasPresentationTrack = session.AssPresentationTrack is not null;
        value.PresentationEpoch = session.AssPresentationEpoch;
        if (identity is not null)
        {
            if (session.MediaSource is null || session.Item is null || session.Player is null)
                throw new PlaybackException("BitmapIdentityNativeObjectsMissing");
            value.NativeIdentities.AddRange(identity.Compare(session.DecoderSource, session.MediaSource,
                session.Item, session.Player, session.NativeSession));
        }

        var metadata = NativeAssInterop.ReadFrame(session.DecoderSource);
        value.HasImage = metadata.HasImage;
        value.PresentationMode = metadata.PresentationMode;
        value.TrackId = metadata.TrackId;
        value.Generation = metadata.Generation;
        value.Sequence = metadata.Sequence;
        value.CueSourceTimestampTicks = metadata.MediaPositionTicks;
        value.ScriptPositionTicks = metadata.ScriptPositionTicks;
        value.VideoWidth = metadata.Geometry.VideoWidth;
        value.VideoHeight = metadata.Geometry.VideoHeight;
        value.PixelAspectRatio = metadata.Geometry.PixelAspectRatio;
        value.X = metadata.Geometry.X;
        value.Y = metadata.Geometry.Y;
        value.Width = metadata.Geometry.Width;
        value.Height = metadata.Geometry.Height;
        value.NativeNonzeroAlphaPixels = metadata.NonzeroAlphaPixels;
        if (!metadata.HasImage || metadata.PresentationMode != 2) return value;
        using var frame = NativeAssInterop.AcquireFrame(session.DecoderSource, metadata.Sequence);
        if (frame is null) return value;
        Interlocked.Increment(ref _bitmapProbeOutstandingLeases);
        try
        {
            if (frame.Metadata != metadata) throw new PlaybackException("BitmapIdentityFrameChangedDuringAcquire");
            var bitmap = frame.Bitmap;
            var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyToBuffer(bytes.AsBuffer());
            ulong alpha = 0;
            for (var offset = 3; offset < bytes.Length; offset += 4)
                if (bytes[offset] != 0) alpha++;
            value.IndependentlyAcquiredBitmap = true;
            value.BitmapWidth = bitmap.PixelWidth;
            value.BitmapHeight = bitmap.PixelHeight;
            value.CountedNonzeroAlphaPixels = alpha;
            value.BgraPixelSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return value;
        }
        finally { Interlocked.Decrement(ref _bitmapProbeOutstandingLeases); }
    });

    internal Task<bool> ReleaseBitmapIdentityForProbeAsync(BitmapSubtitleIdentityLease identity) => OnDispatcherAsync(() =>
    {
        identity.Dispose();
        return identity.HeldNativeReferences == 0 && identity.HeldProjectionReferences == 0
            && Volatile.Read(ref _bitmapProbeOutstandingLeases) == 0;
    });

    internal int BitmapProbeOutstandingBitmapLeases => Volatile.Read(ref _bitmapProbeOutstandingLeases);

    internal Task<bool> BitmapProbeSessionDrainedAsync() => OnDispatcherAsync(() =>
        _current is null && _playerOwner?.Source is null && element.MediaPlayer is null);

    internal Task<bool> BitmapProbeOwnerClearedAsync() => OnDispatcherAsync(() =>
        _current is null && _playerOwner is null && element.MediaPlayer is null && _disposalTask?.IsCompleted == true);
}

/// <summary>Retains canonical native identities only for one bounded observation interval.</summary>
internal sealed partial class BitmapSubtitleIdentityLease : IDisposable
{
    private static int _nextOrdinal;
    private static readonly Guid InspectableId = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");
    private readonly List<IdentityReference> _references = [];
    private object? _productSession;
    private bool _disposed;

    internal int HeldNativeReferences => _references.Count(value => value.CanonicalUnknown != 0);
    internal int HeldProjectionReferences => _references.Count(value => value.Projection is not null)
        + (_productSession is null ? 0 : 1);

    internal static BitmapSubtitleIdentityLease Create(object productSession, FFmpegMediaSource decoder,
        MediaSource source, MediaPlaybackItem item, MediaPlayer player, MediaPlaybackSession nativeSession)
    {
        var result = new BitmapSubtitleIdentityLease { _productSession = productSession };
        try
        {
            result.Retain("FFmpegMediaSource", decoder);
            result.Retain("MediaSource", source);
            result.Retain("MediaPlaybackItem", item);
            result.Retain("MediaPlayer", player);
            result.Retain("MediaPlaybackSession", nativeSession);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal bool MatchesProductSession(object session) => !_disposed && ReferenceEquals(_productSession, session);

    internal BitmapNativeIdentityObservation[] Compare(FFmpegMediaSource decoder, MediaSource source,
        MediaPlaybackItem item, MediaPlayer player, MediaPlaybackSession nativeSession)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [CompareOne(0, decoder), CompareOne(1, source), CompareOne(2, item),
            CompareOne(3, player), CompareOne(4, nativeSession)];
    }

    private void Retain<T>(string role, T projection)
    {
        var canonical = CanonicalUnknown(projection);
        try
        {
            _references.Add(new IdentityReference(role, projection!, canonical, Interlocked.Increment(ref _nextOrdinal)));
            canonical = 0;
        }
        finally { if (canonical != 0) Marshal.Release(canonical); }
    }

    private BitmapNativeIdentityObservation CompareOne<T>(int index, T projection)
    {
        var baseline = _references[index];
        var canonical = CanonicalUnknown(projection);
        try
        {
            var same = canonical == baseline.CanonicalUnknown;
            return new BitmapNativeIdentityObservation
            {
                Role = baseline.Role, BaselineOrdinal = baseline.Ordinal,
                ObservedOrdinal = same ? baseline.Ordinal : Interlocked.Increment(ref _nextOrdinal),
                IInspectableAndIUnknownQueriesSucceeded = true, CanonicalIdentityMatchesBaseline = same,
                ProjectionMatchesBaseline = ReferenceEquals(baseline.Projection, projection)
            };
        }
        finally { Marshal.Release(canonical); }
    }

    private static nint CanonicalUnknown<T>(T projection)
    {
        nint abi = 0;
        nint inspectable = 0;
        nint unknown = 0;
        try
        {
            abi = MarshalInspectable<T>.FromManaged(projection);
            if (abi == 0) throw new PlaybackException("BitmapIdentityInspectableMissing");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in InspectableId, out inspectable));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in UnknownId, out unknown));
            if (unknown == 0) throw new PlaybackException("BitmapIdentityUnknownMissing");
            var retained = unknown;
            unknown = 0;
            return retained;
        }
        finally
        {
            if (unknown != 0) Marshal.Release(unknown);
            if (inspectable != 0) Marshal.Release(inspectable);
            if (abi != 0) MarshalInspectable<T>.DisposeAbi(abi);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _productSession = null;
        foreach (var reference in _references)
        {
            reference.Projection = null;
            var canonical = reference.CanonicalUnknown;
            reference.CanonicalUnknown = 0;
            if (canonical != 0) Marshal.Release(canonical);
        }
    }

    private sealed class IdentityReference(string role, object projection, nint canonicalUnknown, int ordinal)
    {
        internal string Role { get; } = role;
        internal object? Projection { get; set; } = projection;
        internal nint CanonicalUnknown { get; set; } = canonicalUnknown;
        internal int Ordinal { get; } = ordinal;
    }
}
