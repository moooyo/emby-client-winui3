using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe
{
    public sealed partial class App
    {
        private string? _hardwareMultiAudioMediaDirectory;
        private bool _multiAudioSameLanguage;
        private bool _multiAudioExpectSoftwareFallback;

        private void ConfigureHardwareMultiAudioMode(string mediaDirectory, string[] arguments)
        {
            if (!Path.IsPathFullyQualified(mediaDirectory))
                throw new ArgumentException("The generated multi-audio directory must be an explicit absolute path.");
            if (arguments.Count(value => value == "--same-language") > 1
                || arguments.Count(value => value == "--expect-software-fallback") > 1)
                throw new ArgumentException("Repeated multi-audio control option.");
            _multiAudioSameLanguage = arguments.Contains("--same-language", StringComparer.Ordinal);
            _multiAudioExpectSoftwareFallback = arguments.Contains("--expect-software-fallback", StringComparer.Ordinal);
            ConfigureHardwareDecodeMode(arguments.Where(value => value is not "--same-language" and not "--expect-software-fallback").ToArray());
            if (_multiAudioExpectSoftwareFallback && (!_hardwareDecodeRequested || _requireHardwareDecode))
                throw new ArgumentException("The automatic software fallback control requires enabled hardware without a hardware/vendor requirement.");
            if (_multiAudioExpectSoftwareFallback && _expectDecoderApiFallback)
                throw new ArgumentException("Select either the codec fallback control or the unavailable API control.");
            _hardwareDecodeMode = false;
            _hardwareMultiAudioMediaDirectory = Path.GetFullPath(mediaDirectory);
        }

        private async Task RunHardwareMultiAudioAsync()
        {
            var report = new HardwareMultiAudioReport
            {
                HardwareRequested = _hardwareDecodeRequested, HardwareRequired = _requireHardwareDecode,
                RequiredVendor = _requireHardwareVendor, SameLanguageTracks = _multiAudioSameLanguage,
                RequestedApi = _decoderApiRequested, ExpectedApiFallback = _expectDecoderApiFallback,
                ExpectedSoftwareFallback = _multiAudioExpectSoftwareFallback,
                ExpectedVideoCodec = _multiAudioExpectSoftwareFallback ? "mpeg4" : "h264"
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            SyntheticHlsEndpoint? upstream = null;
            NativePlaybackEngine? engine = null;
            try
            {
                SaveHardwareMultiAudio(report);
                Require(report.NativeAot, "NativeAotRequired");
                upstream = SyntheticHlsEndpoint.CreateMultiAudioMp4(_hardwareMultiAudioMediaDirectory!);
                report.InputFile = upstream.InputFiles.Single();
                engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true,
                    initialHardwareDecoding: _hardwareDecodeRequested, initialDecoderApi: _decoderApiRequested);
                engine.Diagnostic += (_, diagnostic) =>
                {
                    lock (report.Diagnostics) report.Diagnostics.Add("Native:" + diagnostic.Operation + ":" + diagnostic.ErrorCode);
                };
                var selections = new List<(int StreamIndex, bool HardwareRequested)>
                {
                    (1, _hardwareDecodeRequested), (2, _hardwareDecodeRequested)
                };
                if (_hardwareDecodeRequested && !_multiAudioExpectSoftwareFallback)
                {
                    selections.Add((1, false));
                    if (_requireHardwareDecode || IsVendorDecoderApi(_decoderApiRequested)) selections.Add((2, true));
                }
                foreach (var selection in selections)
                {
                    var requestedIndex = selection.StreamIndex;
                    var requestedApi = selection.HardwareRequested ? _decoderApiRequested : "Software";
                    var round = new MultiAudioSelectionObservation
                    {
                        RequestedContainerStreamIndex = requestedIndex, RequestedHardwarePolicy = selection.HardwareRequested,
                        RequestedApi = requestedApi
                    };
                    report.Selections.Add(round);
                    engine.ConfigureVideoDecoderApi(requestedApi);
                    report.Stage = "OpenAudioStream" + requestedIndex + (selection.HardwareRequested ? "HardwareAllowed" : "SoftwareOnly");
                    SaveHardwareMultiAudio(report);
                    var id = Guid.NewGuid();
                    await engine.OpenAsync(new PlaybackEngineRequest
                    {
                        PlaybackId = id, MediaUri = upstream.ManifestUri,
                        Headers = new Dictionary<string, string> { ["X-Emby-Token"] = SyntheticHlsEndpoint.SyntheticHeaderValue },
                        DeliveryMethod = PlaybackDeliveryMethod.DirectStream, TimelineKind = PlaybackTimelineKind.FullSource,
                        InitialPositionTicks = 0, TimelineOffsetTicks = 0, ItemRunTimeTicks = upstream.DurationTicks,
                        AudioStreamIndex = requestedIndex, SubtitleStreamIndex = -1,
                        Source = new MediaSourceInfo
                        {
                            Id = "synthetic-multiaudio-control", Container = "mp4", RunTimeTicks = upstream.DurationTicks,
                            SupportsDirectStream = true, DefaultAudioStreamIndex = 2, DefaultSubtitleStreamIndex = -1,
                            MediaStreams = [new MediaStream { Index = 0, Type = "Video", Codec = report.ExpectedVideoCodec },
                                new MediaStream { Index = 1, Type = "Audio", Codec = "aac", Language = "eng", Channels = 2 },
                                new MediaStream { Index = 2, Type = "Audio", Codec = "aac", Language = _multiAudioSameLanguage ? "eng" : "fra", Channels = 2 }]
                        }
                    }, deadline.Token);
                    await WaitForDecoderAsync(engine, deadline.Token, requestedApi);
                    var player = _element!.MediaPlayer ?? throw new ProbeFailure("MultiAudioNativePlayerMissing");
                    var item = player.Source as MediaPlaybackItem ?? throw new ProbeFailure("MultiAudioPlaybackItemMissing");
                    Require(item.AudioTracks.Count == 2, "TwoNativeAudioTracksRequired");
                    round.SelectedNativeTrackIndex = item.AudioTracks.SelectedIndex;
                    Require(round.SelectedNativeTrackIndex is >= 0 and < 2, "NativeAudioSelectionMissing");
                    round.NativeTrackLanguages = Enumerable.Range(0, item.AudioTracks.Count)
                        .Select(index => AudioLanguage(item.AudioTracks[index].Language)).ToArray();
                    round.SelectedNativeLanguage = round.NativeTrackLanguages[round.SelectedNativeTrackIndex];
                    round.ExpectedNativeLanguage = requestedIndex == 1 || _multiAudioSameLanguage ? "en" : "fr";
                    round.SelectedContainerStreamIndex = engine.SelectedContainerAudioIndexForProbe;
                    round.ContainerStreamOrder = engine.AudioContainerOrderForProbe;
                    round.DefaultTrackReorderedFirst = round.ContainerStreamOrder.SequenceEqual([2, 1]);
                    round.ActualVideoCodec = engine.VideoCodecNameForMultiAudioProbe;
                    round.NativeDurationTicks = player.PlaybackSession.NaturalDuration.Ticks;
                    round.PlayingNativePositionTicks = player.PlaybackSession.Position.Ticks;
                    var decoding = engine.VideoDecoding ?? throw new ProbeFailure("ActualVideoDecoderSnapshotMissing");
                    round.Decoding = new HardwareDecodeObservation
                    {
                        Stage = report.Stage, HardwareRequested = decoding.HardwareRequested, Decoder = decoding.Decoder,
                        RequestedApi = decoding.RequestedApi, ActualApi = decoding.ActualApi,
                        FallbackReason = decoding.FallbackReason, NativeDecoderName = decoding.NativeDecoderName,
                        DecodedFrames = decoding.DecodedFrames, HardwareDecodedFrames = decoding.HardwareDecodedFrames,
                        HardwareFallback = decoding.HardwareFallback, Codec = decoding.Codec, GpuName = decoding.GpuName,
                        GpuVendorId = decoding.GpuVendorId, GpuVendor = GraphicsVendor(decoding.GpuVendorId), AdapterLuid = decoding.AdapterLuid,
                        NativePositionTicks = round.PlayingNativePositionTicks, EnginePositionTicks = engine.Snapshot?.PositionTicks ?? 0,
                        TimelineOffsetTicks = 0, VideoWidth = player.PlaybackSession.NaturalVideoWidth,
                        VideoHeight = player.PlaybackSession.NaturalVideoHeight
                    };
                    SaveHardwareMultiAudio(report);
                    Require(round.NativeDurationTicks is >= 590_000_000 and <= 610_000_000, "ExpectedSixtySecondMultiAudioMedia");
                    Require(round.ActualVideoCodec == report.ExpectedVideoCodec, "MultiAudioVideoCodecMismatch");
                    Require(round.DefaultTrackReorderedFirst, "ReorderedDefaultAudioTrackRequired");
                    Require(round.SelectedContainerStreamIndex == requestedIndex && round.SelectedNativeLanguage == round.ExpectedNativeLanguage,
                        "RequestedContainerAudioStreamNotSelected");
                    Require(decoding.HardwareRequested == selection.HardwareRequested, "HardwareDecodingConfigurationMismatch");
                    if (_multiAudioExpectSoftwareFallback)
                        Require(decoding.RequestedApi == requestedApi && decoding.ActualApi == "Software"
                            && decoding.Decoder == "Software" && decoding.HardwareRequested && decoding.HardwareFallback
                            && IsAllowedApiFallbackReason(decoding.FallbackReason),
                            "AutomaticSoftwareFallbackRequired");
                    else ValidateDecoderSelection(decoding, requestedApi);
                    await Task.Delay(650, deadline.Token);
                    Require(player.PlaybackSession.Position.Ticks > round.PlayingNativePositionTicks, "MultiAudioNativeClockStopped");
                    await engine.StopAsync(id, deadline.Token);
                    round.PlayerDetachedAfterStop = _element.MediaPlayer is null && engine.Snapshot is { State: PlaybackEngineState.Stopped };
                    Require(round.PlayerDetachedAfterStop, "MultiAudioNativeStopDidNotDetach");
                    player = null;
                    item = null;
                    await WaitAsync(() => upstream.ActiveRequests == 0, "MultiAudioUpstreamRequestsDidNotDrain", deadline.Token);
                    var stoppedRequests = upstream.RequestCount;
                    await Task.Delay(1300, deadline.Token);
                    round.NoUpstreamRequestsAfterStop = upstream.RequestCount == stoppedRequests && upstream.ActiveRequests == 0;
                    Require(round.NoUpstreamRequestsAfterStop, "MultiAudioUpstreamRequestsContinuedAfterStop");
                    round.Status = "Passed";
                    SaveHardwareMultiAudio(report);
                }
                report.Requests = upstream.Requests();
                Require(report.Requests.Length > 0 && report.Requests.All(value => value.Resource == "Mp4"
                    && value.Authenticated && value.Completed && value.StatusCode is 200 or 206), "AuthenticatedMultiAudioTrafficEvidenceMissing");
                await engine.DisposeAsync();
                engine = null;
                report.EngineDisposed = true;
                await upstream.DisposeAsync();
                upstream = null;
                report.ListenerClosed = true;
                lock (report.Diagnostics) Require(report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
                report.Stage = "Complete";
                report.Status = "Passed";
            }
            catch (Exception exception)
            {
                if (upstream is not null) report.Requests = upstream.Requests();
                if (report.Selections.LastOrDefault() is { Status: "Running" } round) round.Status = "Failed";
                report.Status = "Failed";
                report.ErrorCode = ErrorCode(exception);
                report.ErrorHResult = exception.HResult;
            }
            finally
            {
                try
                {
                    if (engine is not null) { await engine.DisposeAsync(); report.EngineDisposed = true; }
                    if (upstream is not null) { await upstream.DisposeAsync(); report.ListenerClosed = true; }
                }
                catch (Exception exception)
                {
                    report.Status = "Failed";
                    report.ErrorCode ??= "FinalCleanup_" + ErrorCode(exception);
                    report.ErrorHResult ??= exception.HResult;
                }
                report.FinishedAt = DateTimeOffset.UtcNow;
                SaveHardwareMultiAudio(report);
                Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
                _window!.Close();
                Exit();
            }
        }

        private static string AudioLanguage(string? language) => language?.ToLowerInvariant().Split('-')[0] switch
        {
            "en" or "eng" => "en", "fr" or "fra" or "fre" => "fr", _ => "Other"
        };

        private void SaveHardwareMultiAudio(HardwareMultiAudioReport report)
        {
            report.UpdatedAt = DateTimeOffset.UtcNow;
            lock (report.Diagnostics)
            {
                var temporary = _resultPath! + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report,
                    HardwareMultiAudioJsonContext.Default.HardwareMultiAudioReport));
                File.Move(temporary, _resultPath!, overwrite: true);
            }
        }
    }

    internal sealed partial class SyntheticHlsEndpoint
    {
        internal static SyntheticHlsEndpoint CreateMultiAudioMp4(string directory)
        {
            var root = Path.GetFullPath(directory);
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(root)
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new PlaybackException("SyntheticMultiAudioDirectoryInvalid");
            const string name = "fixture-multiaudio.mp4";
            var media = ReadFixtureFile(root, name, 64 * 1024 * 1024);
            if (media.Length < 1024 || media.Length >= 64 * 1024 * 1024 || !media.AsSpan(4, 4).SequenceEqual("ftyp"u8))
                throw new PlaybackException("SyntheticMultiAudioMp4Required");
            return new SyntheticHlsEndpoint(media, InputFile(name, media));
        }

        private SyntheticHlsEndpoint(byte[] media, HlsInputFile input)
        {
            _files.Add("/fixture-multiaudio.mp4", media);
            InputFiles = [input];
            DurationTicks = TimeSpan.FromSeconds(60).Ticks;
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(4);
            ManifestUri = new Uri(FormattableString.Invariant($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/fixture-multiaudio.mp4"));
            _accepting = AcceptAsync();
        }
    }

    internal sealed class HardwareMultiAudioReport
    {
        public string Status { get; set; } = "Running";
        public string ExecutionMode { get; init; } = "SyntheticMultiAudioProductDecoderControl";
        public string Scope { get; init; } = "Explicit generated 60-second MP4 with two AAC streams and the second stream marked default. Owned authenticated loopback origin, linked product NativePlaybackEngine, actual native audio selection and decoder stream-index observations.";
        public string[] NotVerified { get; init; } = ["Real Emby server compatibility", "Coordinator negotiation or audio switching", "Audible output", "Pixel capture", "Long-running resource regression"];
        public bool HardwareRequested { get; init; }
        public bool HardwareRequired { get; init; }
        public string? RequiredVendor { get; init; }
        public string RequestedApi { get; init; } = "Auto";
        public bool ExpectedApiFallback { get; init; }
        public bool SameLanguageTracks { get; init; }
        public bool ExpectedSoftwareFallback { get; init; }
        public string ExpectedVideoCodec { get; init; } = "h264";
        public int ProcessId { get; init; } = Environment.ProcessId;
        public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Stage { get; set; } = "Initialization";
        public string? ErrorCode { get; set; }
        public int? ErrorHResult { get; set; }
        public HlsInputFile? InputFile { get; set; }
        public List<MultiAudioSelectionObservation> Selections { get; init; } = [];
        public SyntheticHlsRequest[] Requests { get; set; } = [];
        public bool EngineDisposed { get; set; }
        public bool ListenerClosed { get; set; }
        public List<string> Diagnostics { get; init; } = [];
    }

    internal sealed class MultiAudioSelectionObservation
    {
        public string Status { get; set; } = "Running";
        public int RequestedContainerStreamIndex { get; init; }
        public bool RequestedHardwarePolicy { get; init; }
        public string RequestedApi { get; init; } = "Auto";
        public int SelectedNativeTrackIndex { get; set; } = -1;
        public string[] NativeTrackLanguages { get; set; } = [];
        public string? ExpectedNativeLanguage { get; set; }
        public string? SelectedNativeLanguage { get; set; }
        public int? SelectedContainerStreamIndex { get; set; }
        public int[] ContainerStreamOrder { get; set; } = [];
        public bool DefaultTrackReorderedFirst { get; set; }
        public string? ActualVideoCodec { get; set; }
        public long NativeDurationTicks { get; set; }
        public long PlayingNativePositionTicks { get; set; }
        public HardwareDecodeObservation? Decoding { get; set; }
        public bool PlayerDetachedAfterStop { get; set; }
        public bool NoUpstreamRequestsAfterStop { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(HardwareMultiAudioReport))]
    internal partial class HardwareMultiAudioJsonContext : JsonSerializerContext;
}

namespace EmbyClient.App.Playback
{
    public sealed partial class NativePlaybackEngine
    {
        internal int? SelectedContainerAudioIndexForProbe
        {
            get
            {
                if (_current?.DecoderSource is not { } source || _current.Item is not { } item) return null;
                var index = item.AudioTracks.SelectedIndex;
                return index >= 0 && index < source.AudioStreams.Count ? (int)source.AudioStreams[index].StreamIndex : null;
            }
        }

        internal int[] AudioContainerOrderForProbe => _current?.DecoderSource is { } source
            ? source.AudioStreams.Select(stream => (int)stream.StreamIndex).ToArray() : [];

        internal string? VideoCodecNameForMultiAudioProbe => _current?.DecoderSource?.CurrentVideoStream?.CodecName.ToLowerInvariant() switch
        {
            "h264" => "h264", "mpeg4" => "mpeg4", _ => null
        };
    }
}
