using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly PlaybackDiagnostics _diagnostics = new();
    private readonly object _diagnosticGate = new();
    private DiagnosticBinding? _currentDiagnostic;
    private DiagnosticBinding? _previousDiagnostic;
    private PlaybackDiagnosticsDialog? _diagnosticsDialog;

    public bool IsModalOpen => IsQueueOpen || _diagnosticsDialog is not null;

    public async Task ShowDiagnosticsAsync(XamlRoot xamlRoot, ElementTheme theme)
    {
        if (IsModalOpen) return;
        var dialog = new PlaybackDiagnosticsDialog(_diagnostics) { XamlRoot = xamlRoot, RequestedTheme = theme };
        _diagnosticsDialog = dialog;
        UpdateQueueControls();
        try { await dialog.ShowAsync(); }
        finally
        {
            if (ReferenceEquals(_diagnosticsDialog, dialog)) _diagnosticsDialog = null;
            UpdateQueueControls();
        }
    }

    private void BeginDiagnosticPlayback(long intent)
    {
        lock (_diagnosticGate) CreateDiagnosticBinding(intent);
    }

    private DiagnosticBinding CreateDiagnosticBinding(long intent)
    {
        _previousDiagnostic = _currentDiagnostic;
        _currentDiagnostic = new(intent, _diagnostics.CreateLocalPlaybackId());
        _diagnostics.Record(_currentDiagnostic.LocalId, PlaybackDiagnosticEvent.Created);
        return _currentDiagnostic;
    }

    private void RecordDiagnosticStatus(PlaybackNotificationOwner.Ticket ticket, PlaybackStatusChangedEventArgs args)
    {
        lock (_diagnosticGate)
        {
            if (!_notificationOwner.IsCurrent(ticket)) return;
            var coreId = args.Context?.PlaybackId ?? args.Recovery?.FailedPlaybackId;
            var binding = FindDiagnosticBinding(coreId);
            if (binding is null)
            {
                binding = _currentDiagnostic is { CoreId: null } pending && pending.Intent == ticket.Intent
                    ? pending : CreateDiagnosticBinding(ticket.Intent);
                binding.CoreId = coreId;
            }
            if (args.Context is { } context) binding.Media = DiagnosticMedia(context);
            var previous = binding.LastStatus;
            binding.LastStatus = args.Status;
            if (previous == args.Status) return;
            if (!binding.Started && args.Status is PlaybackStatus.Playing or PlaybackStatus.Paused)
            {
                binding.Started = true;
                _diagnostics.Record(binding.LocalId, PlaybackDiagnosticEvent.Started, media: binding.Media);
                if (args.Status == PlaybackStatus.Playing) return;
            }
            PlaybackDiagnosticEvent? name = args.Status switch
            {
                PlaybackStatus.Negotiating => PlaybackDiagnosticEvent.Negotiating,
                PlaybackStatus.Opening => PlaybackDiagnosticEvent.Opening,
                PlaybackStatus.Playing when previous == PlaybackStatus.Seeking => PlaybackDiagnosticEvent.Seeked,
                PlaybackStatus.Playing => PlaybackDiagnosticEvent.Resumed,
                PlaybackStatus.Paused => PlaybackDiagnosticEvent.Paused,
                PlaybackStatus.Seeking => PlaybackDiagnosticEvent.Seeking,
                PlaybackStatus.Idle => PlaybackDiagnosticEvent.Stopped,
                PlaybackStatus.Ended => PlaybackDiagnosticEvent.Ended,
                PlaybackStatus.Failed => PlaybackDiagnosticEvent.Failed,
                _ => null
            };
            if (name.HasValue) _diagnostics.Record(binding.LocalId, name.Value, DiagnosticError(args.ErrorCode), binding.Media);
        }
    }

    private DiagnosticBinding? FindDiagnosticBinding(Guid? coreId)
    {
        if (!coreId.HasValue) return null;
        if (_currentDiagnostic?.CoreId == coreId) return _currentDiagnostic;
        return _previousDiagnostic?.CoreId == coreId ? _previousDiagnostic : null;
    }

    private void RecordDiagnosticFailure(PlaybackDiagnosticEventArgs args, bool native)
    {
        lock (_diagnosticGate)
        {
            var binding = FindDiagnosticBinding(args.PlaybackId);
            if (binding is null) return;
            var error = DiagnosticError(args.ErrorCode);
            if (native && args.ErrorCode == "NativeReleaseFailed"
                || args.Operation is "EngineStop" or "EngineStopRetry" or "StopEncoding" or "CloseLiveStream")
            {
                error = PlaybackDiagnosticError.ResourceCleanup;
                binding.CleanupFailed = true;
            }
            _diagnostics.Record(binding.LocalId, args.Operation == "Fallback" ? PlaybackDiagnosticEvent.Failed
                : PlaybackDiagnosticEvent.ReportFailed, error, binding.Media);
        }
    }

    private void RecordDiagnosticRetirement(bool completed)
    {
        lock (_diagnosticGate)
        {
            if (_currentDiagnostic is not { } binding) return;
            var failed = binding.CleanupFailed || !completed;
            _diagnostics.Record(binding.LocalId, failed ? PlaybackDiagnosticEvent.Failed : PlaybackDiagnosticEvent.ResourceReleased,
                failed ? PlaybackDiagnosticError.ResourceCleanup : PlaybackDiagnosticError.None, binding.Media);
            _previousDiagnostic = null;
            _currentDiagnostic = null;
        }
    }

    private void EngineDiagnosticReceived(object? sender, PlaybackDiagnosticEventArgs args)
    {
        RecordDiagnosticFailure(args, native: true);
    }

    private void CoordinatorDiagnosticReceived(object? sender, PlaybackDiagnosticEventArgs args)
    {
        RecordDiagnosticFailure(args, native: false);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _coordinator) || args.Operation == "Fallback") return;
            if (args.ErrorCode == "AuthenticationExpired") { ReportExpiredSession(); return; }
            PlaybackNotice.Message = "A playback update could not be confirmed by the server. Check your connection if your progress is not saved.";
            PlaybackNotice.Severity = InfoBarSeverity.Warning;
            PlaybackNotice.IsOpen = true;
        });
    }

    private static PlaybackDiagnosticError DiagnosticError(string? code) => code switch
    {
        null => PlaybackDiagnosticError.None,
        "AuthenticationExpired" or "AuthenticationRequired" => PlaybackDiagnosticError.Authentication,
        "AccessRestricted" or "NotAllowed" or "NegotiationRejected" => PlaybackDiagnosticError.Permission,
        "ServerUnavailable" or "NetworkFailure" => PlaybackDiagnosticError.Network,
        "Timeout" or "SeekTimedOut" or "PauseNotConfirmed" => PlaybackDiagnosticError.Timeout,
        "InvalidServerResponse" or "UnknownTranscodeTimeline" => PlaybackDiagnosticError.Protocol,
        "UnsupportedTrack" or "UnsupportedFormat" or "UnsupportedSubtitle" or "NoCompatibleSource" or "NoCompatibleTranscode" => PlaybackDiagnosticError.UnsupportedMedia,
        "EngineStopFailed" or "NativeReleaseFailed" => PlaybackDiagnosticError.ResourceCleanup,
        "NativePlaybackFailure" or "EngineFailed" or "SystemMediaControlsUnavailable" => PlaybackDiagnosticError.NativePlayback,
        _ => PlaybackDiagnosticError.Unknown
    };

    private static PlaybackDiagnosticMedia DiagnosticMedia(PlaybackContext context)
    {
        var streams = context.Source.MediaStreams ?? [];
        var audioIndex = context.Selection.AudioStreamIndex ?? context.Source.DefaultAudioStreamIndex;
        var subtitleIndex = context.Selection.SubtitleStreamIndex ?? context.Source.DefaultSubtitleStreamIndex ?? -1;
        var video = streams.FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
        var audio = streams.FirstOrDefault(stream => stream.Index == audioIndex && string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        var hasAudio = streams.Any(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        var subtitle = streams.FirstOrDefault(stream => stream.Index == subtitleIndex && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
        // These are selected source characteristics, not an assertion about the server's encoded output.
        return PlaybackDiagnosticMedia.FromCategories(context.DeliveryMethod.ToString(), context.Source.Container,
            video is null ? "none" : video.Codec, audio?.Codec ?? (hasAudio ? null : "none"), subtitleIndex == -1 ? "none" : subtitle?.Codec);
    }

    private sealed class DiagnosticBinding(long intent, Guid localId)
    {
        public long Intent { get; } = intent;
        public Guid LocalId { get; } = localId;
        public Guid? CoreId { get; set; }
        public PlaybackStatus? LastStatus { get; set; }
        public bool Started { get; set; }
        public bool CleanupFailed { get; set; }
        public PlaybackDiagnosticMedia Media { get; set; } = new();
    }
}
