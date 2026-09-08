using System.Net;
using EmbyClient.Api;

namespace EmbyClient.Playback.Tests;

internal sealed class PlaybackTestContext : IAsyncDisposable
{
    private int negotiationCount;

    public PlaybackTestContext(TimeProvider? timeProvider = null, TimeSpan? progressInterval = null)
    {
        Http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Api = new EmbyApiClient(Http, new Uri("https://server.example/emby/"),
            new ClientIdentity("Playback Test", "Test PC", "device-test", "0.1.0"), "test-token", "user-test");
        Handler.RespondAsync = (request, _) => Task.FromResult(Respond(request));
        Coordinator = new PlaybackCoordinator(Api, Engine, new PlaybackCoordinatorOptions
        {
            ProgressInterval = progressInterval ?? TimeSpan.FromMinutes(5),
            CleanupTimeout = TimeSpan.FromSeconds(3),
            ReportTimeout = TimeSpan.FromSeconds(3)
        }, timeProvider);
    }

    public RecordingHandler Handler { get; } = new();
    public FakePlaybackEngine Engine { get; } = new();
    public HttpClient Http { get; }
    public EmbyApiClient Api { get; }
    public PlaybackCoordinator Coordinator { get; }
    public Func<RecordedRequest, int, MediaSourceInfo[]> Sources { get; set; } = (_, _) => [Source()];

    public HttpResponseMessage Respond(RecordedRequest request)
    {
        if (request.Uri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal))
        {
            var count = Interlocked.Increment(ref negotiationCount);
            return RecordingHandler.PlaybackInfo(new PlaybackInfoResponse
            {
                PlaySessionId = "session-" + count,
                MediaSources = Sources(request, count)
            });
        }
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    public static MediaSourceInfo Source(string id = "source-a") => new()
    {
        Id = id,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        DirectStreamUrl = "/emby/Videos/movie-a/stream?Static=true&MediaSourceId=" + id,
        TranscodingUrl = "/emby/Videos/movie-a/master.m3u8?MediaSourceId=" + id,
        RunTimeTicks = TimeSpan.FromHours(2).Ticks,
        DefaultAudioStreamIndex = 1,
        DefaultSubtitleStreamIndex = -1,
        MediaStreams =
        [
            new() { Index = 0, Type = "Video", Codec = "h264" },
            new() { Index = 1, Type = "Audio", Codec = "aac" },
            new() { Index = 2, Type = "Audio", Codec = "aac" },
            new() { Index = 3, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode", IsTextSubtitleStream = true },
            new() { Index = 4, Type = "Subtitle", Codec = "vtt", DeliveryMethod = "External", IsTextSubtitleStream = true }
        ]
    };

    public static PlaybackSelection Selection(long position = 0, bool transcode = false) => new()
    {
        ItemId = "movie-a",
        StartPositionTicks = position,
        ForceTranscoding = transcode
    };

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        Http.Dispose();
    }
}
