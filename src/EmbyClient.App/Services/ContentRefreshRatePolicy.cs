namespace EmbyClient.App.Services;

/// <summary>Nominal video metadata for the currently owned playback, never a measured presentation cadence.</summary>
public sealed record ContentRefreshRatePlayback(Guid? PlaybackId, bool IsPlayingVideo, double? FramesPerSecond);

/// <summary>The window owner supplies a current fullscreen, preference, and playback snapshot.</summary>
public sealed record ContentRefreshRateRequest(
    nint WindowHandle, bool IsFullscreen, bool Enabled, ContentRefreshRatePlayback Playback);

public enum ContentRefreshRateState
{
    Disabled,
    Inactive,
    WaitingForFrameRate,
    Matched,
    Unsupported,
    ExternalChange,
    RestorePending,
    Closed
}

public enum ContentRefreshRateReason
{
    None,
    SettingDisabled,
    NotFullscreen,
    NoPlayingVideo,
    MissingFrameRate,
    PlatformUnavailable,
    MonitorUnavailable,
    CurrentModeUnavailable,
    NoCompatibleMode,
    ModeRejected,
    ChangeFailed,
    ReadbackFailed,
    ExternalDisplayChange,
    RestoreFailed,
    Closed
}

/// <summary>A matched state requires current-mode readback; ownership is retained until restoration succeeds.</summary>
public sealed record ContentRefreshRateStatus(
    ContentRefreshRateState State,
    ContentRefreshRateReason Reason = ContentRefreshRateReason.None,
    string? DisplayName = null,
    double? FramesPerSecond = null,
    uint? RefreshRate = null,
    bool HasOwnedMode = false,
    int? NativeResult = null);

/// <summary>Only effective display fields participate in ownership; driver-private bytes do not.</summary>
internal readonly record struct ContentDisplayMode(
    uint Width,
    uint Height,
    uint BitsPerPixel,
    uint DisplayFrequency,
    int X = 0,
    int Y = 0,
    uint Orientation = 0,
    uint DisplayFlags = 0,
    uint FixedOutput = 0)
{
    public bool HasSamePresentation(ContentDisplayMode other) =>
        Width == other.Width && Height == other.Height && BitsPerPixel == other.BitsPerPixel
        && Orientation == other.Orientation && DisplayFlags == other.DisplayFlags && FixedOutput == other.FixedOutput;
}

internal sealed record ContentDisplaySnapshot(string DisplayName, ContentDisplayMode Mode);

internal sealed record ContentRefreshRateOwnership(
    string DisplayName, ContentDisplayMode OriginalMode, ContentDisplayMode AppliedMode,
    Guid PlaybackId, double FramesPerSecond);

internal enum ContentRefreshRateRestoreDecision { Restore, AlreadyRestored, AbandonExternalChange }

/// <summary>Pure selection and restoration rules shared with the platform-independent tests.</summary>
internal static class ContentRefreshRatePolicy
{
    // Legacy DEVMODE refresh rates are integer Hz. This admits 24000/1001 -> 24
    // and 30000/1001 -> 30 without treating unrelated fractional multiples as matches.
    private const double NominalRateTolerance = .005;
    private const int MaximumIntegerMultiple = 10;

    public static bool IsValidFrameRate(double? framesPerSecond) =>
        framesPerSecond is { } value && double.IsFinite(value) && value is > 0 and <= 1000;

    public static bool IsEligible(ContentRefreshRateRequest request) => request.Enabled && request.IsFullscreen
        && request.Playback.PlaybackId.HasValue && request.Playback.IsPlayingVideo
        && IsValidFrameRate(request.Playback.FramesPerSecond);

    public static ContentDisplayMode? Select(
        ContentDisplayMode current, IReadOnlyList<ContentDisplayMode> supportedModes, double framesPerSecond)
    {
        var candidates = SelectCandidates(current, supportedModes, framesPerSecond);
        return candidates.Count == 0 ? null : candidates[0];
    }

    public static IReadOnlyList<ContentDisplayMode> SelectCandidates(
        ContentDisplayMode current, IReadOnlyList<ContentDisplayMode> supportedModes, double framesPerSecond)
    {
        if (!IsValidFrameRate(framesPerSecond) || current.Width == 0 || current.Height == 0
            || current.BitsPerPixel == 0) return [];

        var candidates = new List<(ContentDisplayMode Mode, int Multiple, double Error)>();
        var frequencies = new HashSet<uint>();
        // A read-back current mode is already available even if a driver omits it from enumeration.
        foreach (var mode in supportedModes.Append(current))
        {
            // Frequencies 0 and 1 mean the hardware default, not a reported refresh rate.
            if (mode.DisplayFrequency <= 1 || !current.HasSamePresentation(mode)) continue;
            var ratio = mode.DisplayFrequency / framesPerSecond;
            var rounded = Math.Round(ratio, MidpointRounding.AwayFromZero);
            if (rounded is < 1 or > MaximumIntegerMultiple) continue;
            var multiple = (int)rounded;
            var error = Math.Abs(ratio - multiple) / multiple;
            if (error > NominalRateTolerance || !frequencies.Add(mode.DisplayFrequency)) continue;
            // Enumeration positions are not desktop placement. Preserve the current window's display position.
            candidates.Add((current with { DisplayFrequency = mode.DisplayFrequency }, multiple, error));
        }
        return candidates.OrderBy(candidate => candidate.Multiple).ThenBy(candidate => candidate.Error)
            .Select(candidate => candidate.Mode).ToArray();
    }

    public static ContentRefreshRateRestoreDecision GetRestoreDecision(
        ContentRefreshRateOwnership ownership, ContentDisplayMode current) =>
        current == ownership.OriginalMode ? ContentRefreshRateRestoreDecision.AlreadyRestored
        : current == ownership.AppliedMode ? ContentRefreshRateRestoreDecision.Restore
        : ContentRefreshRateRestoreDecision.AbandonExternalChange;
}
