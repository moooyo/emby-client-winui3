using EmbyClient.Playback;
using FFmpegInteropX;
using Microsoft.Graphics.Display;
using Microsoft.UI;

namespace EmbyClient.App.Playback;

public enum HdrMetadataConfigurationState { Pending, Disabled, Unavailable, Configured }

/// <summary>
/// Reports display eligibility and native stream metadata. These observations do not verify screen output
/// or indicate that HDR-to-SDR tone mapping has been applied.
/// </summary>
public sealed record HdrPlaybackSnapshot(Guid PlaybackId, string RequestedMode, HdrMetadataConfigurationState State,
    bool? DisplayHdrActive = null, bool? DisplaySupportsHdr10 = null, bool NativeHdrMetadataEnabled = false,
    bool? StreamHasHdrMetadata = null, bool? StreamHdrMetadataApplied = null, string? UnavailableReason = null);

/// <summary>A current output eligibility check; it does not confirm that media has been rendered.</summary>
public sealed record HdrDisplayEligibilitySnapshot(string RequestedMode, bool Eligible, HdrMetadataConfigurationState State,
    bool? DisplayHdrActive = null, bool? DisplaySupportsHdr10 = null, bool NativeHdrApiAvailable = false,
    string? UnavailableReason = null);

public sealed partial class NativePlaybackEngine
{
    private HdrConfiguration _hdrConfiguration = new("Auto", 0);
    private HdrPlaybackSnapshot? _hdrOutput;

    public HdrPlaybackSnapshot? HdrOutput => Volatile.Read(ref _hdrOutput);

    /// <summary>Reads the current window capability on the player dispatcher before negotiating a source.</summary>
    public bool DisplayHdr10Eligible => HdrDisplayEligibility.Eligible;

    public HdrDisplayEligibilitySnapshot HdrDisplayEligibility
    {
        get
        {
            var configuration = Volatile.Read(ref _hdrConfiguration);
            if (!dispatcher.HasThreadAccess)
                return new(configuration.Mode, false, HdrMetadataConfigurationState.Unavailable,
                    UnavailableReason: "HdrDispatcherUnavailable");
            try
            {
                var nativeConfig = new MediaSourceConfig();
                var nativeAvailable = VendorDecoderInterop.TryConfigure(nativeConfig, Volatile.Read(ref _decoderApi));
                return EvaluateHdr10DisplayEligibility(configuration, nativeAvailable);
            }
            catch (Exception)
            {
                return new(configuration.Mode, false, HdrMetadataConfigurationState.Unavailable,
                    UnavailableReason: "NativeHdrApiUnavailable");
            }
        }
    }

    /// <summary>
    /// Configures HDR metadata for subsequent source openings. Both Auto and Always require Windows HDR
    /// to be active on the supplied window's display; this method does not change the display mode.
    /// </summary>
    public void ConfigureHdrOutput(string mode, ulong windowId = 0)
    {
        if (mode is not ("Auto" or "Always" or "Off")) throw new ArgumentOutOfRangeException(nameof(mode));
        Volatile.Write(ref _hdrConfiguration, new HdrConfiguration(mode, windowId));
    }

    private void BeginHdrConfiguration(Session session)
    {
        EnsureActive(session);
        var configuration = Volatile.Read(ref _hdrConfiguration);
        SetHdrSnapshot(session, new HdrPlaybackSnapshot(session.Request.PlaybackId, configuration.Mode,
            HdrMetadataConfigurationState.Pending));
    }

    private ulong ApplyHdrConfiguration(Session session, MediaSourceConfig config)
    {
        var configuration = Volatile.Read(ref _hdrConfiguration);
        session.AppliedHdrConfiguration = configuration;
        // Disabling HDR metadata is not a color conversion. Keep tone mapping separate from this policy.
        config.Video.HdrSupport = HdrSupport.Disabled;
        var eligibility = EvaluateHdr10DisplayEligibility(configuration, session.NativeDecoderConfigured);
        var snapshot = new HdrPlaybackSnapshot(session.Request.PlaybackId, configuration.Mode, eligibility.State,
            DisplayHdrActive: eligibility.DisplayHdrActive, DisplaySupportsHdr10: eligibility.DisplaySupportsHdr10,
            UnavailableReason: eligibility.UnavailableReason);
        if (eligibility.Eligible)
        {
            config.Video.HdrSupport = configuration.Mode == "Always" ? HdrSupport.Enabled : HdrSupport.Automatic;
            config.Video.VideoOutputAllow10bit = true;
            snapshot = snapshot with { NativeHdrMetadataEnabled = true };
        }
        SetHdrSnapshot(session, snapshot);
        // A display change after negotiation must take the existing fresh server-conversion fallback.
        if (session.Request.RequiresHdr10Output && !eligibility.Eligible) throw new PlaybackException("UnsupportedFormat");
        return eligibility.Eligible ? configuration.WindowId : 0;
    }

    private HdrDisplayEligibilitySnapshot EvaluateHdr10DisplayEligibility(HdrConfiguration configuration, bool nativeAvailable)
    {
        var snapshot = new HdrDisplayEligibilitySnapshot(configuration.Mode, false,
            configuration.Mode == "Off" ? HdrMetadataConfigurationState.Disabled : HdrMetadataConfigurationState.Unavailable,
            NativeHdrApiAvailable: nativeAvailable);
        if (configuration.WindowId == 0)
        {
            return snapshot with { UnavailableReason = "HdrWindowUnavailable" };
        }
        if (!nativeAvailable)
        {
            return snapshot with { UnavailableReason = "NativeHdrApiUnavailable" };
        }

        try
        {
            // This helper runs on the player dispatcher, as required by CreateForWindowId.
            using var display = DisplayInformation.CreateForWindowId(new WindowId { Value = configuration.WindowId });
            var color = display.GetAdvancedColorInfo();
            var hdrActive = color.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
            var supportsHdr10 = color.IsHdrMetadataFormatCurrentlySupported(DisplayHdrMetadataFormat.Hdr10);
            snapshot = snapshot with { DisplayHdrActive = hdrActive, DisplaySupportsHdr10 = supportsHdr10 };
            if (configuration.Mode == "Off") return snapshot with { UnavailableReason = "HdrOutputDisabled" };
            if (!hdrActive || !supportsHdr10)
            {
                return snapshot with
                {
                    UnavailableReason = hdrActive ? "Hdr10MetadataUnsupported" : "DisplayHdrDisabled"
                };
            }
            return snapshot with { Eligible = true, State = HdrMetadataConfigurationState.Configured };
        }
        catch (Exception)
        {
            // Display detection failure keeps negotiation and source opening on the conservative path.
            return snapshot with { UnavailableReason = "DisplayHdrInformationUnavailable" };
        }
    }

    private void VerifyDecodedHdr10Source(Session session, FFmpegMediaSource source)
    {
        var video = source.CurrentVideoStream;
        if (video is not null) RefreshHdrMetadataState(session, video);
        var hasHdrMetadata = video is not null && video.DecoderEngine != DecoderEngine.SystemDecoder && video.HasHdrMetadata;
        if (!session.Request.RequiresHdr10Output && !hasHdrMetadata) return;
        var configuration = Volatile.Read(ref _hdrConfiguration);
        var eligibility = EvaluateHdr10DisplayEligibility(configuration, session.NativeDecoderConfigured);
        var metadataApplied = hasHdrMetadata && video!.IsHdrActive;
        if (eligibility.Eligible && session.Request.Hdr10OutputAllowed
            && session.AppliedHdrConfiguration == configuration
            && (session.Request.Source.MediaStreams ?? []).Any(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase)
                && Hdr10PlaybackPolicy.IsExplicitHdr10(stream))
            && session.HdrSnapshot?.NativeHdrMetadataEnabled == true && metadataApplied
            && video!.CodecName.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)) return;

        if (session.HdrSnapshot is { } snapshot)
        {
            SetHdrSnapshot(session, snapshot with
            {
                State = HdrMetadataConfigurationState.Unavailable,
                DisplayHdrActive = eligibility.DisplayHdrActive,
                DisplaySupportsHdr10 = eligibility.DisplaySupportsHdr10,
                UnavailableReason = eligibility.UnavailableReason
                    ?? (session.AppliedHdrConfiguration != configuration ? "HdrConfigurationChanged" : "DeliveredHdr10MetadataUnavailable")
            });
        }
        // Neither suppressing PQ metadata nor reusing a copy-enabled URL is an HDR-to-SDR conversion.
        throw new PlaybackException("UnsupportedFormat");
    }

    private void VerifyCurrentHdr10Source(Session session)
    {
        if (session.DecoderSource is { } source) VerifyDecodedHdr10Source(session, source);
        else if (session.Request.RequiresHdr10Output) throw new PlaybackException("UnsupportedFormat");
    }

    private void RefreshHdrMetadataState(Session session, VideoStreamInfo video)
    {
        if (session.HdrSnapshot is not { } snapshot) return;
        try
        {
            // System decoders always expose false for these flags, which is not a reliable observation.
            var observable = video.DecoderEngine != DecoderEngine.SystemDecoder;
            SetHdrSnapshot(session, snapshot with
            {
                StreamHasHdrMetadata = observable ? video.HasHdrMetadata : null,
                StreamHdrMetadataApplied = observable ? video.IsHdrActive : null
            });
        }
        catch (Exception)
        {
            // Metadata diagnostics must not interfere with decoder observations or playback.
        }
    }

    private void SetHdrSnapshot(Session session, HdrPlaybackSnapshot snapshot)
    {
        session.HdrSnapshot = snapshot;
        if (ReferenceEquals(_current, session)) Volatile.Write(ref _hdrOutput, snapshot);
    }

    private sealed record HdrConfiguration(string Mode, ulong WindowId);

    private sealed partial class Session
    {
        public HdrPlaybackSnapshot? HdrSnapshot { get; set; }
        public HdrConfiguration? AppliedHdrConfiguration { get; set; }
    }
}
