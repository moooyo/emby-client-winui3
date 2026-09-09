using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the absolute path of a separately built EmbyClient.FixtureServer.dll.");

var serverPath = Path.GetFullPath(args[0]);
var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var testRoot = Path.GetFullPath(Path.Combine(temporaryRoot, "emby-fixture-boundary-checks-" + Guid.NewGuid().ToString("N")));
if (!string.Equals(Path.GetDirectoryName(testRoot), temporaryRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The owned test directory must be directly inside the system temporary directory.");
Directory.CreateDirectory(testRoot);
var bytes = new byte[4096];
Random.Shared.NextBytes(bytes);
"ftyp"u8.CopyTo(bytes.AsSpan(4));
await File.WriteAllBytesAsync(Path.Combine(testRoot, "fixture-h264-aac.mp4"), bytes);
await File.WriteAllTextAsync(Path.Combine(testRoot, "fixture-h264-aac.json"), """
    {"Synthetic":true,"FileName":"fixture-h264-aac.mp4","FileLength":4096,
     "DurationTicks":600000000,"Width":1280,"Height":720,"VideoCodec":"H264",
     "AudioCodec":"AAC","AudioChannels":2,"AudioSampleRate":48000}
    """);
var passed = 0;
try
{
    await DefaultBehavior();
    await DetailBoundaries();
    await MediaBoundary();
    await CleanupBoundaries();
    await InvalidConfiguration();
    Console.WriteLine($"PASS: {passed} HTTP fixture boundary checks. Test-only bytes were not decoded as media.");
}
finally
{
    // Only this run's newly created directory is removed; server outputs and existing fixtures are retained.
    Directory.Delete(testRoot, recursive: true);
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    passed++;
    Console.WriteLine("PASS: " + description);
}

async Task DefaultBehavior()
{
    await using var server = await TestServer.StartAsync(serverPath, testRoot);
    using var detail = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    Check(detail.StatusCode == HttpStatusCode.OK, "Default item details succeed.");
    var session = await server.NegotiateAsync("1001");
    using var media = await server.GetAsync(MediaUrl("1001", session));
    Check((await media.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Default media returns the original fixture bytes.");
    var stats = await server.StatsAsync();
    var controls = stats.GetProperty("BoundaryControls");
    Check(!controls.GetProperty("Configuration").GetProperty("Enabled").GetBoolean()
        && controls.GetProperty("RequestCount").GetInt64() == 0, "Boundary controls and observations are disabled by default.");
}

async Task DetailBoundaries()
{
    await using var server = await TestServer.StartAsync(serverPath, testRoot,
        "--item-detail-failure", "1001:2", "--item-detail-delay", "1001:2:150",
        "--item-detail-delay", "1001:3:3000", "--item-detail-delay", "1002:1:1");
    using var anonymous = new HttpClient { BaseAddress = server.Client.BaseAddress };
    using var unauthorized = await anonymous.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    using var forbidden = await server.GetAsync("/emby/Users/another-user/Items/1001");
    using var missing = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/missing");
    using var listing = await server.GetAsync("/emby/Users/synthetic-user-demo/Items?ParentId=movies");
    Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized && forbidden.StatusCode == HttpStatusCode.Forbidden
        && missing.StatusCode == HttpStatusCode.NotFound && listing.IsSuccessStatusCode,
        "Authentication, unknown items, and list requests preserve their normal status.");
    using var first = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    Check(first.StatusCode == HttpStatusCode.OK, "Invalid and list requests do not consume a detail attempt.");
    var clock = Stopwatch.StartNew();
    using var second = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    Check(second.StatusCode == HttpStatusCode.ServiceUnavailable && clock.ElapsedMilliseconds >= 120,
        "The configured detail attempt combines its delay with one HTTP 503.");

    using var cancellation = new CancellationTokenSource();
    var third = server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001", cancellation.Token);
    await server.WaitEventAsync("ItemDetail", "Entered", attempt: 3);
    Check(!third.IsCompleted, "The selected detail request remains in flight during its delay.");
    using var fourth = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    Check(fourth.StatusCode == HttpStatusCode.OK && !third.IsCompleted,
        "A concurrent later detail attempt is independent of the delayed request.");
    cancellation.Cancel();
    await ExpectCancellation(third);
    await server.WaitEventAsync("ItemDetail", "Canceled", attempt: 3);
    using var fifth = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1001");
    Check(fifth.StatusCode == HttpStatusCode.OK, "Canceling a detail request does not repeat the injected failure.");
    var stats = await server.StatsAsync();
    var events = BoundaryEvents(stats);
    Check(events.Count(item => Operation(item, "ItemDetail") && item.GetProperty("Phase").GetString() == "Completed"
        && item.GetProperty("StatusCode").GetInt32() == 503) == 1, "The failure is observed exactly once.");
    Check(events.Any(item => Operation(item, "ItemDetail") && item.GetProperty("Attempt").GetInt32() == 3
        && item.GetProperty("Phase").GetString() == "Canceled")
        && !events.Any(item => Operation(item, "ItemDetail") && item.GetProperty("Attempt").GetInt32() == 3
            && item.GetProperty("Phase").GetString() == "Completed"), "Canceled details never report a completed response.");
    for (var index = 0; index < 75; index++)
    {
        using var response = await server.GetAsync("/emby/Users/synthetic-user-demo/Items/1002");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("History setup request failed.");
    }
    stats = await server.StatsAsync();
    Check(BoundaryEvents(stats).Length == 200 && stats.GetProperty("BoundaryControls").GetProperty("EventCount").GetInt64() > 200,
        "Control history retains at most 200 events while total counters remain monotonic.");
    Check(!stats.GetRawText().Contains(server.Token, StringComparison.Ordinal), "Statistics do not contain the fixture access token.");
}

async Task MediaBoundary()
{
    await using var server = await TestServer.StartAsync(serverPath, testRoot, "--first-media-delay-ms", "4000");
    using var invalid = await server.GetAsync(MediaUrl("1001", "invalid-session"));
    Check(invalid.StatusCode == HttpStatusCode.NotFound, "Invalid media sessions do not consume the opening delay.");
    var firstSession = await server.NegotiateAsync("1001");
    using var cancellation = new CancellationTokenSource();
    using var headRequest = new HttpRequestMessage(HttpMethod.Head, MediaUrl("1001", firstSession));
    var head = server.Client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
    var clock = Stopwatch.StartNew();
    await server.WaitEventAsync("MediaOpening", "Entered");
    await Task.Delay(1500);
    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, MediaUrl("1001", firstSession));
    rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
    var range = server.Client.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead);
    await server.WaitEventCountAsync("MediaOpening", "Entered", 2);
    cancellation.Cancel();
    await ExpectCancellation(head);
    await server.WaitEventAsync("MediaOpening", "Canceled");
    Check(!range.IsCompleted, "Canceling the first HEAD does not release a concurrent media range.");
    var otherSession = await server.NegotiateAsync("1002");
    using var other = await server.GetAsync(MediaUrl("1002", otherSession));
    Check(other.IsSuccessStatusCode && !range.IsCompleted, "A different playback session is not held by the first-session gate.");
    using var rangeResponse = await range.WaitAsync(TimeSpan.FromSeconds(5));
    Check(rangeResponse.StatusCode == HttpStatusCode.PartialContent
        && (await rangeResponse.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[100..200])
        && clock.ElapsedMilliseconds >= 3750, "Concurrent range headers and bytes wait for the original media deadline.");
    var mediaEvents = BoundaryEvents(await server.StatsAsync());
    var rangeEntered = mediaEvents.Where(item => Operation(item, "MediaOpening") && item.GetProperty("Phase").GetString() == "Entered").Skip(1).First();
    var rangeReleased = mediaEvents.First(item => item.GetProperty("RequestSequence").GetInt64() == rangeEntered.GetProperty("RequestSequence").GetInt64()
        && item.GetProperty("Phase").GetString() == "DelayReleased");
    var rangeWait = rangeReleased.GetProperty("Timestamp").GetDateTimeOffset() - rangeEntered.GetProperty("Timestamp").GetDateTimeOffset();
    Check(rangeWait.TotalMilliseconds < 3300, "A late range shares the original deadline instead of starting a full new delay.");
    clock.Restart();
    using var later = await server.GetAsync(MediaUrl("1001", firstSession));
    Check(later.IsSuccessStatusCode && clock.ElapsedMilliseconds < 1500, "The released media gate is not restarted by later requests.");
    var stats = await server.StatsAsync();
    Check(stats.GetProperty("BoundaryControls").GetProperty("FirstMediaSessionId").GetString() == firstSession,
        "The first delayed media session is explicitly identified by its synthetic session ID.");
}

async Task CleanupBoundaries()
{
    await using var server = await TestServer.StartAsync(serverPath, testRoot, "--stop-delay-ms", "900", "--logout-delay-ms", "900");
    var session = await server.NegotiateAsync("1001");
    var report = $$"""{"ItemId":"1001","PlaySessionId":"{{session}}","PositionTicks":170000000,"Failed":false}""";
    using var start = await server.PostAsync("/emby/Sessions/Playing", report);
    using var invalid = await server.PostAsync("/emby/Sessions/Playing/Stopped", """{"ItemId":"1001","PlaySessionId":"unknown"}""");
    Check(start.IsSuccessStatusCode && invalid.StatusCode == HttpStatusCode.BadRequest,
        "Invalid Stop reports do not enter a boundary delay.");
    using var stopCancellation = new CancellationTokenSource();
    var canceledStop = server.PostAsync("/emby/Sessions/Playing/Stopped", report, stopCancellation.Token);
    await server.WaitEventAsync("Stop", "Entered");
    Check((await server.StatsAsync()).GetProperty("StopCount").GetInt32() == 0 && !canceledStop.IsCompleted,
        "A delayed Stop does not update playback state before its delay releases.");
    stopCancellation.Cancel();
    await ExpectCancellation(canceledStop);
    await server.WaitEventAsync("Stop", "Canceled");
    Check((await server.StatsAsync()).GetProperty("StopCount").GetInt32() == 0, "Canceling a delayed Stop prevents its synthetic state mutation.");
    var clock = Stopwatch.StartNew();
    using var stop = await server.PostAsync("/emby/Sessions/Playing/Stopped", report);
    Check(stop.StatusCode == HttpStatusCode.NoContent && clock.ElapsedMilliseconds >= 750
        && (await server.StatsAsync()).GetProperty("StopCount").GetInt32() == 1, "A completed delayed Stop applies exactly once.");

    using var logoutCancellation = new CancellationTokenSource();
    var canceledLogout = server.PostAsync("/emby/Sessions/Logout", "{}", logoutCancellation.Token);
    await server.WaitEventAsync("Logout", "Entered");
    using var whilePending = await server.GetAsync("/emby/Users/synthetic-user-demo");
    Check(whilePending.IsSuccessStatusCode && !canceledLogout.IsCompleted, "The token remains valid while Logout is delayed.");
    logoutCancellation.Cancel();
    await ExpectCancellation(canceledLogout);
    await server.WaitEventAsync("Logout", "Canceled");
    using var afterCanceled = await server.GetAsync("/emby/Users/synthetic-user-demo");
    Check(afterCanceled.IsSuccessStatusCode, "Canceling delayed Logout does not revoke the token.");
    clock.Restart();
    using var logout = await server.PostAsync("/emby/Sessions/Logout", "{}");
    using var afterLogout = await server.GetAsync("/emby/Users/synthetic-user-demo");
    Check(logout.StatusCode == HttpStatusCode.NoContent && clock.ElapsedMilliseconds >= 750
        && afterLogout.StatusCode == HttpStatusCode.Unauthorized, "Completed delayed Logout revokes the token after the boundary.");
    var stats = await server.StatsAsync();
    Check(stats.GetProperty("BoundaryControls").GetProperty("ActiveDelays").GetInt32() == 0,
        "All completed and canceled delay scopes leave zero active delays.");

    var queryToken = await server.LoginAsync();
    using var queryClient = new HttpClient { BaseAddress = server.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
    using var queryContent = new StringContent("{}", Encoding.UTF8, "application/json");
    var queryLogout = queryClient.PostAsync("/emby/Sessions/Logout?api_key=" + Uri.EscapeDataString(queryToken), queryContent);
    await server.WaitEventCountAsync("Logout", "Entered", 3);
    using var queryPending = await queryClient.GetAsync("/emby/Users/synthetic-user-demo?api_key=" + Uri.EscapeDataString(queryToken));
    Check(queryPending.IsSuccessStatusCode && !queryLogout.IsCompleted, "Query-token authentication remains valid while Logout is delayed.");
    using var queryLoggedOut = await queryLogout;
    using var queryAfter = await queryClient.GetAsync("/emby/Users/synthetic-user-demo?api_key=" + Uri.EscapeDataString(queryToken));
    Check(queryLoggedOut.StatusCode == HttpStatusCode.NoContent && queryAfter.StatusCode == HttpStatusCode.Unauthorized,
        "Logout revokes the same query token accepted by authentication.");
}

async Task InvalidConfiguration()
{
    string[][] invalidArguments =
    [
        ["--first-media-delay-ms", "30001"], ["--stop-delay-ms", "-1"],
        ["--item-detail-failure", "1001:0"], ["--item-detail-delay", "1001:1:0"],
        ["--item-detail-failure", "missing:1"], ["--item-detail-delay", "1001:1:30001"],
        ["--item-detail-failure", "1001:1", "--item-detail-failure", "1001:1"],
        ["--item-detail-delay", "1001:1:100", "--item-detail-delay", "1001:1:200"],
        Enumerable.Range(1, 33).SelectMany(attempt => new[] { "--item-detail-failure", $"1001:{attempt}" }).ToArray(),
        ["--logout-delay-ms"]
    ];
    foreach (var arguments in invalidArguments)
    {
        using var process = TestServer.CreateProcess(serverPath, testRoot, TestServer.FindPort(), arguments);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await Task.WhenAll(output, error);
        Check(process.ExitCode != 0 && !output.Result.Contains("listening at", StringComparison.Ordinal),
            "Invalid configuration is rejected before binding: " + string.Join(' ', arguments));
    }
}

static async Task ExpectCancellation(Task<HttpResponseMessage> request)
{
    try
    {
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        throw new InvalidOperationException("A canceled request unexpectedly completed.");
    }
    catch (OperationCanceledException) { }
}

static string MediaUrl(string itemId, string session) => $"/emby/Videos/{itemId}/stream?PlaySessionId={session}";
static JsonElement[] BoundaryEvents(JsonElement stats) => stats.GetProperty("BoundaryControls").GetProperty("Events").EnumerateArray().ToArray();
static bool Operation(JsonElement value, string operation) => value.GetProperty("Operation").GetString() == operation;

internal sealed class TestServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly List<string> _issuedTokens = [];
    public HttpClient Client { get; }
    public string Token { get; private set; } = "";

    private TestServer(Process process, int port)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public static async Task<TestServer> StartAsync(string serverPath, string mediaDirectory, params string[] controls)
    {
        var port = FindPort();
        var process = CreateProcess(serverPath, mediaDirectory, port, controls);
        process.Start();
        var server = new TestServer(process, port);
        try
        {
            var ready = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("The owned test fixture exited before readiness.");
                try
                {
                    using var response = await server.Client.GetAsync("/_fixture/stats");
                    if (response.IsSuccessStatusCode) { ready = true; break; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            if (!ready) throw new TimeoutException("The owned test fixture did not become ready.");
            server.Token = await server.LoginAsync();
            server.Client.DefaultRequestHeaders.Add("X-Emby-Token", server.Token);
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken token = default) => Client.GetAsync(path, token);

    public async Task<string> LoginAsync()
    {
        using var login = await PostAsync("/emby/Users/AuthenticateByName", """{"Username":"demo","Pw":"demo"}""");
        login.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = payload.RootElement.GetProperty("AccessToken").GetString()!;
        _issuedTokens.Add(token);
        return token;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, string body, CancellationToken token = default)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await Client.PostAsync(path, content, token);
    }

    public async Task<string> NegotiateAsync(string itemId)
    {
        using var response = await PostAsync($"/emby/Items/{itemId}/PlaybackInfo", """{"UserId":"synthetic-user-demo","IsPlayback":true}""");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("PlaySessionId").GetString()!;
    }

    public async Task<JsonElement> StatsAsync()
    {
        using var response = await GetAsync("/_fixture/stats");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.Clone();
    }

    public Task WaitEventAsync(string operation, string phase, int? attempt = null) => WaitEventCountAsync(operation, phase, 1, attempt);

    public async Task WaitEventCountAsync(string operation, string phase, int count, int? attempt = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            var stats = await StatsAsync();
            var matches = stats.GetProperty("BoundaryControls").GetProperty("Events").EnumerateArray().Count(item =>
                item.GetProperty("Operation").GetString() == operation && item.GetProperty("Phase").GetString() == phase
                && (attempt is null || item.GetProperty("Attempt").GetInt32() == attempt));
            if (matches >= count) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Expected boundary event was not observed: {operation}/{phase}.");
    }

    public static int FindPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static Process CreateProcess(string serverPath, string mediaDirectory, int port, string[] controls)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(serverPath);
        start.ArgumentList.Add("--media-dir");
        start.ArgumentList.Add(mediaDirectory);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var control in controls) start.ArgumentList.Add(control);
        return new Process { StartInfo = start };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        var logs = string.Join('\n', await Task.WhenAll(_output, _error));
        _process.Dispose();
        if (_issuedTokens.Any(token => logs.Contains(token, StringComparison.Ordinal)))
            throw new InvalidOperationException("The owned fixture unexpectedly logged its access token.");
    }
}
