using System.Globalization;
using System.Net;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.FixtureServer;

var options = FixtureOptions.Parse(args);
var state = new FixtureState(options);
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, options.Port);
    server.Limits.MaxRequestBodySize = 1024 * 1024;
});
var app = builder.Build();
app.Run(async context =>
{
    context.Response.Headers["X-Synthetic-Fixture"] = "EmbyClient development data; not Emby Server";
    try
    {
        await FixtureRouter.HandleAsync(context, state);
    }
    catch (JsonException)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
    catch (BadHttpRequestException)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // An intentionally canceled image or range transfer does not create a synthetic server fault.
    }
});
Console.WriteLine($"SYNTHETIC Emby fixture listening at http://127.0.0.1:{options.Port}/emby/");
Console.WriteLine("Development data only. This service does not establish Emby compatibility.");
Console.WriteLine("Read playback counters at /_fixture/stats. Request bodies and credentials are not logged.");
if (options.LargeLibraryItems > 0)
    Console.WriteLine($"Large-library mode: {options.LargeLibraryItems} synthetic movies in a separate library.");
if (options.FailFirstPlaybackInfo)
    Console.WriteLine("The first valid PlaybackInfo POST with IsPlayback=true will return a synthetic HTTP 503.");
if (options.BoundaryControls is { Enabled: true })
    Console.WriteLine("Opt-in boundary controls are enabled. Read bounded, credential-free observations at /_fixture/stats.");
await app.RunAsync();

namespace EmbyClient.FixtureServer
{
    internal sealed record FixtureOptions(int Port, string MediaPath, MediaFixtureMetadata Media,
        int LargeLibraryItems = 0, int ImageDelayMilliseconds = 0, bool FailFirstPlaybackInfo = false,
        FixtureBoundaryOptions? BoundaryControls = null)
    {
        public static FixtureOptions Parse(string[] arguments)
        {
            var port = 18960;
            string? mediaDirectory = null;
            var largeLibraryItems = 0;
            var imageDelayMilliseconds = 0;
            var failFirstPlaybackInfo = false;
            var boundaryOptions = new FixtureBoundaryOptionsBuilder();
            for (var index = 0; index < arguments.Length; index++)
            {
                switch (arguments[index])
                {
                    case "--port" when index + 1 < arguments.Length:
                        if (!int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                            || port is < 1024 or > 65535)
                            throw new ArgumentException("The fixture port must be between 1024 and 65535.");
                        break;
                    case "--media-dir" when index + 1 < arguments.Length:
                        mediaDirectory = Path.GetFullPath(arguments[++index]);
                        break;
                    case "--large-library-items" when index + 1 < arguments.Length:
                        if (!int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out largeLibraryItems)
                            || largeLibraryItems is < 1 or > 10000)
                            throw new ArgumentException("The large-library count must be between 1 and 10000.");
                        break;
                    case "--image-delay-ms" when index + 1 < arguments.Length:
                        if (!int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out imageDelayMilliseconds)
                            || imageDelayMilliseconds is < 0 or > 1000)
                            throw new ArgumentException("The synthetic image delay must be between 0 and 1000 milliseconds.");
                        break;
                    case "--fail-first-playback-info":
                        failFirstPlaybackInfo = true;
                        break;
                    case "--item-detail-failure" when index + 1 < arguments.Length:
                        boundaryOptions.AddDetailFailure(arguments[++index]);
                        break;
                    case "--item-detail-delay" when index + 1 < arguments.Length:
                        boundaryOptions.AddDetailDelay(arguments[++index]);
                        break;
                    case "--first-media-delay-ms" when index + 1 < arguments.Length:
                        boundaryOptions.FirstMediaDelayMilliseconds = FixtureBoundaryOptionsBuilder.ParseDelay(arguments[++index]);
                        break;
                    case "--stop-delay-ms" when index + 1 < arguments.Length:
                        boundaryOptions.StopDelayMilliseconds = FixtureBoundaryOptionsBuilder.ParseDelay(arguments[++index]);
                        break;
                    case "--logout-delay-ms" when index + 1 < arguments.Length:
                        boundaryOptions.LogoutDelayMilliseconds = FixtureBoundaryOptionsBuilder.ParseDelay(arguments[++index]);
                        break;
                    default:
                        throw new ArgumentException("Usage: EmbyClient.FixtureServer --media-dir <directory> [--port 18960] [--large-library-items 5000] [--image-delay-ms 100] [--fail-first-playback-info] [--item-detail-failure <id>:<attempt>] [--item-detail-delay <id>:<attempt>:<ms>] [--first-media-delay-ms <ms>] [--stop-delay-ms <ms>] [--logout-delay-ms <ms>]");
                }
            }

            if (mediaDirectory is null) throw new ArgumentException("--media-dir is required. Generate the synthetic MP4 before starting the fixture server.");
            var path = Path.Combine(mediaDirectory, "fixture-h264-aac.mp4");
            if (!File.Exists(path) || new FileInfo(path).Length < 1024)
                throw new InvalidOperationException("The generated fixture-h264-aac.mp4 is missing or too small. No empty media fallback is provided.");
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[4..8].SequenceEqual("ftyp"u8))
                throw new InvalidOperationException("The fixture media does not have the expected MP4 file header.");
            var metadataPath = Path.Combine(mediaDirectory, "fixture-h264-aac.json");
            if (!File.Exists(metadataPath))
                throw new InvalidOperationException("The generated media metadata is missing. Run EmbyClient.MediaFixtures to create the MP4 and its metadata together.");
            var metadata = JsonSerializer.Deserialize(File.ReadAllBytes(metadataPath), FixtureJsonContext.Default.MediaFixtureMetadata);
            if (metadata is null || !metadata.Synthetic || metadata.FileName != "fixture-h264-aac.mp4"
                || metadata.FileLength != stream.Length || metadata.DurationTicks <= 0
                || metadata.Width <= 0 || metadata.Height <= 0 || metadata.AudioChannels <= 0
                || !string.Equals(metadata.VideoCodec, "H264", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(metadata.AudioCodec, "AAC", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The generated media metadata does not match the synthetic MP4.");
            return new FixtureOptions(port, path, metadata, largeLibraryItems, imageDelayMilliseconds,
                failFirstPlaybackInfo, boundaryOptions.Build());
        }
    }

    internal sealed class MediaFixtureMetadata
    {
        public bool Synthetic { get; init; }
        public string? FileName { get; init; }
        public long FileLength { get; init; }
        public long DurationTicks { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public int AudioChannels { get; init; }
        public int AudioSampleRate { get; init; }
        public long FrameRateNumerator { get; init; }
        public long FrameRateDenominator { get; init; }
    }
}
