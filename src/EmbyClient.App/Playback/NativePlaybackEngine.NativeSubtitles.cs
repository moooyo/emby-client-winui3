using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using EmbyClient.Playback;
using FFmpegInteropX;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    /// <summary>Retains a bitmap decoder's known composition across Off and selection of that same track.</summary>
    public async Task<bool> TrySelectEmbeddedBitmapSubtitleAsync(Guid playbackId, int subtitleStreamIndex,
        CancellationToken cancellationToken = default)
    {
        if (subtitleStreamIndex < -1) throw new ArgumentOutOfRangeException(nameof(subtitleStreamIndex));
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeAssInterop.IsBitmapSelectionAvailable) return false;
        await WaitForAudioRecoveryAsync(playbackId, cancellationToken).ConfigureAwait(false);
        await _localSubtitleTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OnPlayerDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId);
                if (session is not { IsOpened: true, UserSeeking: false, InitialSeekPending: false,
                    LocalSubtitle: null, PendingLocalSubtitle: null, ExternalAssSubtitle: null,
                    DecoderSource: not null, NativeSession: not null, Item: not null,
                    NativeEmbeddedSubtitle: { } retained }
                    || NativeBitmapSubtitleCodec(retained.CodecName) is null
                    || subtitleStreamIndex != -1 && subtitleStreamIndex != retained.StreamIndex
                    || Snapshot?.State is not (PlaybackEngineState.Playing or PlaybackEngineState.Paused))
                    return false;
                var mappedRequest = session.Request with { SubtitleStreamIndex = retained.StreamIndex };
                if (!mappedRequest.NativeEmbeddedSubtitlesEligible || !IsNativeEmbeddedSubtitleRequest(mappedRequest)
                    || session.Item.TimedMetadataTracks.Count(track => track == retained.SubtitleTrack) != 1)
                    return false;
                EnsureActive(session);
                cancellationToken.ThrowIfCancellationRequested();
                // Invalidate any pending upload even when the selected cue has identical pixels. The
                // retained native track is source-scoped; no bitmap crosses a decoder or playback ID.
                StopAssSubtitleFramePresentation(session);
                session.Request = session.Request with { SubtitleStreamIndex = subtitleStreamIndex };
                ApplySubtitles(session, requireSelectedTrack: subtitleStreamIndex != -1);
                EnsureActive(session);
                Publish(session, PlaybackEngineEventKind.StateChanged);
                return true;
            }).ConfigureAwait(false);
        }
        finally { _localSubtitleTransition.Release(); }
    }

    /// <summary>
    /// Reports the native subtitle API before negotiation on the player dispatcher. A source still needs
    /// an exact, unique stream mapping; this capability does not depend on the Windows HDR display mode.
    /// </summary>
    public bool NativeEmbeddedSubtitlesEligible => dispatcher.HasThreadAccess && !_disposed
        && HdrDisplayEligibility.NativeHdrApiAvailable && NativeAssInterop.IsFrameBridgeAvailable;

    /// <summary>Checks the actual libass renderer independently of decoder API and display eligibility.</summary>
    public bool NativeAssSubtitlesEligible => dispatcher.HasThreadAccess && !_disposed && NativeAssInterop.IsAvailable;

    private void PrepareNativeEmbeddedSubtitles(Session session, FFmpegMediaSource decoder)
    {
        EnsureActive(session);
        if (!IsNativeEmbeddedSubtitleRequest(session.Request)) return;
        try
        {
            var index = session.Request.SubtitleStreamIndex!.Value;
            var expected = (session.Request.Source.MediaStreams ?? []).Single(stream => stream.Index == index
                && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
            var expectedCodec = NativeEmbeddedSubtitleCodec(expected.Codec);
            if (NativeAssSubtitleCodec(expected.Codec) is not null
                ? !NativeAssInterop.IsAvailable : !session.NativeDecoderConfigured)
                throw new PlaybackException("UnsupportedSubtitle");
            var candidates = decoder.SubtitleStreams.Where(stream => stream.StreamIndex == index).ToArray();
            if (candidates.Length != 1 || candidates[0].IsExternal
                || NativeEmbeddedSubtitleCodec(candidates[0].CodecName) != expectedCodec
                || candidates[0].SubtitleTrack is not { TimedMetadataKind: TimedMetadataKind.ImageSubtitle } track)
                throw new PlaybackException("UnsupportedSubtitle");

            session.NativeEmbeddedSubtitle = candidates[0];
            session.NativeEmbeddedTrackFailed = (_, _) =>
            {
                if (!IsActive(session)) return;
                Volatile.Write(ref session.NativeEmbeddedTrackHasFailed, true);
                Queue(() =>
                {
                    if (IsActive(session) && session.LocalSubtitle is null
                        && session.Request.SubtitleStreamIndex == index) Fail(session, "UnsupportedSubtitle");
                });
            };
            track.TrackFailed += session.NativeEmbeddedTrackFailed;
            EnsureActive(session);
            if (Volatile.Read(ref session.NativeEmbeddedTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch (Exception exception)
        {
            RecordSubtitleFailure("NativeSubtitle/Mapping", exception);
            throw new PlaybackException("UnsupportedSubtitle");
        }
    }

    private static bool IsNativeEmbeddedSubtitleRequest(PlaybackEngineRequest request)
    {
        if (!(request.NativeEmbeddedSubtitlesEligible || request.NativeAssSubtitlesEligible)
            || request.SubtitleStreamIndex is not (>= 0)
            || request.ExternalSubtitleUri is not null || request.DeliveryMethod != PlaybackDeliveryMethod.DirectStream
            || request.TimelineKind != PlaybackTimelineKind.FullSource || request.TimelineOffsetTicks != 0)
            return false;
        var selected = (request.Source.MediaStreams ?? []).Where(stream => stream.Index == request.SubtitleStreamIndex
            && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length != 1 || selected[0].IsExternal != false)
            return false;
        var selectedCodec = NativeEmbeddedSubtitleCodec(selected[0].Codec);
        if (selectedCodec is null || (NativeAssSubtitleCodec(selected[0].Codec) is not null
            ? !request.NativeAssSubtitlesEligible : !request.NativeEmbeddedSubtitlesEligible)) return false;

        // Negotiation identifies an original source. Do not enable an embedded track for a remuxed timeline.
        var staticValues = request.MediaUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => Uri.UnescapeDataString(pair[0]).Equals("Static", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty).ToArray();
        return staticValues.Length == 1 && (staticValues[0] == "1"
            || staticValues[0].Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NativeBitmapSubtitleCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        "pgs" or "pgssub" or "hdmv_pgs_subtitle" => "pgssub",
        "vobsub" or "dvdsub" or "dvd_subtitle" => "dvdsub",
        _ => null
    };

    private static string? NativeAssSubtitleCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        "ass" => "ass",
        "ssa" => "ssa",
        _ => null
    };

    private static string? NativeEmbeddedSubtitleCodec(string? codec) =>
        NativeBitmapSubtitleCodec(codec) ?? NativeAssSubtitleCodec(codec);

    private static TimedMetadataTrack? NativePlatformSubtitleTrack(Session session)
    {
        if (session.LocalSubtitle is { } local)
        {
            if (Volatile.Read(ref local.NativeTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
            return local.NativeTrack;
        }
        if (session.ExternalAssSubtitle is { } external)
        {
            if (!external.IsAlive || Volatile.Read(ref session.ExternalAssTrackHasFailed))
                throw new PlaybackException("UnsupportedSubtitle");
            return external.Track;
        }
        if (session.Request.SubtitleStreamIndex == -1) return null;
        if (Volatile.Read(ref session.NativeEmbeddedTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
        return session.NativeEmbeddedSubtitle?.SubtitleTrack;
    }

    private async Task LoadLocalStyledSubtitleAsync(Guid playbackId, NativePreparedSubtitle prepared,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _localSubtitleTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await OnPlayerDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId);
                if (session is null) return Task.CompletedTask;
                if (session.Request.SubtitleStreamIndex is not (null or -1))
                    throw new PlaybackException("SubtitleSourceMustBeDisabled");
                if (session.MediaSource is null || session.Player is null)
                    throw new PlaybackException("SubtitlePreferencesNotSupported");
                // The native parser cannot safely clip a whole item's past cues into a trimmed source.
                if (session.Request.TimelineKind != PlaybackTimelineKind.FullSource || session.Request.TimelineOffsetTicks != 0)
                    throw new PlaybackException("UnsupportedSubtitle");
                var import = new LocalSubtitleImport { RecoverySubtitle = prepared };
                session.PendingLocalSubtitle = import;
                return session.LocalSubtitleLoadTask = LoadLocalStyledSubtitleCoreAsync(session, import, prepared, cancellationToken);
            }).ConfigureAwait(false);
            await load.ConfigureAwait(false);
        }
        finally { _localSubtitleTransition.Release(); }
    }

    private async Task LoadLocalStyledSubtitleCoreAsync(Session session, LocalSubtitleImport import,
        NativePreparedSubtitle prepared, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var phase = "NativeSubtitle/Stream";
        try
        {
            var data = Encoding.UTF8.GetBytes(prepared.NativeStyledText!);
            var writing = await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                import.Stream = new InMemoryRandomAccessStream();
                return import.Stream.WriteAsync(data.AsBuffer()).AsTask(lifetime.Token);
            }).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            phase = "NativeSubtitle/Parse";
            IAsyncOperation<SubtitleParser>? operation = null;
            var parsing = await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                import.Stream!.Seek(0);
                var config = new MediaSourceConfig();
                config.Subtitles.AutoSelectForcedSubtitles = false;
                config.Subtitles.OverrideSubtitleStyles = false;
                config.Subtitles.UseEmbeddedSubtitleFonts = false;
                config.Subtitles.DefaultSubtitleDelay = TimeSpan.Zero;
                config.Subtitles.ExternalSubtitleEncoding = CharacterEncoding.UTF8;
                operation = SubtitleParser.ReadSubtitleAsync(import.Stream, prepared.FileName, config,
                    CreateSubtitleVideoDescriptor(session));
                // Await native completion even after cancellation so a late parser is still owned and disposed.
                return operation.AsTask();
            }).ConfigureAwait(false);
            using var cancel = lifetime.Token.Register(() => Queue(() =>
            {
                try { operation?.Cancel(); }
                catch { }
            }));
            var parser = await parsing.ConfigureAwait(false);
            phase = "NativeSubtitle/Attach";
            await OnDispatcherAsync(() =>
            {
                import.Parser = parser;
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                var info = parser.SubtitleTrack;
                if (!info.IsExternal || info.SubtitleTrack.TimedMetadataKind != TimedMetadataKind.Subtitle)
                    throw new PlaybackException("UnsupportedSubtitle");
                import.NativeTrack = info.SubtitleTrack;
                ValidateStyledSubtitleCues(import.NativeTrack);
                import.NativeTrackFailed = (_, _) =>
                {
                    if (!IsActive(session) || import.Detached) return;
                    Volatile.Write(ref import.NativeTrackHasFailed, true);
                    Queue(() =>
                    {
                        if (!IsActive(session) || import.Detached) return;
                        if (import.Committed && ReferenceEquals(session.LocalSubtitle, import))
                            Fail(session, "UnsupportedSubtitle");
                        else import.Completion.TrySetException(new PlaybackException("UnsupportedSubtitle"));
                    });
                };
                import.NativeTrack.TrackFailed += import.NativeTrackFailed;
                ApplyLocalNativeSubtitleDelay(session, import, TimeSpan.FromSeconds(_subtitleDelaySeconds).Ticks);
                import.Attached = true;
                session.MediaSource!.ExternalTimedMetadataTracks.Add(import.NativeTrack);
                import.Attached = true;
                EnsureLocalSubtitleActive(session, import);
                CompletePendingNativeSubtitleImport(session);
            }).ConfigureAwait(false);
            phase = "NativeSubtitle/Resolve";
            var tracks = await import.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            phase = "NativeSubtitle/Commit";
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                CommitLocalSubtitle(session, import, tracks);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !session.Lifetime.IsCancellationRequested)
        {
            RecordSubtitleFailure(phase, exception);
            throw new PlaybackException("UnsupportedSubtitle");
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException exception) { RecordSubtitleFailure(phase, exception); throw; }
        catch (Exception exception) { RecordSubtitleFailure(phase, exception); throw new PlaybackException("UnsupportedSubtitle"); }
        finally
        {
            await OnDispatcherAsync(() =>
            {
                if (!import.Committed) ReleaseLocalSubtitle(session, import, closeStream: true);
                if (ReferenceEquals(session.PendingLocalSubtitle, import)) session.PendingLocalSubtitle = null;
            }).ConfigureAwait(false);
        }
    }

    private static void CompletePendingNativeSubtitleImport(Session session)
    {
        if (session.PendingLocalSubtitle is not { Detached: false, NativeTrack: { } track } import || session.Item is null)
            return;
        // A previously completed resolution cannot consume a later native failure before commit observes it.
        if (Volatile.Read(ref import.NativeTrackHasFailed))
        {
            import.Completion.TrySetException(new PlaybackException("UnsupportedSubtitle"));
            return;
        }
        var count = session.Item.TimedMetadataTracks.Count(candidate => candidate == track);
        if (count == 1) import.Completion.TrySetResult([track]);
        else if (count > 1) import.Completion.TrySetException(new PlaybackException("UnsupportedSubtitle"));
    }

    private void ApplyNativeSubtitleDelay(Session session, double delaySeconds)
    {
        var delayTicks = TimeSpan.FromSeconds(delaySeconds).Ticks;
        if (session.LocalSubtitle is { } local)
        {
            ApplyLocalNativeSubtitleDelay(session, local, delayTicks);
            return;
        }
        if (session.ExternalAssSubtitle is not null)
        {
            ApplyExternalAssSubtitleDelay(session, delayTicks);
            return;
        }
        if (session.NativeEmbeddedSubtitle is not { } stream || session.DecoderSource is not { } decoder)
            throw new PlaybackException("SubtitlePreferencesNotSupported");
        if (Volatile.Read(ref session.NativeEmbeddedTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
        if (session.NativeEmbeddedDelayTicks == delayTicks) return;
        var previous = session.NativeEmbeddedDelayTicks;
        session.NativeEmbeddedDelayTicks = delayTicks;
        try
        {
            decoder.SetStreamDelay(stream, TimeSpan.FromTicks(delayTicks));
            if (NativeAssSubtitleCodec(stream.CodecName) is not null) RefreshAssSubtitles(session);
        }
        catch (Exception exception)
        {
            if (NativeAssSubtitleCodec(stream.CodecName) is not null && IsActive(session)
                && ReferenceEquals(session.DecoderSource, decoder))
            {
                try
                {
                    decoder.SetStreamDelay(stream, TimeSpan.FromTicks(previous ?? 0));
                    RefreshAssSubtitles(session);
                }
                catch (Exception restoreFailure) { RecordSubtitleFailure("NativeSubtitle/RestoreDelay", restoreFailure); }
            }
            if (session.NativeEmbeddedDelayTicks == delayTicks) session.NativeEmbeddedDelayTicks = previous;
            RecordSubtitleFailure("NativeSubtitle/Delay", exception);
            throw new PlaybackException("SubtitlePreferencesNotSupported");
        }
    }

    private void ApplyLocalNativeSubtitleDelay(Session session, LocalSubtitleImport import, long delayTicks)
    {
        if (TryApplyLocalAssSubtitleDelay(session, import, delayTicks)) return;
        // Styled local tracks are admitted only on a full source timeline.
        if (session.Request.TimelineKind != PlaybackTimelineKind.FullSource || session.Request.TimelineOffsetTicks != 0)
            throw new PlaybackException("UnsupportedSubtitle");
        var nativeDelay = delayTicks;
        if (import.NativeDelayTicks == nativeDelay) return;
        var previous = import.NativeDelayTicks;
        import.NativeDelayTicks = nativeDelay;
        try { import.Parser!.SetStreamDelay(TimeSpan.FromTicks(nativeDelay)); }
        catch
        {
            if (import.NativeDelayTicks == nativeDelay) import.NativeDelayTicks = previous;
            throw;
        }
    }

    private void RetireNativeEmbeddedSubtitles(Session session)
    {
        if (session.NativeEmbeddedSubtitle is { } stream && session.NativeEmbeddedTrackFailed is { } failed)
            Release(session, "UnsubscribeNativeSubtitleFailure", () => stream.SubtitleTrack.TrackFailed -= failed);
        session.NativeEmbeddedSubtitle = null;
        session.NativeEmbeddedTrackFailed = null;
        session.NativeEmbeddedDelayTicks = null;
        Volatile.Write(ref session.NativeEmbeddedTrackHasFailed, false);
        session.NativeSubtitlePresentationActive = false;
    }

    private sealed partial class Session
    {
        public SubtitleStreamInfo? NativeEmbeddedSubtitle { get; set; }
        public TypedEventHandler<TimedMetadataTrack, TimedMetadataTrackFailedEventArgs>? NativeEmbeddedTrackFailed { get; set; }
        public long? NativeEmbeddedDelayTicks { get; set; }
        public bool NativeEmbeddedTrackHasFailed;
        public bool NativeSubtitlePresentationActive { get; set; }
    }
}
