using System.Text;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    /// <summary>Reports the versioned renderer API before negotiating a source or importing a full ASS script.</summary>
    public bool NativeAssRendererAvailable => dispatcher.HasThreadAccess && !_disposed && NativeAssInterop.IsAvailable;

    private async Task<NativePreparedSubtitle> PrepareLocalAssSubtitleCoreAsync(Session session, string fileName,
        byte[] data, CancellationToken cancellationToken)
    {
        // The caller tracks this task before retirement can drain pending preparations.
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var phase = "AssSubtitlePreparation/Decode";
        try
        {
            var text = await Task.Run(() => LocalSubtitleTextParser.Decode(data), lifetime.Token).ConfigureAwait(false);
            phase = "AssSubtitlePreparation/Validate";
            var count = await Task.Run(() =>
            {
                var utf8 = Encoding.UTF8.GetBytes(text);
                if (!NativeAssInterop.TryValidateDocument(utf8, out var events) || events is 0 or > MaximumSubtitleCues)
                    throw new PlaybackException("UnsupportedSubtitle");
                return events;
            }, lifetime.Token).ConfigureAwait(false);
            return await OnPlayerDispatcherAsync(() =>
            {
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                RecordSubtitleDiagnostic(FormattableString.Invariant($"Prepared libassEvents={count}"));
                // Preserve styles, drawing commands, animation, and encoded font sections as a complete document.
                return new NativePreparedSubtitle(session.Request.PlaybackId, fileName, string.Empty, count,
                    usesPlainTextPresentation: false, nativeStyledText: text, usesLibassPresentation: true);
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
    }

    private async Task LoadLocalAssSubtitleAsync(Guid playbackId, NativePreparedSubtitle prepared,
        CancellationToken cancellationToken)
    {
        if (!prepared.UsesLibassPresentation || prepared.NativeStyledText is null)
            throw new PlaybackException("UnsupportedSubtitle");
        cancellationToken.ThrowIfCancellationRequested();
        await _localSubtitleTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await OnPlayerDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId);
                if (session is null) return Task.CompletedTask;
                if (session.Request.SubtitleStreamIndex is not (null or -1) || session.Request.ExternalSubtitleUri is not null)
                    throw new PlaybackException("SubtitleSourceMustBeDisabled");
                if (session.DecoderSource is null || session.MediaSource is null || session.Player is null
                    || !NativeAssInterop.IsAvailable)
                    throw new PlaybackException("SubtitlePreferencesNotSupported");
                if (session.Request.TimelineOffsetTicks < 0)
                    throw new PlaybackException("UnsupportedSubtitle");
                var import = new LocalSubtitleImport { RecoverySubtitle = prepared };
                session.PendingLocalSubtitle = import;
                return session.LocalSubtitleLoadTask = LoadLocalAssSubtitleCoreAsync(session, import, prepared, cancellationToken);
            }).ConfigureAwait(false);
            await load.ConfigureAwait(false);
        }
        finally { _localSubtitleTransition.Release(); }
    }

    private async Task LoadLocalAssSubtitleCoreAsync(Session session, LocalSubtitleImport import,
        NativePreparedSubtitle prepared, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var phase = "AssSubtitleImport/Create";
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(prepared.NativeStyledText!);
            if (utf8.Length is 0 or > MaximumLocalSubtitleBytes) throw new PlaybackException("UnsupportedSubtitle");
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                var delayTicks = TimeSpan.FromSeconds(Volatile.Read(ref _subtitleDelaySeconds)).Ticks;
                var owner = NativeAssInterop.CreateLocalTrack(session.DecoderSource!, utf8, prepared.FileName,
                    session.Request.TimelineOffsetTicks, delayTicks);
                // Retain ownership before adding a track can synchronously reenter retirement.
                session.LocalAssSubtitles.Add(import, owner);
                import.NativeTrack = owner.Track;
                import.NativeDelayTicks = delayTicks;
                EnsureLocalSubtitleActive(session, import);
                ValidateLocalAssSubtitleTrack(session, import, owner.Track);
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
                owner.Track.TrackFailed += import.NativeTrackFailed;
                import.Attached = true;
                session.MediaSource!.ExternalTimedMetadataTracks.Add(owner.Track);
                import.Attached = true;
                EnsureLocalSubtitleActive(session, import);
                CompletePendingNativeSubtitleImport(session);
                return true;
            }).ConfigureAwait(false);
            phase = "AssSubtitleImport/Resolve";
            var tracks = await import.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            phase = "AssSubtitleImport/Commit";
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                CommitLocalSubtitle(session, import, tracks);
                EnsureLocalSubtitleActive(session, import);
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

    private static bool IsLocalAssSubtitle(Session session, LocalSubtitleImport import) =>
        session.LocalAssSubtitles.TryGetValue(import, out var owner) && owner.IsAlive;

    private static bool UsesLibassSubtitleStyles(Session session) => session.LocalSubtitle is { } local
        ? IsLocalAssSubtitle(session, local)
        : session.ExternalAssSubtitle is { IsAlive: true }
            || session.NativeEmbeddedSubtitle is { } embedded
                && embedded.SubtitleTrack.TimedMetadataKind == TimedMetadataKind.ImageSubtitle
                && NativeAssSubtitleCodec(embedded.CodecName) is not null;

    private async Task PrepareExternalAssSubtitleAsync(Session session, byte[] data, string codec,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var phase = "ExternalAssSubtitle/Validate";
        var installed = false;
        try
        {
            if (NativeAssSubtitleCodec(codec) is null || session.Request.ExternalSubtitleFormat != codec
                || !session.Request.NativeAssSubtitlesEligible || !NativeAssInterop.IsAvailable
                || data.Length is 0 or > MaximumLocalSubtitleBytes || session.Request.TimelineOffsetTicks < 0)
                throw new PlaybackException("UnsupportedSubtitle");
            var utf8 = await Task.Run(() =>
            {
                var bytes = Encoding.UTF8.GetBytes(LocalSubtitleTextParser.Decode(data));
                if (!NativeAssInterop.TryValidateDocument(bytes, out var events) || events is 0 or > MaximumSubtitleCues)
                    throw new PlaybackException("UnsupportedSubtitle");
                return bytes;
            }, lifetime.Token).ConfigureAwait(false);
            phase = "ExternalAssSubtitle/Attach";
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                if (session.DecoderSource is null || session.MediaSource is null || session.ExternalAssSubtitle is not null)
                    throw new PlaybackException("UnsupportedSubtitle");
                var delayTicks = TimeSpan.FromSeconds(Volatile.Read(ref _desiredSubtitleDelaySeconds)).Ticks;
                var owner = NativeAssInterop.CreateLocalTrack(session.DecoderSource, utf8,
                    codec == "ssa" ? "External.ssa" : "External.ass", session.Request.TimelineOffsetTicks, delayTicks);
                session.ExternalAssSubtitle = owner;
                session.ExternalAssDelayTicks = delayTicks;
                session.ExternalAssTrackFailed = (_, _) =>
                {
                    if (!IsActive(session) || !ReferenceEquals(session.ExternalAssSubtitle, owner)) return;
                    Volatile.Write(ref session.ExternalAssTrackHasFailed, true);
                    Queue(() =>
                    {
                        if (IsActive(session) && session.LocalSubtitle is null) Fail(session, "UnsupportedSubtitle");
                    });
                };
                owner.Track.TrackFailed += session.ExternalAssTrackFailed;
                session.ExternalAssAttached = true;
                session.MediaSource.ExternalTimedMetadataTracks.Add(owner.Track);
                session.ExternalAssAttached = true;
                EnsureActive(session);
                lifetime.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref session.ExternalAssTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
                session.ExternalTracks = [owner.Track];
                session.ExternalTextReady = true;
                installed = true;
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
            if (!installed) await OnDispatcherAsync(() => RetireExternalAssSubtitles(session)).ConfigureAwait(false);
        }
    }

    private void ApplyExternalAssSubtitleDelay(Session session, long delayTicks)
    {
        EnsureActive(session);
        if (session.ExternalAssSubtitle is not { IsAlive: true } owner || Volatile.Read(ref session.ExternalAssTrackHasFailed))
            throw new PlaybackException("SubtitlePreferencesNotSupported");
        if (session.ExternalAssDelayTicks == delayTicks) return;
        var previous = session.ExternalAssDelayTicks;
        try
        {
            owner.SetDelay(delayTicks);
            session.ExternalAssDelayTicks = delayTicks;
            RefreshAssSubtitles(session);
            if (Volatile.Read(ref session.ExternalAssTrackHasFailed)) throw new PlaybackException("UnsupportedSubtitle");
        }
        catch
        {
            if (IsActive(session) && owner.IsAlive)
            {
                try
                {
                    owner.SetDelay(previous ?? 0);
                    session.ExternalAssDelayTicks = previous;
                    RefreshAssSubtitles(session);
                }
                catch (Exception exception) { RecordSubtitleFailure("ExternalAssSubtitle/RestoreDelay", exception); }
            }
            throw;
        }
    }

    private static void ValidateLocalAssSubtitleTrack(Session session, LocalSubtitleImport import, TimedMetadataTrack track)
    {
        if (!session.LocalAssSubtitles.TryGetValue(import, out var owner) || !owner.IsAlive || track != owner.Track
            || track.TimedMetadataKind != TimedMetadataKind.ImageSubtitle || Volatile.Read(ref import.NativeTrackHasFailed))
            throw new PlaybackException("UnsupportedSubtitle");
        // The complete document was validated by libass. Image cues are generated only for actual video samples.
    }

    private bool TryApplyLocalAssSubtitleDelay(Session session, LocalSubtitleImport import, long delayTicks)
    {
        if (!session.LocalAssSubtitles.TryGetValue(import, out var owner)) return false;
        EnsureLocalSubtitleActive(session, import);
        if (import.NativeDelayTicks == delayTicks) return true;
        var previous = import.NativeDelayTicks;
        try
        {
            owner.SetDelay(delayTicks);
            import.NativeDelayTicks = delayTicks;
            if (import.Committed) RefreshAssSubtitles(session);
            EnsureLocalSubtitleActive(session, import);
            return true;
        }
        catch
        {
            if (IsActive(session) && owner.IsAlive)
            {
                try
                {
                    owner.SetDelay(previous ?? 0);
                    import.NativeDelayTicks = previous;
                    if (import.Committed) RefreshAssSubtitles(session);
                }
                catch (Exception exception) { RecordSubtitleFailure("AssSubtitleImport/RestoreDelay", exception); }
            }
            throw;
        }
    }

    private void RefreshAssSubtitles(Session session)
    {
        if (!IsActive(session) || session.DecoderSource is not { } source || session.NativeSession is not { } native
            || !NativeAssInterop.IsAvailable) return;
        // Zero retains the native driver's observed video-sample duration instead of inventing a media cadence.
        try
        {
            NativeAssInterop.Refresh(source, Math.Max(0, native.Position.Ticks), frameDurationTicks: 0);
            RefreshAssSubtitleFramePresentation(session);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            RecordSubtitleFailure("AssSubtitle/Refresh", exception);
            throw new PlaybackException("UnsupportedSubtitle");
        }
    }

    private bool HasFailedAssSubtitles(Session session) => IsActive(session) && session.DecoderSource is { } source
        && NativeAssInterop.TryReadFailure(source, out var failure) && failure < 0;

    private void ReleaseLocalAssSubtitle(Session session, LocalSubtitleImport import)
    {
        if (!session.LocalAssSubtitles.Remove(import, out var owner)) return;
        Release(session, "CloseLocalAssSubtitle", owner.Dispose);
    }

    private void RetireAssSubtitles(Session session)
    {
        RetireExternalAssSubtitles(session);
        foreach (var import in session.LocalAssSubtitles.Keys.ToArray()) ReleaseLocalAssSubtitle(session, import);
    }

    private void RetireExternalAssSubtitles(Session session)
    {
        var owner = session.ExternalAssSubtitle;
        session.ExternalAssSubtitle = null;
        var failed = session.ExternalAssTrackFailed;
        session.ExternalAssTrackFailed = null;
        var attached = session.ExternalAssAttached;
        session.ExternalAssAttached = false;
        if (owner is not null)
        {
            if (failed is not null) Release(session, "UnsubscribeExternalAssSubtitle", () => owner.Track.TrackFailed -= failed);
            if (attached)
            {
                if (session.Item is { } item)
                {
                    for (var index = item.TimedMetadataTracks.Count - 1; index >= 0; index--)
                    {
                        if (item.TimedMetadataTracks[index] != owner.Track) continue;
                        var trackIndex = checked((uint)index);
                        Release(session, "DisableExternalAssSubtitle", () =>
                            item.TimedMetadataTracks.SetPresentationMode(trackIndex, TimedMetadataTrackPresentationMode.Disabled));
                    }
                }
                if (session.MediaSource is { } media)
                    Release(session, "DetachExternalAssSubtitle", () => media.ExternalTimedMetadataTracks.Remove(owner.Track));
            }
            if (session.ExternalTracks.Length == 1 && session.ExternalTracks[0] == owner.Track)
                session.ExternalTracks = [];
            Release(session, "CloseExternalAssSubtitle", owner.Dispose);
        }
        session.ExternalAssDelayTicks = null;
        Volatile.Write(ref session.ExternalAssTrackHasFailed, false);
    }

    private sealed partial class Session
    {
        public Dictionary<LocalSubtitleImport, NativeAssTrackOwner> LocalAssSubtitles { get; } = [];
        public NativeAssTrackOwner? ExternalAssSubtitle { get; set; }
        public TypedEventHandler<TimedMetadataTrack, TimedMetadataTrackFailedEventArgs>? ExternalAssTrackFailed { get; set; }
        public bool ExternalAssTrackHasFailed;
        public bool ExternalAssAttached { get; set; }
        public long? ExternalAssDelayTicks { get; set; }
    }
}
