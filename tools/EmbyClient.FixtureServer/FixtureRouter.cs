using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal static class FixtureRouter
{
    public static async Task HandleAsync(HttpContext context, FixtureState state)
    {
        var request = context.Request;
        var path = request.Path.Value?.TrimEnd('/') ?? "";
        var method = request.Method;
        if (path is "" or "/_fixture")
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("SYNTHETIC local Emby development fixture. Use /emby with demo/demo. Read /_fixture/stats for counters. This is not an Emby Server or compatibility certification.", context.RequestAborted);
            return;
        }
        if (path == "/_fixture/stats" && method == "GET")
        {
            await Json(context, state.Stats(), FixtureJsonContext.Default.FixtureStats);
            return;
        }
        if (!path.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 404; return; }
        var parts = path[6..].Split('/');
        if (path.Equals("/emby/System/Info/Public", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await Json(context, state.PublicInfo, EmbyJsonContext.Default.PublicSystemInfo);
            return;
        }
        if (path.Equals("/emby/Users/Public", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await Json(context, new[] { state.User }, EmbyJsonContext.Default.UserDtoArray);
            return;
        }
        if (path.Equals("/emby/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var result = state.Login(await Body(context, EmbyJsonContext.Default.AuthenticateByNameRequest));
            if (result is null) { context.Response.StatusCode = 401; return; }
            await Json(context, result, EmbyJsonContext.Default.AuthenticationResult);
            return;
        }
        if (!state.IsAuthenticated(request))
        {
            state.AuthenticationFailed(); context.Response.StatusCode = 401; return;
        }
        var queryUser = request.Query["UserId"].ToString();
        if ((queryUser.Length > 0 && queryUser != FixtureState.UserId)
            || (parts[0] == "Users" && parts.Length > 1 && parts[1] != FixtureState.UserId))
        {
            context.Response.StatusCode = 403; return;
        }
        if (parts is ["System", "Info"] && method == "GET")
        {
            await Json(context, state.SystemInfo, EmbyJsonContext.Default.SystemInfo); return;
        }
        if (parts is ["Users", _] && method == "GET")
        {
            await Json(context, state.User, EmbyJsonContext.Default.UserDto); return;
        }
        if (parts is ["Users", _, "Views"] && method == "GET")
        {
            await Json(context, state.Views(), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts is ["Users", _, "Items"] && method == "GET")
        {
            await Json(context, state.Query(request.Query), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts is ["Users", _, "Items", "Latest"] && method == "GET")
        {
            await Json(context, state.Latest(request.Query), EmbyJsonContext.Default.BaseItemDtoArray); return;
        }
        if (parts is ["Users", _, "Items", "Resume"] && method == "GET")
        {
            await Json(context, state.Query(request.Query, resume: true, forceRecursive: true, operation: "Resume"), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts is ["Users", _, "Items", var itemId] && method == "GET")
        {
            var item = state.Item(itemId);
            if (item is null) { context.Response.StatusCode = 404; return; }
            await Json(context, item, EmbyJsonContext.Default.BaseItemDto); return;
        }
        if (parts is ["Users", _, var operation, var stateItemId]
            && operation is "FavoriteItems" or "PlayedItems" && method is "POST" or "DELETE")
        {
            var data = state.SetUserData(stateItemId, method == "POST", operation == "FavoriteItems");
            if (data is null) { context.Response.StatusCode = 404; return; }
            await Json(context, data, EmbyJsonContext.Default.UserItemDataDto); return;
        }
        if (parts is ["Shows", "NextUp"] && method == "GET")
        {
            await Json(context, state.Query(request.Query, nextUp: true, forceRecursive: true, operation: "NextUp"), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts is ["Shows", var seriesId, "Seasons"] && method == "GET")
        {
            await Json(context, state.Query(request.Query, seriesId, "Season", operation: "Seasons"), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts is ["Shows", var episodeSeriesId, "Episodes"] && method == "GET")
        {
            var seasonId = request.Query["SeasonId"].ToString();
            await Json(context, state.Query(request.Query, seasonId.Length > 0 ? seasonId : episodeSeriesId,
                "Episode", forceRecursive: true, operation: "Episodes"), EmbyJsonContext.Default.QueryResultBaseItemDto); return;
        }
        if (parts.Length >= 4 && parts[0] is "Items" or "Users" && parts[2] == "Images" && method is "GET" or "HEAD")
        {
            state.ImageStarted();
            try
            {
                var png = state.Poster(parts[0] == "Users" ? "1001" : parts[1]);
                if (png is null) { context.Response.StatusCode = 404; return; }
                if (state.Options.ImageDelayMilliseconds > 0)
                    await Task.Delay(state.Options.ImageDelayMilliseconds, context.RequestAborted);
                context.Response.Headers.CacheControl = "private, max-age=3600";
                await Results.Bytes(png, "image/png").ExecuteAsync(context);
                state.ImageCompleted(png.Length);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                state.ImageCanceled();
                throw;
            }
            finally { state.ImageEnded(); }
            return;
        }
        if (parts is ["Items", var playbackItemId, "PlaybackInfo"] && method == "POST")
        {
            var body = await Body(context, EmbyJsonContext.Default.PlaybackInfoRequest);
            if (body?.UserId is not null && body.UserId != FixtureState.UserId) { context.Response.StatusCode = 403; return; }
            if (state.Item(playbackItemId) is not { IsFolder: false }) { context.Response.StatusCode = 404; return; }
            if (state.ConsumePlaybackInfoFailure(body)) { context.Response.StatusCode = 503; return; }
            var response = state.PlaybackInfo(playbackItemId, body);
            if (response is null) { context.Response.StatusCode = 404; return; }
            await Json(context, response, EmbyJsonContext.Default.PlaybackInfoResponse); return;
        }
        if (parts is ["Videos", var streamItemId, "stream"] && method is "GET" or "HEAD")
        {
            var playSessionId = request.Query["PlaySessionId"].ToString();
            if (!state.OwnsPlayback(streamItemId, playSessionId)) { context.Response.StatusCode = 404; return; }
            state.MediaRequested(request.Headers.ContainsKey("Range"));
            await Results.File(state.Options.MediaPath, contentType: "video/mp4", enableRangeProcessing: true).ExecuteAsync(context);
            if (context.Response.StatusCode == StatusCodes.Status206PartialContent) state.PartialResponse();
            return;
        }
        if (parts is ["Sessions", "Playing"] && method == "POST")
        {
            var report = await Body(context, EmbyJsonContext.Default.PlaybackStartInfo);
            context.Response.StatusCode = report is not null && state.Record("Start", report.ItemId,
                report.PlaySessionId, report.PositionTicks, report.EventName) ? 204 : 400;
            return;
        }
        if (parts is ["Sessions", "Playing", "Progress"] && method == "POST")
        {
            var report = await Body(context, EmbyJsonContext.Default.PlaybackProgressInfo);
            context.Response.StatusCode = report is not null && state.Record("Progress", report.ItemId,
                report.PlaySessionId, report.PositionTicks, report.EventName) ? 204 : 400;
            return;
        }
        if (parts is ["Sessions", "Playing", "Stopped"] && method == "POST")
        {
            var report = await Body(context, EmbyJsonContext.Default.PlaybackStopInfo);
            context.Response.StatusCode = report is not null && state.Record("Stop", report.ItemId,
                report.PlaySessionId, report.PositionTicks, failed: report.Failed) ? 204 : 400;
            return;
        }
        if (parts is ["Sessions", "Capabilities", "Full"] && method == "POST")
        {
            await Body(context, EmbyJsonContext.Default.ClientCapabilities);
            state.CapabilityUpdated(); context.Response.StatusCode = 204; return;
        }
        if (parts is ["Sessions", "Logout"] && method == "POST")
        {
            state.Logout(request); context.Response.StatusCode = 204; return;
        }
        if (parts is ["Videos", "ActiveEncodings"] && method == "DELETE")
        {
            if (request.Query["DeviceId"].Count == 0 || request.Query["PlaySessionId"].Count == 0)
            { context.Response.StatusCode = 400; return; }
            state.EncodingCleaned(); context.Response.StatusCode = 204; return;
        }
        context.Response.StatusCode = 404;
    }

    private static async Task<T?> Body<T>(HttpContext context, JsonTypeInfo<T> typeInfo) =>
        await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted);

    private static async Task Json<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, context.RequestAborted);
    }
}
