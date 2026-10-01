using EmbyClient.Playback;
using FFmpegInteropX;

namespace EmbyClient.App.Playback;

/// <summary>Describes the decoder producing frames, rather than the requested policy or codec availability.</summary>
public sealed record VideoDecodingSnapshot(bool HardwareRequested, string Decoder, bool HardwareFallback,
    string Codec, string? GpuName = null, uint? GpuVendorId = null, long? AdapterLuid = null,
    string RequestedApi = "Auto", string ActualApi = "Pending", string? FallbackReason = null,
    string? NativeDecoderName = null, ulong DecodedFrames = 0, ulong HardwareDecodedFrames = 0);

public sealed partial class NativePlaybackEngine
{
    private string _decoderApi = initialDecoderApi is null ? initialHardwareDecoding ? "Auto" : "Software"
        : VendorDecoderInterop.IsKnownApi(initialDecoderApi) ? initialDecoderApi
        : throw new ArgumentOutOfRangeException(nameof(initialDecoderApi));
    private VideoDecodingSnapshot? _videoDecoding;

    public VideoDecodingSnapshot? VideoDecoding => Volatile.Read(ref _videoDecoding);

    /// <summary>Changes the policy for subsequent opens without interrupting the current playback.</summary>
    public void ConfigureHardwareDecoding(bool enabled) => ConfigureVideoDecoderApi(enabled ? "Auto" : "Software");

    /// <summary>Selects one decoder API for the next opening without changing the current session.</summary>
    public void ConfigureVideoDecoderApi(string api)
    {
        if (!VendorDecoderInterop.IsKnownApi(api)) throw new ArgumentOutOfRangeException(nameof(api));
        Volatile.Write(ref _decoderApi, api);
    }

    private async Task CreateDecodedSourceAsync(Session session)
    {
        Uri localUri;
        if (MediaDeliveryClassifier.IsHls(session.Request))
        {
            var relay = await HlsHttpRelay.OpenAsync(session.Transport, session.Request.MediaUri,
                session.Lifetime.Token, code => OnRelayFailure(session, code)).ConfigureAwait(false);
            await OnDispatcherAsync(() => { session.HlsRelay = relay; EnsureActive(session); }).ConfigureAwait(false);
            localUri = relay.LocalUri;
        }
        else if (session.Request.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && session.Request.TimelineKind == PlaybackTimelineKind.ProgressiveSegment)
        {
            var relay = await ProgressiveHttpRelay.OpenAsync(session.Transport, session.Request.MediaUri,
                session.Request.Source.TranscodingContainer, session.Lifetime.Token,
                code => OnRelayFailure(session, code)).ConfigureAwait(false);
            await OnDispatcherAsync(() => { session.ProgressiveRelay = relay; EnsureActive(session); }).ConfigureAwait(false);
            localUri = relay.LocalUri;
        }
        else
        {
            var relay = await SessionHttpRelay.OpenAsync(session.Transport, session.Request.MediaUri,
                session.Request.Source.Container, session.Lifetime.Token,
                code => OnRelayFailure(session, code)).ConfigureAwait(false);
            await OnDispatcherAsync(() => { session.DirectRelay = relay; EnsureActive(session); }).ConfigureAwait(false);
            localUri = relay.LocalUri;
        }

        var creation = await OnDispatcherAsync(() =>
        {
            EnsureActive(session);
            var config = new MediaSourceConfig();
            config.Video.VideoDecoderMode = session.HardwareRequested
                ? VideoDecoderMode.Automatic : VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            session.NativeDecoderConfigured = VendorDecoderInterop.TryConfigure(config, session.RequestedDecoderApi);
            if (VendorDecoderInterop.IsVendorApi(session.RequestedDecoderApi) && !session.NativeDecoderConfigured)
            {
                // A missing extension must never silently execute D3D11 while presenting a vendor API selection.
                config.Video.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            }
            config.Video.HdrSupport = HdrSupport.Disabled;
            config.General.MaxSupportedPlaybackRate = MaximumPlaybackRate;
            config.General.FastSeek = false;
            config.General.KeepMetadataOnMediaSourceClosed = false;
            config.Subtitles.AutoSelectForcedSubtitles = false;
            // FFmpeg sees capability URLs only. Managed relays retain credentials and validate all upstream requests.
            config.FFmpegOptions.Add("protocol_whitelist", "http,tcp,crypto");
            config.FFmpegOptions.Add("rw_timeout", "15000000");
            return FFmpegMediaSource.CreateFromUriAsync(localUri.AbsoluteUri, config).AsTask(session.Lifetime.Token);
        }).ConfigureAwait(false);
        var decoder = await creation.ConfigureAwait(false);
        await OnDispatcherAsync(() =>
        {
            if (!IsActive(session))
            {
                Release(session, "CloseLateDecoderSource", decoder.Dispose);
                throw new OperationCanceledException(session.Lifetime.Token);
            }
            session.DecoderSource = decoder;
            session.Item = decoder.CreateMediaPlaybackItem();
            session.MediaSource = session.Item.Source;
        }).ConfigureAwait(false);
    }

    private void RefreshVideoDecoding(Session session)
    {
        if (session.DecoderSource is not { } source) return;
        try
        {
            var video = source.CurrentVideoStream;
            if (video is null) return;
            // Automatic mode initially reports software until the MF D3D11 device and first frame arrive.
            if (!session.IsOpened || session.NativeSession?.Position <= TimeSpan.Zero) return;
            var native = session.NativeDecoderConfigured ? VendorDecoderInterop.Read(source) : null;
            var vendorRequested = VendorDecoderInterop.IsVendorApi(session.RequestedDecoderApi);
            var actual = native?.ActualApi ?? (session.NativeDecoderConfigured ? "Pending" : video.DecoderEngine switch
            {
                DecoderEngine.FFmpegD3D11HardwareDecoder => "D3D11",
                DecoderEngine.FFmpegSoftwareDecoder => "Software",
                _ => "Pending"
            });
            var decoder = actual switch { "IntelQsv" => "QSV", "AmdAmf" => "AMF", "NvidiaNvdec" => "NVDEC", _ => actual };
            if (actual == "D3D11" && session.DecoderGpu is null)
                session.DecoderGpu = DecoderDeviceInfo.Read(source.GetMediaStreamSource());
            var gpu = actual == "D3D11" ? session.DecoderGpu : null;
            var codecName = video.CodecName.ToLowerInvariant();
            if (codecName.EndsWith("_qsv", StringComparison.Ordinal) || codecName.EndsWith("_amf", StringComparison.Ordinal))
                codecName = codecName[..^4];
            else if (codecName.EndsWith("_cuvid", StringComparison.Ordinal)) codecName = codecName[..^6];
            var codec = codecName switch
            {
                "h264" => "H264", "hevc" => "HEVC", "av1" => "AV1", "vp8" => "VP8", "vp9" => "VP9",
                "mpeg2video" => "MPEG2", "vc1" => "VC1", "wmv3" => "WMV3", _ => "Other"
            };
            var fallback = session.HardwareRequested && actual == "Software";
            var reason = fallback ? native?.FallbackReason
                ?? (vendorRequested && !session.NativeDecoderConfigured ? "DecoderApiUnavailable" : "CodecUnsupported") : null;
            var hardware = actual is "D3D11" or "IntelQsv" or "AmdAmf" or "NvidiaNvdec";
            Volatile.Write(ref _videoDecoding, new VideoDecodingSnapshot(session.HardwareRequested, decoder,
                fallback, codec, hardware ? native?.GpuName ?? gpu?.Name : null, hardware ? native?.VendorId ?? gpu?.VendorId : null,
                hardware ? native?.AdapterLuid ?? gpu?.AdapterLuid : null, session.RequestedDecoderApi, actual, reason,
                native?.CodecName, native?.DecodedFrameCount ?? 0, native?.HardwareFrameCount ?? 0));
        }
        catch
        {
            // Diagnostic availability must never interrupt playback or change the reported decoder to hardware.
        }
    }

    private sealed partial class Session
    {
        public bool HardwareRequested { get; init; }
        public string RequestedDecoderApi { get; init; } = "Auto";
        public bool NativeDecoderConfigured { get; set; }
        public FFmpegMediaSource? DecoderSource { get; set; }
        public DecoderDeviceInfo? DecoderGpu { get; set; }
        public HlsHttpRelay? HlsRelay { get; set; }
    }
}
