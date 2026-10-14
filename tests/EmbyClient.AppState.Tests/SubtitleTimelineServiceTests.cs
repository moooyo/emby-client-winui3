using EmbyClient.Api;
using EmbyClient.App.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EmbyClient.AppState.Tests;

public sealed class SubtitleTimelineServiceTests
{
    private const string CueText = "WEBVTT\n\n00:01.125 --> 00:04.750\nDownloaded caption";
    private const string DeliveryUrl = "/emby/Videos/movie-a/source-a/Subtitles/9/Stream.vtt";
    private static readonly MediaSourceInfo Source = new() { Id = "source-a", RunTimeTicks = TimeSpan.FromMinutes(2).Ticks };
    private static readonly MediaStream Track = new() { Index = 9, Type = "Subtitle", Codec = "ass", IsTextSubtitleStream = true };

    [Fact]
    public async Task A_timeline_uses_negotiated_external_delivery_and_the_original_media_clock()
    {
        using var context = new TimelineContext(DeliveryUrl + "?StartPositionTicks=0&CopyTimestamps=false");

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Null(result.UnavailableReason);
        var cue = Assert.Single(Assert.IsType<SubtitleTimelineDocument>(result.Document).Cues);
        Assert.Equal(TimeSpan.FromMilliseconds(1125).Ticks, cue.StartTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(4750).Ticks, cue.EndTicks);
        Assert.Equal("Downloaded caption", cue.Preview);
        Assert.Equal(2, context.Requests.Count);
        var negotiation = context.Requests[0];
        Assert.Equal(HttpMethod.Post, negotiation.Method);
        using var body = JsonDocument.Parse(Assert.IsType<string>(negotiation.Body));
        Assert.Equal("source-a", body.RootElement.GetProperty("MediaSourceId").GetString());
        Assert.Equal(9, body.RootElement.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(0L, body.RootElement.GetProperty("StartTimeTicks").GetInt64());
        Assert.False(body.RootElement.GetProperty("IsPlayback").GetBoolean());
        Assert.False(body.RootElement.GetProperty("AutoOpenLiveStream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("EnableTranscoding").GetBoolean());
        var profiles = body.RootElement.GetProperty("DeviceProfile").GetProperty("SubtitleProfiles");
        Assert.Equal(new[] { "vtt", "srt" }, profiles.EnumerateArray().Select(profile => profile.GetProperty("Format").GetString()));
        Assert.All(profiles.EnumerateArray(), profile => Assert.Equal("External", profile.GetProperty("Method").GetString()));
        Assert.Equal("/proxy/emby/Videos/movie-a/source-a/Subtitles/9/Stream.vtt", context.Requests[1].Uri.AbsolutePath);
        Assert.Equal("token-a", context.Requests[1].Token);
    }

    [Fact]
    public async Task Missing_delivery_urls_use_the_negotiated_stream_index_in_the_official_text_route()
    {
        using var context = new TimelineContext(deliveryUrl: null);

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Document);
        Assert.EndsWith("/Videos/movie-a/source-a/Subtitles/9/Stream.vtt", context.Requests[1].Uri.AbsolutePath);
        Assert.Equal(string.Empty, context.Requests[1].Uri.Query);
    }

    [Theory]
    [InlineData("pgssub", true)]
    [InlineData("dvdsub", false)]
    [InlineData("subrip", false)]
    public async Task Bitmap_or_explicitly_non_text_tracks_are_unavailable_without_a_network_request(string codec, bool textFlag)
    {
        using var context = new TimelineContext(DeliveryUrl);

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source,
            Track with { Codec = codec, IsTextSubtitleStream = textFlag }, TestContext.Current.CancellationToken);

        Assert.Null(result.Document);
        Assert.Contains("format", result.UnavailableReason);
        Assert.Empty(context.Requests);
    }

    [Theory]
    [InlineData("Encode")]
    [InlineData("Embed")]
    public async Task Burned_or_embedded_delivery_is_not_represented_by_invented_cues(string method)
    {
        using var context = new TimelineContext(DeliveryUrl, deliveryMethod: method);

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Document);
        Assert.NotNull(result.UnavailableReason);
        Assert.Single(context.Requests);
    }

    [Fact]
    public async Task A_response_for_another_media_version_cannot_supply_the_selected_timeline()
    {
        using var context = new TimelineContext(DeliveryUrl, sourceId: "another-source");

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Document);
        Assert.Single(context.Requests);
    }

    [Theory]
    [InlineData("https://external.example/Stream.vtt?api_key=token-a")]
    [InlineData("http://server.example/emby/Videos/movie-a/source-a/Subtitles/9/Stream.vtt")]
    public async Task External_subtitle_hosts_are_not_contacted_with_account_headers_or_a_token_query(string deliveryUrl)
    {
        using var context = new TimelineContext(deliveryUrl);

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Document);
        Assert.Contains("external", result.UnavailableReason);
        Assert.Single(context.Requests);
    }

    [Theory]
    [InlineData(DeliveryUrl + "?StartPositionTicks=10000000")]
    [InlineData(DeliveryUrl + "?StartTimeTicks=10000000")]
    [InlineData(DeliveryUrl + "?EndPositionTicks=10000000")]
    [InlineData(DeliveryUrl + "?StartPositionTicks=0&startpositionticks=0")]
    [InlineData(DeliveryUrl + "?CopyTimestamps=true")]
    [InlineData(DeliveryUrl + "?StartPositionTicks=unknown")]
    [InlineData("/emby/Videos/movie-a/source-a/Subtitles/9/10000000/Stream.vtt")]
    [InlineData("/emby/Videos/movie-a/source-a/Subtitles/8/Stream.vtt")]
    [InlineData("/emby/Videos/movie-a/another-source/Subtitles/9/Stream.vtt")]
    [InlineData("/unconfirmed/captions.vtt")]
    public async Task An_unconfirmed_or_trimmed_time_reference_is_not_drawn_on_the_original_media_clock(string deliveryUrl)
    {
        using var context = new TimelineContext(deliveryUrl);

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Document);
        Assert.Contains("time reference", result.UnavailableReason);
        Assert.Single(context.Requests);
    }

    [Fact]
    public async Task The_zero_start_path_variant_preserves_real_cue_positions()
    {
        using var context = new TimelineContext("/Videos/movie-a/source-a/Subtitles/9/0/Stream.vtt");

        var result = await SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track,
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(1125).Ticks, Assert.Single(result.Document!.Cues).StartTicks);
    }

    [Fact]
    public async Task Cancellation_during_download_does_not_return_a_timeline_from_the_abandoned_request()
    {
        using var context = new TimelineContext(DeliveryUrl);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.Download = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var task = SubtitleTimelineService.LoadAsync(context.Api, "movie-a", Source, Track, cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(2, context.Requests.Count);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void SRT_retains_exact_intervals_and_overlaps_instead_of_joining_gaps(string newline)
    {
        var source = string.Join(newline, "10", "01:02:03,125 --> 01:02:07.250", "First caption", "",
            "11", "01:02:05,500 --> 01:02:08,000", "Second caption");

        var document = SubtitleTimelineService.Parse(".srt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(2, document.CueCount);
        Assert.False(document.IsSampled);
        Assert.Equal(TimeSpan.FromMilliseconds(3_723_125).Ticks, document.Cues[0].StartTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(3_727_250).Ticks, document.Cues[0].EndTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(3_725_500).Ticks, document.Cues[1].StartTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(3_728_000).Ticks, document.Cues[1].EndTicks);
    }

    [Fact]
    public void WebVTT_metadata_does_not_create_fake_cues_and_preview_markup_is_plain_text()
    {
        const string source = "WEBVTT\n\nSTYLE\n::cue { color: lime; }\n\nREGION\nid:main\n\n"
            + "NOTE Timestamps inside this block are comments\n00:30.000 --> 00:31.000\nNot a cue\n\n"
            + "narrator\n00:01.125 --> 00:04.750 align:center\n<v Narrator><i>Hello</i> &amp; welcome.</v>";

        var document = SubtitleTimelineService.Parse("vtt", Encoding.UTF8.GetBytes(source));

        var cue = Assert.Single(document.Cues);
        Assert.Equal(1, document.CueCount);
        Assert.Equal("Hello & welcome.", cue.Preview);
        Assert.Equal(TimeSpan.FromMilliseconds(1125).Ticks, cue.StartTicks);
    }

    [Fact]
    public void Projection_clips_to_the_known_duration_without_rewriting_the_downloaded_intervals()
    {
        var document = SubtitleTimelineService.Parse("srt", Encoding.UTF8.GetBytes(
            "00:00:01,000 --> 00:00:05,000\nFirst\n\n00:00:03,000 --> 00:00:09,000\nOverlap\n\n00:00:20,000 --> 00:00:22,000\nOutside"));

        var intervals = SubtitleTimelineService.Project(document, TimeSpan.FromSeconds(8).Ticks);

        Assert.Equal(2, intervals.Count);
        Assert.Equal(.125, intervals[0].StartFraction);
        Assert.Equal(.625, intervals[0].EndFraction);
        Assert.Equal(.375, intervals[1].StartFraction);
        Assert.Equal(1, intervals[1].EndFraction);
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, intervals[1].EndTicks);
        Assert.Equal(TimeSpan.FromSeconds(9).Ticks, intervals[1].Cue.EndTicks);
        Assert.Empty(SubtitleTimelineService.Project(document, 0));
    }

    [Fact]
    public void A_long_document_returns_a_bounded_sample_of_real_cues_across_the_file()
    {
        var source = new StringBuilder();
        for (var number = 0; number < 5000; number++)
        {
            var start = TimeSpan.FromSeconds(number * 2);
            var end = start + TimeSpan.FromSeconds(1);
            source.Append(start.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)).Append(",000 --> ")
                .Append(end.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)).Append(",000\nCue ").Append(number).Append("\n\n");
        }

        var document = SubtitleTimelineService.Parse("srt", Encoding.UTF8.GetBytes(source.ToString()));

        Assert.Equal(5000, document.CueCount);
        Assert.True(document.IsSampled);
        Assert.InRange(document.Cues.Count, 2, SubtitleTimelineService.MaximumSampleCues);
        Assert.Equal(0, document.Cues[0].StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(9998).Ticks, document.Cues[^1].StartTicks);
        Assert.All(document.Cues, cue =>
        {
            var originalNumber = cue.Number - 1;
            Assert.Equal(TimeSpan.FromSeconds(originalNumber * 2).Ticks, cue.StartTicks);
            Assert.Equal(cue.StartTicks + TimeSpan.TicksPerSecond, cue.EndTicks);
            Assert.Equal("Cue " + originalNumber, cue.Preview);
        });
    }

    [Fact]
    public void Documents_at_the_sample_limit_keep_every_real_cue()
    {
        var source = string.Concat(Enumerable.Repeat("00:00:00,000 --> 00:00:01,000\nCue\n\n", SubtitleTimelineService.MaximumSampleCues));

        var document = SubtitleTimelineService.Parse("srt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(SubtitleTimelineService.MaximumSampleCues, document.Cues.Count);
        Assert.False(document.IsSampled);
    }

    [Fact]
    public void Text_previews_are_bounded_without_splitting_Unicode_scalars()
    {
        var payload = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        var document = SubtitleTimelineService.Parse("srt", Encoding.UTF8.GetBytes("00:00:01,000 --> 00:00:02,000\n" + payload));

        var preview = Assert.Single(document.Cues).Preview;

        Assert.InRange(preview.Length, 1, SubtitleTimelineService.MaximumPreviewCharacters);
        Assert.EndsWith("\u2026", preview);
        _ = new UTF8Encoding(false, true).GetByteCount(preview);
    }

    [Theory]
    [InlineData("srt", "00:00:05,000 --> 00:00:01,000\nReversed")]
    [InlineData("srt", "00:60:00,000 --> 01:01:00,000\nInvalid minute")]
    [InlineData("srt", "00:00:60,000 --> 00:01:01,000\nInvalid second")]
    [InlineData("srt", "9999999999999999:00:00,000 --> 9999999999999999:00:01,000\nOverflow")]
    [InlineData("srt", "00:00:01,000 --> 00:00:02,000\n<i></i>")]
    [InlineData("srt", "00:00:01,000 --> 00:00:02,000\nFirst\n00:00:03,000 --> 00:00:04,000\nMissing separator")]
    [InlineData("vtt", "00:01.000 --> 00:02.000\nMissing signature")]
    [InlineData("vtt", "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\n00:01.000 --> 00:02.000\nUnknown origin")]
    [InlineData("vtt", "WEBVTT\n\n00:01,000 --> 00:02,000\nInvalid separator")]
    public void Invalid_cues_or_unknown_time_origins_never_return_a_partial_timeline(string format, string source)
    {
        Assert.Throws<FormatException>(() => SubtitleTimelineService.Parse(format, Encoding.UTF8.GetBytes(source)));
    }

    [Fact]
    public void Malformed_encoding_and_oversized_files_are_rejected()
    {
        Assert.Throws<FormatException>(() => SubtitleTimelineService.Parse("srt", [0xc3, 0x28]));
        Assert.Throws<FormatException>(() => SubtitleTimelineService.Parse("vtt", new byte[EmbyApiClient.MaximumTextSubtitleBytes + 1]));
    }

    [Fact]
    public void Too_many_cues_are_rejected_even_when_the_download_size_is_within_the_limit()
    {
        var source = string.Concat(Enumerable.Repeat("00:00:00,000 --> 00:00:01,000\nCue\n\n", SubtitleTimelineService.MaximumCues + 1));

        Assert.Throws<FormatException>(() => SubtitleTimelineService.Parse("srt", Encoding.UTF8.GetBytes(source)));
    }

    private sealed class TimelineContext : IDisposable
    {
        private readonly HttpClient http;
        public TimelineContext(string? deliveryUrl, string deliveryMethod = "External", string sourceId = "source-a")
        {
            var encodedUrl = deliveryUrl is null ? "null" : "\"" + JsonEncodedText.Encode(deliveryUrl) + "\"";
            Handler.Negotiation = $$"""
                {"MediaSources":[{"Id":"{{sourceId}}","MediaStreams":[{"Index":9,"Type":"Subtitle","Codec":"ass","IsTextSubtitleStream":true,
                  "DeliveryMethod":"{{deliveryMethod}}","DeliveryUrl":{{encodedUrl}}}]}]}
                """;
            http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Api = new EmbyApiClient(http, new Uri("https://server.example/proxy/emby/"),
                new ClientIdentity("Timeline tests", "Windows", "test-device", "1"), "token-a", "user-a");
        }
        public TimelineHandler Handler { get; } = new();
        public EmbyApiClient Api { get; }
        public List<TimelineRequest> Requests => Handler.Requests;
        public void Dispose() => http.Dispose();
    }

    private sealed record TimelineRequest(HttpMethod Method, Uri Uri, string? Body, string? Token);
    private sealed class TimelineHandler : HttpMessageHandler
    {
        public string Negotiation { get; set; } = "{}";
        public List<TimelineRequest> Requests { get; } = [];
        public Func<CancellationToken, Task<HttpResponseMessage>> Download { get; set; } = _ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CueText, Encoding.UTF8, "text/vtt") });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new TimelineRequest(request.Method, request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-Emby-Token", out var tokens) ? tokens.Single() : null));
            return request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Negotiation, Encoding.UTF8, "application/json") }
                : await Download(cancellationToken);
        }
    }
}
