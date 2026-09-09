using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private async Task<StorageFile> LoadSyntheticFileAsync(CancellationToken ct)
    {
        Require(_controlMediaDirectory is not null, "SyntheticMediaDirectoryRequired");
        var metadataPath = Path.Combine(_controlMediaDirectory!, "fixture-h264-aac.json");
        var metadata = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(metadataPath, ct),
            ProbeJsonContext.Default.SyntheticMediaMetadata);
        var mediaPath = Path.Combine(_controlMediaDirectory!, "fixture-h264-aac.mp4");
        Require(metadata is { Synthetic: true, FileName: "fixture-h264-aac.mp4" }
            && metadata.DurationTicks is >= 590_000_000 and <= 610_000_000
            && string.Equals(metadata.VideoCodec, "H264", StringComparison.OrdinalIgnoreCase)
            && string.Equals(metadata.AudioCodec, "AAC", StringComparison.OrdinalIgnoreCase)
            && new FileInfo(mediaPath).Length == metadata.FileLength, "ExpectedSixtySecondSyntheticMedia");
        await using (var stream = File.OpenRead(mediaPath))
        {
            var header = new byte[12];
            await stream.ReadExactlyAsync(header, ct);
            Require(header.AsSpan(4, 4).SequenceEqual("ftyp"u8), "SyntheticMp4HeaderRequired");
        }
        return await StorageFile.GetFileFromPathAsync(mediaPath).AsTask(ct);
    }

    private async Task<NativeControlCycle> RunFileCycleAsync(StorageFile? file, int number,
        string mode, CancellationToken ct, Uri? nativeHttpUri = null)
    {
        var result = new NativeControlCycle { Number = number };
        using var managedFile = mode == "file-managed-stream-playback"
            ? new FileStream(file!.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous) : null;
        using var managedRandomAccessStream = managedFile?.AsRandomAccessStream();
        using var fileStream = mode == "file-stream-playback" ? await file!.OpenReadAsync().AsTask(ct) : null;
        using var source = nativeHttpUri is not null ? MediaSource.CreateFromUri(nativeHttpUri)
            : managedRandomAccessStream is not null
            ? MediaSource.CreateFromStream(managedRandomAccessStream, "video/mp4")
            : fileStream is not null ? MediaSource.CreateFromStream(fileStream, fileStream.ContentType)
            : MediaSource.CreateFromStorageFile(file!);
        var item = new MediaPlaybackItem(source);
        using var player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 1 };
        player.CommandManager.IsEnabled = false;
        var session = player.PlaybackSession;
        var retired = false;
        var nativeEvents = 0;
        var seekEvents = 0;
        string? failure = null;
        void Queue(Action action) => _window!.DispatcherQueue.TryEnqueue(() =>
        {
            if (retired) return;
            nativeEvents++;
            action();
        });
        void Sample() { _ = session.Position; _ = session.PlaybackState; }
        TypedEventHandler<MediaPlayer, object> opened = (_, _) => Queue(player.Play);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) => Queue(() => failure = "UnexpectedFilePlaybackEnd");
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, _) => Queue(() => failure = "NativeFilePlaybackFailed");
        TypedEventHandler<MediaPlaybackSession, object> state = (_, _) => Queue(Sample);
        TypedEventHandler<MediaPlaybackSession, object> position = (_, _) => Queue(Sample);
        TypedEventHandler<MediaPlaybackSession, object> seek = (_, _) => Queue(() => { seekEvents++; Sample(); });
        TypedEventHandler<MediaPlaybackSession, object> duration = (_, _) => Queue(Sample);
        TypedEventHandler<MediaPlaybackItem, IVectorChangedEventArgs> tracks = (_, _) => Queue(() => { });
        player.MediaOpened += opened;
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        session.PlaybackStateChanged += state;
        session.PositionChanged += position;
        session.SeekCompleted += seek;
        session.NaturalDurationChanged += duration;
        item.TimedMetadataTracksChanged += tracks;
        try
        {
            _element!.SetMediaPlayer(player);
            player.Source = item;
            await WaitAsync(() => failure is not null || session.PlaybackState == MediaPlaybackState.Playing
                && session.Position.Ticks > 2_000_000 && session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0,
                "NativeFileClockDidNotAdvance", ct);
            Require(failure is null, failure ?? "NativeFilePlaybackFailed");
            Require(player.IsMuted && session.NaturalDuration.Ticks is >= 590_000_000 and <= 610_000_000,
                "SyntheticFilePlaybackMetadataMismatch");
            result.PlayingPositionTicks = session.Position.Ticks;
            result.VideoWidth = session.NaturalVideoWidth;
            result.VideoHeight = session.NaturalVideoHeight;
            player.Pause();
            await WaitAsync(() => session.PlaybackState == MediaPlaybackState.Paused, "NativeFilePauseFailed", ct);
            await Task.Delay(150, ct);
            var pauseStart = session.Position.Ticks;
            await Task.Delay(650, ct);
            result.PauseDriftTicks = Math.Abs(session.Position.Ticks - pauseStart);
            Require(result.PauseDriftTicks <= 500_000, "NativeFilePausedClockAdvanced");
            result.SeekTargetTicks = TimeSpan.FromSeconds(8 + number % 10).Ticks;
            var priorSeeks = seekEvents;
            session.Position = TimeSpan.FromTicks(result.SeekTargetTicks);
            await WaitAsync(() => seekEvents > priorSeeks, "NativeFileSeekDidNotComplete", ct);
            result.SeekObservedTicks = session.Position.Ticks;
            Require(Math.Abs(result.SeekObservedTicks - result.SeekTargetTicks) <= 7_500_000, "NativeFileSeekMismatch");
            player.Play();
            await WaitAsync(() => session.PlaybackState == MediaPlaybackState.Playing
                && session.Position.Ticks > result.SeekObservedTicks + 2_000_000, "NativeFileResumeFailed", ct);
            result.ResumePositionTicks = session.Position.Ticks;
            await Task.Delay(300, ct);
            Require(failure is null, failure ?? "NativeFilePlaybackFailed");
            result.NativeEvents = nativeEvents;
        }
        finally
        {
            retired = true;
            _element!.SetMediaPlayer(null);
            result.PlayerDetached = _element.MediaPlayer is null;
            player.MediaOpened -= opened;
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
            session.PlaybackStateChanged -= state;
            session.PositionChanged -= position;
            session.SeekCompleted -= seek;
            session.NaturalDurationChanged -= duration;
            item.TimedMetadataTracksChanged -= tracks;
            player.Pause();
            player.Source = null;
        }
        Require(result.PlayerDetached, "NativeFilePlayerDidNotDetach");
        return result;
    }
}
