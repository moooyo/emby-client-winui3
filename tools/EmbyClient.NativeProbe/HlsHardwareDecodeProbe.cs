using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _hardwareHlsMediaDirectory;

    private void ConfigureHardwareHlsMode(string mediaDirectory, string[] arguments)
    {
        if (!Path.IsPathFullyQualified(mediaDirectory))
            throw new ArgumentException("The generated HLS directory must be an explicit absolute path.");
        ConfigureHardwareDecodeMode(arguments);
        _hardwareDecodeMode = false;
        _hardwareHlsMediaDirectory = Path.GetFullPath(mediaDirectory);
    }

    private async Task RunHardwareHlsAsync()
    {
        var report = new HlsHardwareDecodeReport
        {
            HardwareRequested = _hardwareDecodeRequested, HardwareRequired = _requireHardwareDecode,
            RequiredVendor = _requireHardwareVendor, RequestedApi = _decoderApiRequested,
            ExpectedApiFallback = _expectDecoderApiFallback
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        SyntheticHlsEndpoint? upstream = null;
        NativePlaybackEngine? engine = null;
        var playbackId = Guid.NewGuid();
        try
        {
            SaveHardwareHls(report);
            Require(report.NativeAot, "NativeAotRequired");
            upstream = new SyntheticHlsEndpoint(_hardwareHlsMediaDirectory!);
            report.InputFiles = upstream.InputFiles;
            report.ManifestDurationTicks = upstream.DurationTicks;
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true,
                initialHardwareDecoding: _hardwareDecodeRequested, initialDecoderApi: _decoderApiRequested);
            engine.Diagnostic += (_, diagnostic) =>
            {
                lock (report.Diagnostics) report.Diagnostics.Add("Native:" + diagnostic.Operation + ":" + diagnostic.ErrorCode);
            };
            var request = new PlaybackEngineRequest
            {
                PlaybackId = playbackId, MediaUri = upstream.ManifestUri,
                Headers = new Dictionary<string, string> { ["X-Emby-Token"] = SyntheticHlsEndpoint.SyntheticHeaderValue },
                DeliveryMethod = PlaybackDeliveryMethod.Transcode, TimelineKind = PlaybackTimelineKind.FullSource,
                InitialPositionTicks = 0, TimelineOffsetTicks = 0, ItemRunTimeTicks = upstream.DurationTicks,
                AudioStreamIndex = 1, SubtitleStreamIndex = -1,
                Source = new MediaSourceInfo
                {
                    Id = "synthetic-hls-control", Container = "mp4", RunTimeTicks = upstream.DurationTicks,
                    SupportsTranscoding = true, TranscodingContainer = "ts", TranscodingSubProtocol = "hls",
                    DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1,
                    MediaStreams = [new MediaStream { Index = 0, Type = "Video", Codec = "h264" },
                        new MediaStream { Index = 1, Type = "Audio", Codec = "aac", Channels = 2 }]
                }
            };
            report.Stage = "OpenHlsThroughProductRelay";
            SaveHardwareHls(report);
            await engine.OpenAsync(request, deadline.Token);
            await WaitForDecoderAsync(engine, deadline.Token);
            AddHardwareHlsObservation(report, engine, "Playing");
            var player = _element!.MediaPlayer!;
            report.InitialMuted = player.IsMuted;
            Require(report.InitialMuted, "InitialMuteNotPreserved");
            var previousPosition = player.PlaybackSession.Position.Ticks;
            report.Stage = "ObserveHlsPlayback";
            for (var second = 0; second < 5; second++)
            {
                SaveHardwareHls(report);
                await Task.Delay(1000, deadline.Token);
                Require(player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                    && player.PlaybackSession.Position.Ticks > previousPosition, "HlsNativePlaybackClockStopped");
                previousPosition = player.PlaybackSession.Position.Ticks;
                AddHardwareHlsObservation(report, engine, "PlayingObservation");
            }

            report.Stage = "Pause";
            SaveHardwareHls(report);
            await engine.PauseAsync(playbackId, deadline.Token);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused,
                "HlsNativePauseDidNotComplete", deadline.Token);
            await Task.Delay(150, deadline.Token);
            report.PauseStartTicks = player.PlaybackSession.Position.Ticks;
            await Task.Delay(650, deadline.Token);
            report.PauseEndTicks = player.PlaybackSession.Position.Ticks;
            Require(Math.Abs(report.PauseEndTicks - report.PauseStartTicks) <= 500_000, "HlsPausedClockAdvanced");

            report.Stage = "Seek";
            SaveHardwareHls(report);
            // Product HLS seeks reopen at the full-source position and then restore the paused intent.
            playbackId = Guid.NewGuid();
            await engine.OpenAsync(request with { PlaybackId = playbackId, InitialPositionTicks = report.SeekTargetTicks }, deadline.Token);
            await WaitForDecoderAsync(engine, deadline.Token);
            player = _element.MediaPlayer ?? throw new ProbeFailure("HlsNativePlayerMissingAfterSeek");
            await engine.PauseAsync(playbackId, deadline.Token);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused,
                "HlsSeekPauseIntentNotRestored", deadline.Token);
            report.PauseRestoredAfterSeek = true;
            report.SeekObservedTicks = player.PlaybackSession.Position.Ticks;
            Require(Math.Abs(report.SeekObservedTicks - report.SeekTargetTicks) <= 7_500_000, "HlsNativeSeekPositionMismatch");

            report.Stage = "Resume";
            SaveHardwareHls(report);
            await engine.ResumeAsync(playbackId, deadline.Token);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                && player.PlaybackSession.Position.Ticks > report.SeekObservedTicks + 2_000_000,
                "HlsNativeResumeClockDidNotAdvance", deadline.Token);
            await WaitForDecoderAsync(engine, deadline.Token);
            report.ResumePositionTicks = player.PlaybackSession.Position.Ticks;
            Require(player.IsMuted, "ResumeMuteNotPreserved");
            AddHardwareHlsObservation(report, engine, "AfterResume");
            await Task.Delay(1000, deadline.Token);

            report.Stage = "Stop";
            SaveHardwareHls(report);
            await engine.StopAsync(playbackId, deadline.Token);
            report.PlayerDetachedAfterStop = _element.MediaPlayer is null;
            report.StoppedSnapshotObserved = engine.Snapshot is { State: PlaybackEngineState.Stopped };
            report.LastDecoderSnapshotRetainedAfterStop = IsObservedDecoder(engine.VideoDecoding);
            Require(report.PlayerDetachedAfterStop && report.StoppedSnapshotObserved
                && report.LastDecoderSnapshotRetainedAfterStop, "HlsNativeStopDidNotDetach");
            player = null;
            await WaitAsync(() => upstream.ActiveRequests == 0, "HlsUpstreamRequestsDidNotDrain", deadline.Token);
            var stoppedRequests = upstream.RequestCount;
            await Task.Delay(1300, deadline.Token);
            report.NoUpstreamRequestsAfterStop = upstream.RequestCount == stoppedRequests && upstream.ActiveRequests == 0;
            Require(report.NoUpstreamRequestsAfterStop, "HlsUpstreamRequestsContinuedAfterStop");
            report.Requests = upstream.Requests();
            report.AllRequestsAuthenticated = report.Requests.All(value => value.Authenticated);
            report.ManifestRequests = report.Requests.Count(value => value.Resource == "Manifest" && value.StatusCode == 200);
            report.SegmentRequests = report.Requests.Count(value => value.Resource == "Segment" && value.StatusCode is 200 or 206);
            Require(report.AllRequestsAuthenticated && report.Requests.All(value => value.Completed && value.StatusCode is 200 or 206)
                && report.ManifestRequests > 0 && report.SegmentRequests > 1, "AuthenticatedHlsTrafficEvidenceMissing");
            await engine.DisposeAsync();
            engine = null;
            report.EngineDisposed = true;
            await upstream.DisposeAsync();
            upstream = null;
            report.ListenerClosed = true;
            report.ResourcesAfterCleanup = await CaptureResourcesAsync(deadline.Token);
            lock (report.Diagnostics) Require(report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
            report.Stage = "Complete";
            report.Status = "Passed";
        }
        catch (Exception exception)
        {
            if (engine?.VideoDecoding is not null && _element?.MediaPlayer is not null)
            {
                try { AddHardwareHlsObservation(report, engine, "Failure", validate: false); }
                catch { }
            }
            if (upstream is not null) report.Requests = upstream.Requests();
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
            SaveHardwareHls(report);
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private void AddHardwareHlsObservation(HlsHardwareDecodeReport report, NativePlaybackEngine engine,
        string stage, bool validate = true)
    {
        var decoding = engine.VideoDecoding ?? throw new ProbeFailure("ActualVideoDecoderSnapshotMissing");
        var player = _element!.MediaPlayer ?? throw new ProbeFailure("HlsNativePlayerMissing");
        var vendor = GraphicsVendor(decoding.GpuVendorId);
        report.DecodingSamples.Add(new HardwareDecodeObservation
        {
            Stage = stage, HardwareRequested = decoding.HardwareRequested, Decoder = decoding.Decoder,
            RequestedApi = decoding.RequestedApi, ActualApi = decoding.ActualApi,
            FallbackReason = decoding.FallbackReason, NativeDecoderName = decoding.NativeDecoderName,
            DecodedFrames = decoding.DecodedFrames, HardwareDecodedFrames = decoding.HardwareDecodedFrames,
            HardwareFallback = decoding.HardwareFallback, Codec = decoding.Codec, GpuName = decoding.GpuName,
            GpuVendorId = decoding.GpuVendorId, GpuVendor = vendor, AdapterLuid = decoding.AdapterLuid,
            NativePositionTicks = player.PlaybackSession.Position.Ticks, EnginePositionTicks = engine.Snapshot?.PositionTicks ?? 0,
            TimelineOffsetTicks = 0, VideoWidth = player.PlaybackSession.NaturalVideoWidth,
            VideoHeight = player.PlaybackSession.NaturalVideoHeight
        });
        if (!validate) return;
        Require(decoding.Codec == "H264", "SyntheticHlsH264DecoderRequired");
        ValidateDecoderSelection(decoding, _decoderApiRequested);
    }

    private void SaveHardwareHls(HlsHardwareDecodeReport report)
    {
        report.UpdatedAt = DateTimeOffset.UtcNow;
        lock (report.Diagnostics)
        {
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report, HlsHardwareDecodeJsonContext.Default.HlsHardwareDecodeReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
    }
}

internal sealed partial class SyntheticHlsEndpoint : IAsyncDisposable
{
    internal const string SyntheticHeaderValue = "synthetic-hls-probe-no-real-credentials";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<SyntheticHlsRequest> _requests = [];
    private readonly HashSet<Task> _clients = [];
    private readonly Task _accepting;
    private int _disposed;
    internal Uri ManifestUri { get; }
    internal long DurationTicks { get; }
    internal HlsInputFile[] InputFiles { get; }
    internal int RequestCount { get { lock (_gate) return _requests.Count; } }
    internal int ActiveRequests { get { lock (_gate) return _clients.Count; } }

    internal SyntheticHlsEndpoint(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new PlaybackException("SyntheticHlsDirectoryInvalid");
        var manifestBytes = ReadFixtureFile(root, "index.m3u8", 256 * 1024);
        var manifest = new UTF8Encoding(false, true).GetString(manifestBytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
        var lines = manifest.Split('\n');
        if (lines[0] != "#EXTM3U" || !lines.Contains("#EXT-X-ENDLIST", StringComparer.Ordinal)
            || lines.Any(line => line.Contains("URI=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal)
                || line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
            throw new PlaybackException("SyntheticHlsManifestInvalid");
        double seconds = 0;
        var inputs = new List<HlsInputFile>();
        _files.Add("/index.m3u8", manifestBytes);
        inputs.Add(InputFile("index.m3u8", manifestBytes));
        long totalLength = manifestBytes.Length;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (!double.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                    out var duration) || !double.IsFinite(duration) || duration is <= 0 or > 61)
                    throw new PlaybackException("SyntheticHlsDurationInvalid");
                seconds += duration;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                if (!ValidSegmentName(line)) throw new PlaybackException("SyntheticHlsSegmentReferenceInvalid");
                var data = ReadFixtureFile(root, line, 64 * 1024 * 1024);
                if (data.Length < 188 || data[0] != 0x47) throw new PlaybackException("SyntheticHlsTransportStreamInvalid");
                if (!_files.TryAdd("/" + line, data)) throw new PlaybackException("SyntheticHlsDuplicateSegment");
                inputs.Add(InputFile(line, data));
                totalLength += data.Length;
                if (totalLength > 128 * 1024 * 1024 || inputs.Count > 65) throw new PlaybackException("SyntheticHlsFixtureTooLarge");
            }
        }
        if (seconds is < 59 or > 61 || _files.Count < 3) throw new PlaybackException("ExpectedSixtySecondSyntheticHls");
        DurationTicks = TimeSpan.FromSeconds(seconds).Ticks;
        InputFiles = inputs.ToArray();
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(4);
        ManifestUri = new Uri(FormattableString.Invariant($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/index.m3u8"));
        _accepting = AcceptAsync();
    }

    private static bool ValidSegmentName(string name) => name.StartsWith("segment-", StringComparison.Ordinal)
        && name.EndsWith(".ts", StringComparison.Ordinal) && name.Length > 11
        && name.AsSpan(8, name.Length - 11).IndexOfAnyExceptInRange('0', '9') < 0;

    private static byte[] ReadFixtureFile(string root, string name, int limit)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PlaybackException("SyntheticHlsFileInvalid");
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 || file.Length > limit) throw new PlaybackException("SyntheticHlsFileSizeInvalid");
        var data = new byte[checked((int)file.Length)];
        file.ReadExactly(data);
        if (file.ReadByte() != -1) throw new PlaybackException("SyntheticHlsFileChanged");
        return data;
    }

    private static HlsInputFile InputFile(string name, byte[] data) => new()
    {
        Name = name, Length = data.Length, Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
    };

    internal SyntheticHlsRequest[] Requests()
    {
        lock (_gate) return _requests.Select(value => new SyntheticHlsRequest
        {
            Resource = value.Resource, Authenticated = value.Authenticated, StatusCode = value.StatusCode,
            BytesSent = value.BytesSent, Completed = value.Completed
        }).ToArray();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _clients.Add(completed.Task);
                _ = ServeAsync(client, completed);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, TaskCompletionSource completed)
    {
        SyntheticHlsRequest? observed = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using (client)
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address)) return;
                var stream = client.GetStream();
                var header = new byte[16 * 1024];
                var count = 0;
                while (count < header.Length)
                {
                    if (await stream.ReadAsync(header.AsMemory(count, 1), deadline.Token).ConfigureAwait(false) == 0) return;
                    count++;
                    if (count >= 4 && header.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                var lines = Encoding.ASCII.GetString(header, 0, count).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var first = lines[0].Split(' ');
                var authentication = lines.Where(line => line.StartsWith("X-Emby-Token:", StringComparison.OrdinalIgnoreCase)).ToArray();
                var authenticated = authentication.Length == 1 && authentication[0][13..].Trim() == SyntheticHeaderValue;
                var target = first.Length == 3 ? first[1] : "";
                var valid = first.Length == 3 && first[0] == "GET" && first[2] == "HTTP/1.1" && _files.TryGetValue(target, out _);
                var status = !authenticated ? 403 : !valid ? 404 : 200;
                observed = new SyntheticHlsRequest
                {
                    Resource = target == "/index.m3u8" ? "Manifest"
                        : _files.ContainsKey(target) ? target.EndsWith(".mp4", StringComparison.Ordinal) ? "Mp4" : "Segment" : "Rejected",
                    Authenticated = authenticated, StatusCode = status
                };
                lock (_gate)
                {
                    if (_requests.Count >= 512) throw new PlaybackException("SyntheticHlsRequestLimitExceeded");
                    _requests.Add(observed);
                }
                var body = status == 200 ? _files[target] : [];
                var offset = 0;
                var length = body.Length;
                string? contentRange = null;
                var range = lines.FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
                if (status == 200 && range is not null)
                {
                    var bounds = range.StartsWith("bytes=", StringComparison.Ordinal) ? range[6..].Split('-', 2) : [];
                    if (bounds.Length != 2 || !int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                        || offset < 0 || offset >= body.Length || (bounds[1].Length > 0
                            && !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                    {
                        status = 416;
                        length = 0;
                    }
                    else
                    {
                        var end = bounds[1].Length == 0 ? body.Length - 1 : int.Parse(bounds[1], CultureInfo.InvariantCulture);
                        if (end < offset) { status = 416; length = 0; }
                        else
                        {
                            end = Math.Min(end, body.Length - 1);
                            length = end - offset + 1;
                            status = 206;
                            contentRange = FormattableString.Invariant($"Content-Range: bytes {offset}-{end}/{body.Length}\r\n");
                        }
                    }
                }
                observed.StatusCode = status;
                var reason = status switch { 200 => "OK", 206 => "Partial Content", 403 => "Forbidden", 416 => "Range Not Satisfiable", _ => "Not Found" };
                var type = target == "/index.m3u8" ? "application/vnd.apple.mpegurl"
                    : target.EndsWith(".mp4", StringComparison.Ordinal) ? "video/mp4" : "video/mp2t";
                var response = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 {status} {reason}\r\nContent-Length: {length}\r\nContent-Type: {type}\r\nAccept-Ranges: bytes\r\n{contentRange}Connection: close\r\n\r\n"));
                await stream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                if (length > 0) await stream.WriteAsync(body.AsMemory(offset, length), deadline.Token).ConfigureAwait(false);
                lock (_gate) { observed.BytesSent = length; observed.Completed = true; }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            client.Dispose();
            lock (_gate) { _clients.Remove(completed.Task); completed.TrySetResult(); }
            _slots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        await Task.WhenAll(clients).ConfigureAwait(false);
        _shutdown.Dispose();
        _slots.Dispose();
    }
}

internal sealed class HlsHardwareDecodeReport
{
    public string Status { get; set; } = "Running";
    public string ExecutionMode { get; init; } = "SyntheticHlsProductDecoderControl";
    public string Scope { get; init; } = "Explicit generated 60-second H.264/AAC HLS input, owned ephemeral authenticated loopback origin, linked product HlsHttpRelay and NativePlaybackEngine. Independent native transport/decoder control; no Emby API or coordinator negotiation.";
    public string[] NotVerified { get; init; } = ["Real Emby server compatibility or transcoding", "Coordinator negotiation or playback reporting", "Pixel capture", "Audible audio", "Other codecs, adapters, or drivers", "Long-running resource regression"];
    public bool HardwareRequested { get; init; }
    public bool HardwareRequired { get; init; }
    public string? RequiredVendor { get; init; }
    public string RequestedApi { get; init; } = "Auto";
    public bool ExpectedApiFallback { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Stage { get; set; } = "Initialization";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public HlsInputFile[] InputFiles { get; set; } = [];
    public long ManifestDurationTicks { get; set; }
    public List<HardwareDecodeObservation> DecodingSamples { get; init; } = [];
    public bool InitialMuted { get; set; }
    public long PauseStartTicks { get; set; }
    public long PauseEndTicks { get; set; }
    public long SeekTargetTicks { get; init; } = TimeSpan.FromSeconds(20).Ticks;
    public string SeekMethod { get; init; } = "ReopenAtSourcePosition";
    public bool PauseRestoredAfterSeek { get; set; }
    public long SeekObservedTicks { get; set; }
    public long ResumePositionTicks { get; set; }
    public bool PlayerDetachedAfterStop { get; set; }
    public bool StoppedSnapshotObserved { get; set; }
    public bool LastDecoderSnapshotRetainedAfterStop { get; set; }
    public bool NoUpstreamRequestsAfterStop { get; set; }
    public bool AllRequestsAuthenticated { get; set; }
    public int ManifestRequests { get; set; }
    public int SegmentRequests { get; set; }
    public SyntheticHlsRequest[] Requests { get; set; } = [];
    public bool ListenerClosed { get; set; }
    public bool EngineDisposed { get; set; }
    public ResourceSample? ResourcesAfterCleanup { get; set; }
    public List<string> Diagnostics { get; init; } = [];
}

internal sealed class HlsInputFile
{
    public string Name { get; init; } = "";
    public long Length { get; init; }
    public string Sha256 { get; init; } = "";
}

internal sealed class SyntheticHlsRequest
{
    public string Resource { get; init; } = "Rejected";
    public bool Authenticated { get; init; }
    public int StatusCode { get; set; }
    public int BytesSent { get; set; }
    public bool Completed { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HlsHardwareDecodeReport))]
internal partial class HlsHardwareDecodeJsonContext : JsonSerializerContext;
