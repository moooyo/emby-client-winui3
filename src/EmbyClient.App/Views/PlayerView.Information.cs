using System.Globalization;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private void PlaybackInfoClicked(object sender, RoutedEventArgs args) => TogglePlaybackInfo();

    private void TogglePlaybackInfo()
    {
        if (IsModalOpen) return;
        _playbackInfoOpen = !_playbackInfoOpen;
        if (_playbackInfoOpen)
        {
            _openQuickFlyout?.Hide();
            CloseSidePanel(restoreFocus: false);
            UpdatePlaybackInformation();
            PlaybackInfoPanel.Visibility = Visibility.Visible;
            ClosePlaybackInfoButton.Focus(FocusState.Programmatic);
        }
        else
        {
            PlaybackInfoPanel.Visibility = Visibility.Collapsed;
            ResetServerPlaybackInformation();
            VideoFocusTarget.Focus(FocusState.Programmatic);
        }
        UpdatePlayerLayout();
        RevealControls();
    }

    private void ClosePlaybackInformation(bool restoreFocus = true)
    {
        if (!_playbackInfoOpen) return;
        _playbackInfoOpen = false;
        ResetServerPlaybackInformation();
        PlaybackInfoPanel.Visibility = Visibility.Collapsed;
        if (restoreFocus && VideoFocusTarget.IsLoaded) VideoFocusTarget.Focus(FocusState.Programmatic);
        ScheduleChromeAutoHide();
    }

    private void UpdatePlaybackInformation()
    {
        if (!_playbackInfoOpen) return;
        var context = _coordinator?.ActiveContext ?? _lastPlaybackContext;
        var rows = new List<(string Name, string Value)>();
        var unknown = LumenText.Get("Unavailable");
        if (context is null)
        {
            rows.Add((LumenText.Get("State"), LumenText.Get("No active playback")));
        }
        else
        {
            RefreshServerPlaybackInformation(context);
            var source = context.Source;
            var serverTranscoding = CurrentServerTranscodingInformation(context);
            var streams = source.MediaStreams ?? [];
            var video = streams.FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
            var audioIndex = context.Selection.AudioStreamIndex ?? source.DefaultAudioStreamIndex;
            var subtitleIndex = context.Selection.SubtitleStreamIndex ?? source.DefaultSubtitleStreamIndex;
            var audio = streams.FirstOrDefault(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase)
                && stream.Index == audioIndex)
                ?? streams.FirstOrDefault(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));
            var subtitles = streams.FirstOrDefault(stream => string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
                && stream.Index == subtitleIndex);
            var direct = context.DeliveryMethod == PlaybackDeliveryMethod.DirectStream;
            var engineSnapshot = _engine?.Snapshot;
            var currentEngine = engineSnapshot?.PlaybackId == context.PlaybackId;
            var decoding = currentEngine ? _engine?.VideoDecoding : null;
            var availableStatistics = _engine?.PlaybackStatistics;
            var statistics = currentEngine && availableStatistics?.PlaybackId == context.PlaybackId
                ? availableStatistics : null;
            var width = currentEngine ? _engine?.VideoWidth ?? 0 : 0;
            var height = currentEngine ? _engine?.VideoHeight ?? 0 : 0;
            var resolution = width > 0 && height > 0 ? $"{width} × {height}"
                : direct && video?.Width is > 0 && video.Height is > 0 ? $"{video.Width} × {video.Height}" : unknown;
            var frameRate = statistics?.NativeVideoFramesPerSecond
                ?? (direct ? video?.AverageFrameRate ?? video?.RealFrameRate : serverTranscoding?.Framerate);
            var decodedCodec = decoding is { ActualApi: not "Pending", Codec: not "Unknown" } ? decoding.Codec : null;
            rows.Add((LumenText.Get("Playback method"), LumenText.Get(direct ? "Direct play" : "Transcoding")
                + " · " + LumenText.Get(IsLocalServer(_session?.Api.ApiRoot) ? "Local network" : "Internet")));
            rows.Add((LumenText.Get("Container"), (direct ? source.Container : serverTranscoding?.Container ?? source.TranscodingContainer)?.ToUpperInvariant() ?? unknown));
            rows.Add((LumenText.Get("Video"), string.Join(" · ", new[]
            {
                direct ? video?.Codec?.ToUpperInvariant() : decodedCodec?.ToUpperInvariant() ?? serverTranscoding?.VideoCodec?.ToUpperInvariant(), resolution,
                frameRate is > 0 ? frameRate.Value.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)))));
            rows.Add((LumenText.Get("Decoder"), decoding is null ? unknown
                : string.Join(" · ", new[] { DecoderApiName(decoding.ActualApi), decoding.NativeDecoderName, decoding.GpuName }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))));
            var hdr = currentEngine ? _engine?.HdrOutput : null;
            var activeEngine = engineSnapshot?.State is PlaybackEngineState.Playing or PlaybackEngineState.Paused
                or PlaybackEngineState.Buffering or PlaybackEngineState.Seeking;
            rows.Add((LumenText.Get("HDR output"), hdr?.PlaybackId != context.PlaybackId || !activeEngine ? unknown
                : hdr.NativeHdrMetadataEnabled && hdr.StreamHasHdrMetadata == true && hdr.StreamHdrMetadataApplied == true
                    ? LumenText.Get("HDR10 · native output metadata active")
                    : hdr.StreamHasHdrMetadata == false ? LumenText.Get("No HDR metadata in the current stream")
                        : unknown));
            rows.Add((LumenText.Get("Decoded frames"), decoding?.HasFrameStatistics != true ? unknown
                : LumenText.Get("{0} total · {1} hardware", decoding.DecodedFrames.ToString("N0"), decoding.HardwareDecodedFrames.ToString("N0"))));
            // The current native decoder interfaces expose no dropped-frame count.
            rows.Add((LumenText.Get("Dropped frames"), unknown));
            rows.Add((LumenText.Get("Bitrate"), PlaybackBitrateInformation(context, video)));
            rows.Add((LumenText.Get("Buffer"), activeEngine && statistics?.BufferedDuration is { } bufferedDuration
                ? LumenText.Get("{0} s · buffered ahead", bufferedDuration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                : engineSnapshot?.State == PlaybackEngineState.Buffering
                && statistics?.BufferingProgress is { } progress
                ? LumenText.Get("{0}% · native buffering progress", (progress * 100).ToString("0", CultureInfo.InvariantCulture))
                : unknown));
            var audioInformation = TrackInformation(audio, LumenText.Get("No audio track"));
            var availableAudioOutput = currentEngine ? _engine?.AudioOutput : null;
            var audioOutput = availableAudioOutput?.PlaybackId == context.PlaybackId && activeEngine
                ? availableAudioOutput : null;
            rows.Add((LumenText.Get("Audio"), !direct && serverTranscoding?.AudioCodec is { Length: > 0 } audioCodec
                ? LumenText.Get("{0} · server audio stream", audioCodec.ToUpperInvariant())
                : audioOutput is { State: AudioOutputState.Pending or AudioOutputState.Bitstream }
                    ? AudioOutputText("{0} · compressed audio track", audioInformation)
                : audio is null ? audioInformation : LumenText.Get(direct
                    ? "{0} · decoded audio" : "{0} · source audio track", audioInformation)));
            AddActualAudioOutputInformation(rows, audioOutput);
            rows.Add((LumenText.Get("Subtitles"), _localSubtitleName is { } localName ? LocalSubtitleDisplayName(localName)
                : (subtitleIndex == -1
                ? LumenText.Get("Off") : TrackInformation(subtitles, LumenText.Get("Server default")))));
            rows.Add((LumenText.Get("Position"), FormatTime(context.PositionTicks) + " / "
                + (source.RunTimeTicks is > 0 ? FormatTime(source.RunTimeTicks.Value) : LumenText.Get("Live"))));
            rows.Add((LumenText.Get("Server"), _session?.Server.ServerName ?? unknown));
        }
        _playbackInfoCopy = string.Join(Environment.NewLine, rows.Select(row => $"{row.Name}: {row.Value}"));
        if (_playbackInfoValues.SequenceEqual(rows)) return;
        _playbackInfoValues = rows;
        PlaybackInfoRows.Children.Clear();
        PlaybackInfoRows.RowDefinitions.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            PlaybackInfoRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = rows[index].Name, FontSize = 12, FontFamily = LumenTheme.MonoFont,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)) };
            var value = new TextBlock { Text = rows[index].Value, FontSize = 12, FontFamily = LumenTheme.MonoFont, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.White), IsTextSelectionEnabled = true };
            Grid.SetRow(name, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            PlaybackInfoRows.Children.Add(name);
            PlaybackInfoRows.Children.Add(value);
        }
    }

    private static string TrackInformation(MediaStream? stream, string fallback) => stream is null ? fallback
        : stream.DisplayTitle ?? stream.Title ?? string.Join(" · ", new[] { stream.Language, stream.Codec?.ToUpperInvariant() }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static void AddActualAudioOutputInformation(List<(string Name, string Value)> rows,
        AudioOutputSnapshot? output)
    {
        var unavailable = LumenText.Get("Unavailable");
        var result = output is null ? unavailable : output.State == AudioOutputState.Off
            ? AudioOutputText("No audio output") : output.IsBitstream
            ? AudioOutputText("{0} · exclusive WASAPI bitstream", output.OutputFormat ?? output.SourceCodec)
            : output.State is AudioOutputState.Pending or AudioOutputState.Bitstream
                ? AudioOutputText("Waiting for the native output to accept compressed audio")
                : output.State == AudioOutputState.FallbackRequired
                    ? AudioOutputText("Switching to PCM · {0}", AudioOutputFallbackText(output.FallbackReason))
                    : output.State == AudioOutputState.Stopped ? AudioOutputText("Audio output stopped")
                    : output.FallbackReason is null or "PolicyOff" ? AudioOutputText("PCM · Windows audio")
                    : AudioOutputText("PCM · fallback: {0}", AudioOutputFallbackText(output.FallbackReason));
        rows.Add((AudioOutputText("Audio output"), result));
        if (output?.IsBitstream != true) return;
        rows.Add((AudioOutputText("Audio device"), output.EndpointName ?? unavailable));
        rows.Add((AudioOutputText("Audio transport"), output.TransportSampleRate > 0 && output.TransportChannels > 0
            ? AudioOutputText("{0} Hz · {1} transport channels",
                output.TransportSampleRate.ToString("N0", CultureInfo.InvariantCulture), output.TransportChannels)
            : unavailable));
        rows.Add((AudioOutputText("Audio clock drift"), AudioOutputText("{0} ms · native output clock",
            (output.DriftTicks / (double)TimeSpan.TicksPerMillisecond).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))));
    }

    private static string AudioOutputFallbackText(string? reason) => AudioOutputText(reason switch
    {
        "NativeAudioApiUnavailable" or "PassthroughNotEligible" => "Native passthrough is unavailable",
        "DeliveryUnsupported" => "The delivered stream requires decoded audio",
        "CodecUnsupported" => "The selected codec is outside the passthrough policy",
        "MetadataUnavailable" or "AudioStreamMappingUnavailable" => "The selected audio stream could not be verified",
        "EndpointUnavailable" => "No audio output device is available",
        "FormatUnsupported" or "FormatChanged" => "The output device does not support this exact compressed format",
        "ExclusiveUnavailable" => "The output device could not open in exclusive mode",
        "DeviceInvalidated" or "EndpointChanged" => "The audio output device changed",
        "RateUnsupported" => "Playback speed requires PCM audio",
        "VolumeRequested" or "MuteRequested" or "SoftwareVolumeRequired" => "Volume or mute requires PCM audio",
        "ClockUnavailable" or "TimingDiscontinuity" => "The audio output could not follow the playback clock",
        "MuxUnavailable" or "MuxFailed" or "QueueOverflow" => "The compressed audio output failed",
        _ => "Native audio output could not be confirmed"
    });

    private static string AudioOutputText(string key, params object[] arguments)
    {
        _ = AudioOutputInformationTextRegistered;
        return LumenText.Get(key, arguments);
    }

    private static readonly bool AudioOutputInformationTextRegistered = RegisterAudioOutputInformationText();

    private static bool RegisterAudioOutputInformationText()
    {
        LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{0} · compressed audio track"] = "{0} \u00b7 \u538b\u7f29\u97f3\u8f68",
            ["{0} · exclusive WASAPI bitstream"] = "{0} \u00b7 WASAPI \u72ec\u5360\u7801\u6d41\u76f4\u901a",
            ["Waiting for the native output to accept compressed audio"] = "\u7b49\u5f85\u539f\u751f\u8f93\u51fa\u63a5\u6536\u538b\u7f29\u97f3\u9891",
            ["Switching to PCM · {0}"] = "\u6b63\u5728\u5207\u6362\u4e3a PCM \u00b7 {0}",
            ["Audio output stopped"] = "\u97f3\u9891\u8f93\u51fa\u5df2\u505c\u6b62",
            ["No audio output"] = "\u65e0\u97f3\u9891\u8f93\u51fa",
            ["PCM · Windows audio"] = "PCM \u00b7 Windows \u97f3\u9891",
            ["PCM · fallback: {0}"] = "PCM \u00b7 \u56de\u9000\u539f\u56e0\uff1a{0}",
            ["Audio output"] = "\u97f3\u9891\u8f93\u51fa",
            ["Audio device"] = "\u97f3\u9891\u8bbe\u5907",
            ["Audio transport"] = "\u97f3\u9891\u4f20\u8f93\u683c\u5f0f",
            ["Audio clock drift"] = "\u97f3\u9891\u65f6\u949f\u504f\u5dee",
            ["{0} Hz · {1} transport channels"] = "{0} Hz \u00b7 {1} \u4e2a\u4f20\u8f93\u901a\u9053",
            ["{0} ms · native output clock"] = "{0} ms \u00b7 \u539f\u751f\u8f93\u51fa\u65f6\u949f",
            ["Native passthrough is unavailable"] = "\u539f\u751f\u97f3\u9891\u76f4\u901a\u4e0d\u53ef\u7528",
            ["The delivered stream requires decoded audio"] = "\u5f53\u524d\u4f20\u8f93\u7801\u6d41\u9700\u8981\u89e3\u7801\u97f3\u9891",
            ["The selected codec is outside the passthrough policy"] = "\u6240\u9009\u7f16\u7801\u683c\u5f0f\u4e0d\u5728\u76f4\u901a\u7b56\u7565\u8303\u56f4\u5185",
            ["The selected audio stream could not be verified"] = "\u65e0\u6cd5\u786e\u8ba4\u6240\u9009\u97f3\u8f68",
            ["No audio output device is available"] = "\u6ca1\u6709\u53ef\u7528\u7684\u97f3\u9891\u8f93\u51fa\u8bbe\u5907",
            ["The output device does not support this exact compressed format"] = "\u8f93\u51fa\u8bbe\u5907\u4e0d\u652f\u6301\u6b64\u5b8c\u6574\u538b\u7f29\u683c\u5f0f",
            ["The output device could not open in exclusive mode"] = "\u8f93\u51fa\u8bbe\u5907\u65e0\u6cd5\u4ee5\u72ec\u5360\u6a21\u5f0f\u6253\u5f00",
            ["The audio output device changed"] = "\u97f3\u9891\u8f93\u51fa\u8bbe\u5907\u5df2\u53d8\u66f4",
            ["Playback speed requires PCM audio"] = "\u64ad\u653e\u500d\u901f\u9700\u8981 PCM \u97f3\u9891",
            ["Volume or mute requires PCM audio"] = "\u97f3\u91cf\u6216\u9759\u97f3\u63a7\u5236\u9700\u8981 PCM \u97f3\u9891",
            ["The audio output could not follow the playback clock"] = "\u97f3\u9891\u8f93\u51fa\u65e0\u6cd5\u8ddf\u968f\u64ad\u653e\u65f6\u949f",
            ["The compressed audio output failed"] = "\u538b\u7f29\u97f3\u9891\u8f93\u51fa\u5931\u8d25",
            ["Native audio output could not be confirmed"] = "\u65e0\u6cd5\u786e\u8ba4\u539f\u751f\u97f3\u9891\u8f93\u51fa"
        });
        return true;
    }

    private string PlaybackBitrateInformation(PlaybackContext context, MediaStream? video)
    {
        var serverTranscoding = CurrentServerTranscodingInformation(context);
        if (serverTranscoding?.Bitrate is > 0)
            return LumenText.Get("{0} Mbps · server transcode target", (serverTranscoding.Bitrate.Value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture));
        if (serverTranscoding?.VideoBitrate is > 0)
            return LumenText.Get("{0} Mbps · server video transcode target", (serverTranscoding.VideoBitrate.Value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture));
        var source = context.Source;
        if (source.Bitrate is > 0)
            return LumenText.Get("{0} Mbps · Emby source media information", (source.Bitrate.Value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture));
        if (video?.BitRate is > 0)
            return LumenText.Get("{0} Mbps · Emby source video information", (video.BitRate.Value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture));
        return LumenText.Get("Unavailable");
    }

    private static string DecoderApiName(string api) => api switch
    {
        "D3D11" => "D3D11VA",
        "NvidiaNvdec" => "NVDEC",
        "IntelQsv" => "Quick Sync",
        "AmdAmf" => "AMF",
        "Software" => LumenText.Get("Software"),
        _ => LumenText.Get("Unavailable")
    };

    private void CopyPlaybackInfoClicked(object sender, RoutedEventArgs args)
    {
        UpdatePlaybackInformation();
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(_playbackInfoCopy);
            var copied = Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsAllowedInHistory = false, IsRoamable = false
            });
            ShowPlayerToast(copied ? "Playback information copied" : "Clipboard unavailable");
        }
        catch (Exception) { ShowPlayerToast("Clipboard unavailable"); }
    }

    private async void SavePlaybackInfoClicked(object sender, RoutedEventArgs args)
    {
        if (IsModalOpen || XamlRoot is null) return;
        _playerFileDialogOpen = true;
        RevealControls();
        UpdateQueueControls();
        try
        {
            var snapshot = _diagnostics.CreateSnapshot();
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "lumen-playback-diagnostics-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                DefaultFileExtension = ".json"
            };
            picker.FileTypeChoices.Add("JSON diagnostic snapshot", new List<string> { ".json" });
            var result = await picker.PickSaveFileAsync();
            if (result is null) return;
            await File.WriteAllBytesAsync(result.Path, snapshot);
            ShowPlayerOsd("info_20_regular", LumenText.Get("Diagnostic report saved"));
        }
        catch (Exception) { ShowPlayerOsd("info_20_regular", LumenText.Get("Diagnostic report could not be saved")); }
        finally
        {
            _playerFileDialogOpen = false;
            UpdateQueueControls();
            RevealControls();
        }
    }

    private void UpdateMediaControlsPresentation(PlaybackContext context)
    {
        var engine = _engine;
        if (engine is null) return;
        var neighbors = _episodeNeighborsItemId == _item?.Id ? _episodeNeighbors : null;
        var enabled = CanChangeEpisode();
        engine.SetMediaControlsMetadata(context.PlaybackId, TitleText.Text, PlaybackSubtitle(_item),
            previousEnabled: enabled && neighbors?.Previous is not null, nextEnabled: enabled && neighbors?.Next is not null);
        if (_mediaArtworkPlaybackId == context.PlaybackId) return;
        _mediaArtworkPlaybackId = context.PlaybackId;
        _ = LoadMediaControlsArtworkAsync(context.PlaybackId);
    }

    private async Task LoadMediaControlsArtworkAsync(Guid playbackId)
    {
        var session = _session;
        var item = _item;
        var engine = _engine;
        var intent = _playIntent;
        var token = _playRequest?.Token ?? CancellationToken.None;
        if (session is null || engine is null || item is null || string.IsNullOrWhiteSpace(session.Server.Id)
            || string.IsNullOrWhiteSpace(session.User.Id)) return;
        bool OwnsArtwork() => ReferenceEquals(session, _session) && ReferenceEquals(engine, _engine)
            && intent == _playIntent && _coordinator?.ActiveContext?.PlaybackId == playbackId;
        try
        {
            var bytes = await _playerArtworkCache.GetAsync(session.Api, session.Server.Id, session.User.Id,
                item, 320, 480, ArtworkKind.Poster, token);
            if (!OwnsArtwork() || bytes is not { Length: > 0 }) return;
            await engine.SetMediaControlsArtworkAsync(playbackId, bytes, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Artwork is optional; a failed image request must not alter the active playback state.
        }
    }

    private async void UpdateNextEpisodeArtwork(BaseItemDto target)
    {
        if (_nextArtworkItemId == target.Id) return;
        _nextArtworkItemId = target.Id;
        NextEpisodeArtwork.Source = null;
        var session = _session;
        var intent = _playIntent;
        var token = _playRequest?.Token ?? CancellationToken.None;
        if (session is null || string.IsNullOrWhiteSpace(session.Server.Id) || string.IsNullOrWhiteSpace(session.User.Id)) return;
        bool OwnsArtwork() => ReferenceEquals(session, _session) && intent == _playIntent
            && _nextArtworkItemId == target.Id && ChapterNextTarget()?.Id == target.Id;
        try
        {
            var bytes = await _playerArtworkCache.GetAsync(session.Api, session.Server.Id, session.User.Id,
                target, 288, 162, ArtworkKind.Landscape, token);
            if (!OwnsArtwork() || bytes is not { Length: > 0 }) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 288, 162, token,
                bitmap => { if (OwnsArtwork()) NextEpisodeArtwork.Source = bitmap; });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Optional preview art never changes the countdown or its navigation owner.
        }
    }
}
