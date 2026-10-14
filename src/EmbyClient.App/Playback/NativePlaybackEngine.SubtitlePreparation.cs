using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using EmbyClient.Playback;
using FFmpegInteropX;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace EmbyClient.App.Playback;

/// <summary>Immutable local subtitle data that can be retained with an item's local subtitle choices.</summary>
public sealed class NativePreparedSubtitle
{
    internal NativePreparedSubtitle(Guid playbackId, string fileName, string webVtt, int cueCount, bool usesPlainTextPresentation,
        string? nativeStyledText = null, bool usesLibassPresentation = false)
    {
        PreparedForPlaybackId = playbackId;
        FileName = fileName;
        WebVtt = webVtt;
        CueCount = cueCount;
        UsesPlainTextPresentation = usesPlainTextPresentation;
        NativeStyledText = nativeStyledText;
        UsesLibassPresentation = usesLibassPresentation;
    }

    public Guid PreparedForPlaybackId { get; }
    public string FileName { get; }
    public int CueCount { get; }

    /// <summary>True when a styled source format is reduced to timed text by the application's presenter.</summary>
    public bool UsesPlainTextPresentation { get; }

    /// <summary>True when the original styles are rendered through a native text or image track.</summary>
    public bool UsesNativePresentation => NativeStyledText is not null;
    public bool UsesLibassPresentation { get; }

    internal string WebVtt { get; }
    internal string? NativeStyledText { get; }
}

public sealed partial class NativePlaybackEngine
{
    public static IReadOnlyList<string> SupportedLocalSubtitleExtensions { get; } =
        Array.AsReadOnly(new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" });

    public static bool IsSupportedLocalSubtitleExtension(string? fileName) => !string.IsNullOrWhiteSpace(fileName)
        && SupportedLocalSubtitleExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks the size and text-format signature before native parsing. Binary VobSub requires an index file
    /// and an image-caption presenter, so it is deliberately rejected instead of appearing to load successfully.
    /// </summary>
    public static void ValidateLocalSubtitleData(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsSupportedLocalSubtitleExtension(fileName) || data.Length is 0 or > MaximumLocalSubtitleBytes)
            throw new PlaybackException("UnsupportedSubtitle");
        string text;
        try { text = DecodeLocalSubtitleHeader(data); }
        catch (FormatException exception)
        {
            RecordSubtitleFailure("SubtitlePreparation/Decode", exception);
            throw new PlaybackException("UnsupportedSubtitle");
        }
        var supported = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".vtt" => text.StartsWith("WEBVTT", StringComparison.Ordinal)
                && (text.Length == 6 || char.IsWhiteSpace(text[6])),
            ".srt" => LocalSubRipHeader().IsMatch(text),
            ".ass" or ".ssa" => text.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase),
            ".sub" => LocalTextSubHeader().IsMatch(text),
            _ => false
        };
        if (!supported) throw new PlaybackException("UnsupportedSubtitle");
        if (Path.GetExtension(fileName).Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(fileName).Equals(".ssa", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (NativeAssInterop.IsAvailable)
                {
                    if (!NativeAssInterop.TryValidateDocument(Encoding.UTF8.GetBytes(text), out var events)
                        || events is 0 or > MaximumSubtitleCues) throw new FormatException("The native ASS document is invalid.");
                }
                else LocalAssSubtitlePolicy.Validate(text);
            }
            catch (FormatException exception)
            {
                RecordSubtitleFailure("SubtitlePreparation/AssPolicy", exception);
                throw new PlaybackException("UnsupportedSubtitle");
            }
        }
    }

    /// <summary>
    /// Parses without changing the playing subtitle. Call this before disabling a server-rendered subtitle;
    /// the returned immutable data can be attached after the coordinator has negotiated the replacement source.
    /// Only the supplied bytes are opened. No subtitle content is sent to the media server.
    /// </summary>
    public async Task<NativePreparedSubtitle> PrepareLocalSubtitleAsync(Guid playbackId, string fileName, byte[] data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length is 0 or > MaximumLocalSubtitleBytes) throw new PlaybackException("UnsupportedSubtitle");
        cancellationToken.ThrowIfCancellationRequested();
        var content = data.ToArray();
        ValidateLocalSubtitleData(fileName, content);
        var name = Path.GetFileName(fileName);
        await _localSubtitleTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preparation = await OnPlayerDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId) ?? throw new OperationCanceledException();
                var task = PrepareLocalSubtitleCoreAsync(session, name, content, cancellationToken);
                session.LocalSubtitlePreparationTask = task;
                return task;
            }).ConfigureAwait(false);
            return await preparation.ConfigureAwait(false);
        }
        finally { _localSubtitleTransition.Release(); }
    }

    public Task LoadLocalSubtitleAsync(Guid playbackId, NativePreparedSubtitle subtitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        return subtitle.UsesLibassPresentation ? LoadLocalAssSubtitleAsync(playbackId, subtitle, cancellationToken)
            : subtitle.NativeStyledText is not null
            ? LoadLocalStyledSubtitleAsync(playbackId, subtitle, cancellationToken)
            : LoadPreparedLocalTextSubtitleAsync(playbackId, subtitle, cancellationToken);
    }

    /// <summary>Loads a local text subtitle when server subtitles have already been disabled.</summary>
    public async Task LoadLocalSubtitleAsync(Guid playbackId, string fileName, byte[] data,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareLocalSubtitleAsync(playbackId, fileName, data, cancellationToken).ConfigureAwait(false);
        await LoadLocalSubtitleAsync(playbackId, prepared, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NativePreparedSubtitle> PrepareLocalSubtitleCoreAsync(Session session, string fileName, byte[] data,
        CancellationToken cancellationToken)
    {
        // Publish the task before cancellation can retire the source and drain outstanding native work.
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        InMemoryRandomAccessStream? stream = null;
        SubtitleParser? parser = null;
        var phase = "SubtitlePreparation/ParseText";
        try
        {
            var extension = Path.GetExtension(fileName);
            var nativeStyles = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase);
            if (nativeStyles && NativeAssInterop.IsAvailable)
                return await PrepareLocalAssSubtitleCoreAsync(session, fileName, data, lifetime.Token).ConfigureAwait(false);
            if (nativeStyles && (session.Request.TimelineKind != PlaybackTimelineKind.FullSource
                || session.Request.TimelineOffsetTicks != 0))
                throw new PlaybackException("UnsupportedSubtitle");
            if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                // Deterministic Unicode parsing avoids codec/character-conversion dependencies for common text files.
                var textDocument = await Task.Run(() => LocalSubtitleTextParser.Parse(fileName, data), lifetime.Token).ConfigureAwait(false);
                return await OnPlayerDispatcherAsync(() =>
                {
                    EnsureActive(session);
                    lifetime.Token.ThrowIfCancellationRequested();
                    RecordSubtitleDiagnostic(FormattableString.Invariant($"Prepared textCues={textDocument.CueCount}"));
                    return new NativePreparedSubtitle(session.Request.PlaybackId, fileName, textDocument.WebVtt,
                        textDocument.CueCount, usesPlainTextPresentation: false);
                }).ConfigureAwait(false);
            }
            phase = "SubtitlePreparation/NormalizeEncoding";
            // The bundled decoder has no iconv. Explicit UTF-8 prevents pure ASCII from entering its ANSI conversion path.
            var normalizedText = LocalSubtitleTextParser.Decode(data);
            var normalizedData = Encoding.UTF8.GetBytes(normalizedText);
            phase = "SubtitlePreparation/Stream";
            var writing = await OnPlayerDispatcherAsync(() =>
            {
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                stream = new InMemoryRandomAccessStream();
                return stream.WriteAsync(normalizedData.AsBuffer()).AsTask(lifetime.Token);
            }).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            phase = "SubtitlePreparation/ParseNative";
            IAsyncOperation<SubtitleParser>? operation = null;
            var parsing = await OnPlayerDispatcherAsync(() =>
            {
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                stream!.Seek(0);
                var descriptor = CreateSubtitleVideoDescriptor(session);
                var header = DecodeLocalSubtitleHeader(data);
                if (Path.GetExtension(fileName).Equals(".sub", StringComparison.OrdinalIgnoreCase)
                    && header.StartsWith('{') && descriptor is null && !LocalMicroDvdFrameRate().IsMatch(header))
                    throw new PlaybackException("SubtitleFrameRateUnavailable");
                var config = new MediaSourceConfig();
                config.Subtitles.AutoSelectForcedSubtitles = false;
                config.Subtitles.UseEmbeddedSubtitleFonts = false;
                config.Subtitles.DefaultSubtitleDelay = TimeSpan.Zero;
                config.Subtitles.ExternalSubtitleEncoding = CharacterEncoding.UTF8;
                operation = SubtitleParser.ReadSubtitleAsync(stream, fileName, config, descriptor);
                return operation.AsTask();
            }).ConfigureAwait(false);
            using var cancel = lifetime.Token.Register(() => Queue(() =>
            {
                try { operation?.Cancel(); }
                catch { }
            }));
            parser = await parsing.ConfigureAwait(false);
            phase = "SubtitlePreparation/CaptureCues";
            return await OnPlayerDispatcherAsync(() =>
            {
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                var prepared = nativeStyles
                    ? PrepareStyledSubtitleCues(session.Request.PlaybackId, fileName, normalizedText,
                        parser.SubtitleTrack.SubtitleTrack)
                    : PrepareSubtitleCues(session.Request.PlaybackId, fileName, parser.SubtitleTrack.SubtitleTrack);
                RecordSubtitleDiagnostic(FormattableString.Invariant($"Prepared nativeCues={prepared.CueCount}"));
                return prepared;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !session.Lifetime.IsCancellationRequested)
        { RecordSubtitleFailure(phase, exception); throw new PlaybackException("UnsupportedSubtitle"); }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException exception) { RecordSubtitleFailure(phase, exception); throw; }
        catch (Exception exception) { RecordSubtitleFailure(phase, exception); throw new PlaybackException("UnsupportedSubtitle"); }
        finally
        {
            await OnDispatcherAsync(() =>
            {
                if (parser is not null) Release(session, "CloseSubtitleParser", parser.Dispose);
                if (stream is not null) Release(session, "ClosePreparedSubtitleStream", stream.Dispose);
            }).ConfigureAwait(false);
        }
    }

    private static VideoStreamDescriptor? CreateSubtitleVideoDescriptor(Session session)
    {
        var video = session.DecoderSource?.CurrentVideoStream;
        if (video is null || video.PixelWidth <= 0 || video.PixelHeight <= 0
            || !double.IsFinite(video.FramesPerSecond) || video.FramesPerSecond is <= 0 or > 1000) return null;
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12,
            checked((uint)video.PixelWidth), checked((uint)video.PixelHeight));
        properties.FrameRate.Numerator = checked((uint)Math.Round(video.FramesPerSecond * 1_000_000));
        properties.FrameRate.Denominator = 1_000_000;
        properties.PixelAspectRatio.Numerator = 1;
        properties.PixelAspectRatio.Denominator = 1;
        return new VideoStreamDescriptor(properties);
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextCue))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextLine))]
    private static NativePreparedSubtitle PrepareSubtitleCues(Guid playbackId, string fileName, TimedMetadataTrack track)
    {
        if (track.Cues.Count is 0 or > MaximumSubtitleCues) throw new PlaybackException("UnsupportedSubtitle");
        var webVtt = new StringBuilder("WEBVTT\n\n");
        var count = 0;
        foreach (var cue in track.Cues)
        {
            if (cue is not TimedTextCue text) throw new PlaybackException("UnsupportedSubtitle");
            if (text.Duration.Ticks <= 0) continue;
            var content = string.Join("\n", text.Lines.Select(line => line.Text)).Replace("\r", "", StringComparison.Ordinal).Trim();
            if (content.Length == 0) continue;
            var start = Math.Max(0, text.StartTime.Ticks);
            if (start > long.MaxValue - text.Duration.Ticks) throw new PlaybackException("UnsupportedSubtitle");
            var end = start + text.Duration.Ticks;
            var startMilliseconds = start / TimeSpan.TicksPerMillisecond;
            var endMilliseconds = Math.Max(startMilliseconds + 1,
                end / TimeSpan.TicksPerMillisecond + (end % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1));
            webVtt.Append(FormatWebVttTimestamp(startMilliseconds)).Append(" --> ")
                .Append(FormatWebVttTimestamp(endMilliseconds)).Append('\n');
            foreach (var line in content.Split('\n'))
                webVtt.Append(line.Length == 0 ? "\u00a0" : EscapeWebVttText(line)).Append('\n');
            webVtt.Append('\n');
            count++;
            if (webVtt.Length > MaximumLocalSubtitleBytes) throw new PlaybackException("UnsupportedSubtitle");
        }
        var result = webVtt.ToString();
        if (count == 0 || Encoding.UTF8.GetByteCount(result) > MaximumLocalSubtitleBytes)
            throw new PlaybackException("UnsupportedSubtitle");
        return new NativePreparedSubtitle(playbackId, fileName, result, count, usesPlainTextPresentation: false);
    }

    private static NativePreparedSubtitle PrepareStyledSubtitleCues(Guid playbackId, string fileName, string source,
        TimedMetadataTrack track)
    {
        var count = ValidateStyledSubtitleCues(track);
        // No parser, font attachment, or WinRT cue escapes preparation. Loading owns a fresh native track.
        return new NativePreparedSubtitle(playbackId, fileName, string.Empty, count,
            usesPlainTextPresentation: false, nativeStyledText: source);
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextCue))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextLine))]
    private static int ValidateStyledSubtitleCues(TimedMetadataTrack track)
    {
        if (track.TimedMetadataKind != TimedMetadataKind.Subtitle || track.Cues.Count is 0 or > MaximumSubtitleCues)
            throw new PlaybackException("UnsupportedSubtitle");
        var count = 0;
        foreach (var cue in track.Cues)
        {
            if (cue is not TimedTextCue text) throw new PlaybackException("UnsupportedSubtitle");
            if (text.Duration.Ticks <= 0 || !text.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text))) continue;
            count++;
        }
        if (count == 0) throw new PlaybackException("UnsupportedSubtitle");
        return count;
    }

    private static string FormatWebVttTimestamp(long milliseconds)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    private static string EscapeWebVttText(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string DecodeLocalSubtitleHeader(byte[] data)
    {
        return LocalSubtitleTextParser.Decode(data).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
    }

    [GeneratedRegex(@"\A(?:\d+[ \t]*\r?\n[ \t]*)?\d{1,6}:\d{2}:\d{2}[,.]\d{1,3}[ \t]*-->[ \t]*\d{1,6}:\d{2}:\d{2}[,.]\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex LocalSubRipHeader();

    [GeneratedRegex(@"\A(?:\{\d+\}\{\d+\}|\[\d+\]\[\d+\]|\[INFORMATION\]|\[SUBTITLE\]|\d{1,3}:\d{2}:\d{2}\.\d{2,3},\d{1,3}:\d{2}:\d{2}\.\d{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalTextSubHeader();

    [GeneratedRegex(@"\A\{([01])\}\{\1\}[ \t]*\d+(?:\.\d+)?(?:[ \t]*\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalMicroDvdFrameRate();

    private sealed partial class Session
    {
        public Task LocalSubtitlePreparationTask { get; set; } = Task.CompletedTask;
    }
}
