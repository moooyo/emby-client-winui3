using System.Runtime.CompilerServices;
using System.Text.Json;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace EmbyClient.VlcProbe;

public sealed partial class App : Application
{
    private readonly TaskCompletionSource<string[]> _viewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _milestones = [];
    private Window? _window;
    private VideoView? _view;
    private string? _resultPath;
    private string _status = "Starting";
    private string? _error;
    private long _maximumTime;
    private int _playingEvents;
    private int _errorEvents;
    private int _decodedVideo;
    private int _displayedPictures;
    private uint _videoOutputs;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            _status = "Failed";
            _error = Describe(args.Exception);
            Save();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (commandLine is not ["--media", var mediaPath, "--output-dir", var directory])
                throw new ArgumentException("Usage: EmbyClient.VlcProbe --media <synthetic-mp4> --output-dir <new-directory>");

            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var resultPath = Path.Combine(directory, "result.json");
            if (File.Exists(resultPath))
                throw new ArgumentException("A result already exists. Select a new output directory.");
            _resultPath = resultPath;
            Save();

            mediaPath = Path.GetFullPath(mediaPath);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(mediaPath, ".json")));
            Require(Path.GetFileName(mediaPath) == "fixture-h264-aac.mp4"
                && metadata.RootElement.GetProperty("Synthetic").GetBoolean()
                && metadata.RootElement.GetProperty("FileLength").GetInt64() == new FileInfo(mediaPath).Length,
                "Expected the generated synthetic H.264/AAC media fixture.");
            Milestone("SyntheticFixtureAccepted");

            Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
            Milestone("CoreInitialized");
            var probeWindow = new ProbeWindow(_viewReady, Milestone);
            _view = probeWindow.Video;
            _window = probeWindow;
            _window.AppWindow.Resize(new SizeInt32(480, 300));
            _window.AppWindow.Show(activateWindow: false);
            _view.ApplyTemplate();
            _ = RunAsync(mediaPath);
        }
        catch (Exception exception)
        {
            _status = "Failed";
            _error = Describe(exception);
            Save();
            Environment.ExitCode = 1;
            Exit();
        }
    }

    private async Task RunAsync(string mediaPath)
    {
        LibVLC? libVlc = null;
        Media? media = null;
        MediaPlayer? player = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var swapChainOptions = await _viewReady.Task.WaitAsync(timeout.Token);
            Milestone("VideoViewInitialized");
            libVlc = new LibVLC(swapChainOptions.Concat(["--quiet"]).ToArray());
            Milestone("LibVlcCreated");
            media = new Media(libVlc, new Uri(mediaPath));
            player = new MediaPlayer(libVlc) { Mute = true, Volume = 0, EnableHardwareDecoding = true };
            player.Playing += (_, _) => Interlocked.Increment(ref _playingEvents);
            player.EncounteredError += (_, _) => Interlocked.Increment(ref _errorEvents);
            player.TimeChanged += (_, change) => Interlocked.Exchange(ref _maximumTime, Math.Max(Interlocked.Read(ref _maximumTime), change.Time));
            _view!.MediaPlayer = player;
            Require(player.Play(media), "LibVLC rejected the synthetic media.");
            Milestone("PlayAccepted");
            await UntilAsync(() => Interlocked.Read(ref _maximumTime) >= 2000 && player.VoutCount > 0, timeout.Token);
            _decodedVideo = media.Statistics.DecodedVideo;
            _displayedPictures = media.Statistics.DisplayedPictures;
            _videoOutputs = player.VoutCount;
            Require(player.Mute && player.Volume == 0, "The probe must remain muted.");
            Require(_decodedVideo > 0 && _displayedPictures > 0, "No decoded and displayed video frames were reported.");
            Milestone("VideoFramesReported");

            player.SetPause(true);
            await UntilAsync(() => player.State == VLCState.Paused, timeout.Token);
            Milestone("Paused");
            Require(player.IsSeekable, "The local fixture must be seekable.");
            player.Time = 4000;
            player.SetPause(false);
            await UntilAsync(() => player.Time >= 4500 && player.State == VLCState.Playing, timeout.Token);
            Milestone("SeekAndResumeObserved");
            Require(Volatile.Read(ref _errorEvents) == 0, "LibVLC reported a playback error.");
            _status = "Passed";
        }
        catch (Exception exception)
        {
            _status = "Failed";
            _error = Describe(exception);
        }
        finally
        {
            try
            {
                if (player is not null)
                {
                    await Task.Run(player.Stop);
                    Milestone("Stopped");
                    if (_view is not null)
                        _view.MediaPlayer = null;
                    player.Dispose();
                    Milestone("PlayerDisposed");
                }
                media?.Dispose();
                libVlc?.Dispose();
                Milestone("MediaAndLibVlcDisposed");
            }
            catch (Exception exception)
            {
                _status = "Failed";
                _error = (_error is null ? "" : _error + Environment.NewLine) + Describe(exception);
            }
            Save();
            Environment.ExitCode = _status == "Passed" ? 0 : 1;
            _window?.Close();
            Exit();
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
            await Task.Delay(100, cancellationToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Describe(Exception exception) => $"HRESULT 0x{exception.HResult:X8}: {exception}";

    private void Milestone(string value)
    {
        _milestones.Add(value);
        Save();
    }

    private void Save()
    {
        if (_resultPath is null)
            return;
        using var stream = File.Create(_resultPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("Status", _status);
        writer.WriteBoolean("DynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
        writer.WriteNumber("ProcessId", Environment.ProcessId);
        writer.WriteString("UpdatedUtc", DateTimeOffset.UtcNow);
        writer.WriteString("Error", _error);
        writer.WriteNumber("MaximumTimeMs", Interlocked.Read(ref _maximumTime));
        writer.WriteNumber("PlayingEvents", Volatile.Read(ref _playingEvents));
        writer.WriteNumber("ErrorEvents", Volatile.Read(ref _errorEvents));
        writer.WriteNumber("DecodedVideo", _decodedVideo);
        writer.WriteNumber("DisplayedPictures", _displayedPictures);
        writer.WriteNumber("VideoOutputs", _videoOutputs);
        writer.WriteStartArray("Milestones");
        foreach (var milestone in _milestones)
            writer.WriteStringValue(milestone);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
