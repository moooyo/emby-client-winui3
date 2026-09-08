using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EmbyClient.Api;

/// <summary>A typed, immutable server/user context. The caller owns the HTTP client.</summary>
public sealed class EmbyApiClient
{
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly string? _accessToken;
    private readonly string _publicIdentityHeader;
    private readonly string _authenticatedIdentityHeader;

    /// <summary>
    /// The supplied HTTP client must disable automatic redirects and have no default credentials.
    /// Use CreateHttpClient to obtain a transport with those properties.
    /// </summary>
    public EmbyApiClient(HttpClient httpClient, Uri apiRoot, ClientIdentity identity,
        string? accessToken = null, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(identity);
        _httpClient = httpClient;
        EnsureSafeDefaultHeaders();
        ApiRoot = NormalizeApiRoot(apiRoot);
        Identity = identity;
        ValidateHeaderValue(identity.Client, nameof(identity.Client));
        ValidateHeaderValue(identity.Device, nameof(identity.Device));
        ValidateHeaderValue(identity.DeviceId, nameof(identity.DeviceId));
        ValidateHeaderValue(identity.Version, nameof(identity.Version));
        if ((accessToken is null) != (userId is null))
        {
            throw new ArgumentException("An authenticated context requires both an access token and a user ID.");
        }

        if (accessToken is not null)
        {
            ValidateHeaderValue(accessToken, nameof(accessToken));
            ValidateHeaderValue(userId!, nameof(userId));
        }

        _accessToken = accessToken;
        UserId = userId;
        _publicIdentityHeader = $"Emby Client=\"{EscapeHeader(identity.Client)}\", Device=\"{EscapeHeader(identity.Device)}\", DeviceId=\"{EscapeHeader(identity.DeviceId)}\", Version=\"{EscapeHeader(identity.Version)}\"";
        _authenticatedIdentityHeader = userId is null
            ? _publicIdentityHeader
            : $"{_publicIdentityHeader}, UserId=\"{EscapeHeader(userId)}\"";
    }

    public Uri ApiRoot { get; }
    public ClientIdentity Identity { get; }
    public string? UserId { get; }
    public bool IsAuthenticated => _accessToken is not null && UserId is not null;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public EmbyApiClient WithAuthentication(string accessToken, string userId) =>
        new(_httpClient, ApiRoot, Identity, accessToken, userId) { RequestTimeout = RequestTimeout };

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync("System/Info/Public", null, EmbyJsonContext.Default.PublicSystemInfo, cancellationToken, false);

    public Task<UserDto[]> GetPublicUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync("Users/Public", null, EmbyJsonContext.Default.UserDtoArray, cancellationToken, false);

    public Task<AuthenticationResult> AuthenticateByNameAsync(string username, string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        return PostAsync("Users/AuthenticateByName", null, new AuthenticateByNameRequest(username, password),
            EmbyJsonContext.Default.AuthenticateByNameRequest, EmbyJsonContext.Default.AuthenticationResult,
            cancellationToken, false);
    }

    public Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync($"Users/{Segment(RequireUserId())}", null, EmbyJsonContext.Default.UserDto, cancellationToken);

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync("System/Info", null, EmbyJsonContext.Default.SystemInfo, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Post, "Sessions/Logout", null, null, cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetViewsAsync(bool includeExternalContent = false,
        CancellationToken cancellationToken = default) =>
        GetItemsResultAsync($"Users/{Segment(RequireUserId())}/Views",
            new Query().Add("IncludeExternalContent", includeExternalContent), cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetItemsAsync(ItemQuery? query = null,
        CancellationToken cancellationToken = default) =>
        GetItemsResultAsync($"Users/{Segment(RequireUserId())}/Items", BuildItemQuery(query ?? new()), cancellationToken);

    public Task<BaseItemDto> GetItemAsync(string itemId, CancellationToken cancellationToken = default) =>
        GetAsync($"Users/{Segment(RequireUserId())}/Items/{Segment(itemId)}", null,
            EmbyJsonContext.Default.BaseItemDto, cancellationToken);

    public Task<BaseItemDto[]> GetLatestItemsAsync(LatestItemsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new();
        ValidatePaging(null, query.Limit);
        return GetAsync($"Users/{Segment(RequireUserId())}/Items/Latest",
            new Query().Add("ParentId", query.ParentId).Add("Limit", query.Limit)
                .AddList("IncludeItemTypes", query.IncludeItemTypes).AddList("Fields", query.Fields)
                .Add("IsPlayed", query.IsPlayed).Add("GroupItems", query.GroupItems)
                .Add("EnableUserData", query.EnableUserData),
            EmbyJsonContext.Default.BaseItemDtoArray, cancellationToken);
    }

    public Task<QueryResult<BaseItemDto>> GetResumeItemsAsync(ItemQuery? query = null,
        CancellationToken cancellationToken = default) =>
        GetItemsResultAsync($"Users/{Segment(RequireUserId())}/Items/Resume",
            BuildItemQuery(query ?? new ItemQuery { Limit = 20, MediaTypes = ["Video"] }), cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetNextUpAsync(NextUpQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new();
        ValidatePaging(query.StartIndex, query.Limit);
        return GetItemsResultAsync("Shows/NextUp",
            new Query().Add("UserId", RequireUserId()).Add("ParentId", query.ParentId)
                .Add("SeriesId", query.SeriesId).Add("StartIndex", query.StartIndex).Add("Limit", query.Limit)
                .AddList("Fields", query.Fields).Add("EnableUserData", query.EnableUserData), cancellationToken);
    }

    public Task<QueryResult<BaseItemDto>> GetSeasonsAsync(string seriesId,
        CancellationToken cancellationToken = default) =>
        GetItemsResultAsync($"Shows/{Segment(seriesId)}/Seasons",
            new Query().Add("UserId", RequireUserId()).Add("EnableUserData", true), cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetEpisodesAsync(string seriesId, string? seasonId = null,
        CancellationToken cancellationToken = default) =>
        GetItemsResultAsync($"Shows/{Segment(seriesId)}/Episodes",
            new Query().Add("UserId", RequireUserId()).Add("SeasonId", seasonId).Add("EnableUserData", true),
            cancellationToken);

    public Task<UserItemDataDto> SetFavoriteAsync(string itemId, bool isFavorite,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(isFavorite ? HttpMethod.Post : HttpMethod.Delete,
            $"Users/{Segment(RequireUserId())}/FavoriteItems/{Segment(itemId)}", null, null,
            EmbyJsonContext.Default.UserItemDataDto, cancellationToken);

    public Task<UserItemDataDto> SetPlayedAsync(string itemId, bool isPlayed,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(isPlayed ? HttpMethod.Post : HttpMethod.Delete,
            $"Users/{Segment(RequireUserId())}/PlayedItems/{Segment(itemId)}", null, null,
            EmbyJsonContext.Default.UserItemDataDto, cancellationToken);

    public Task<byte[]> GetItemImageAsync(string itemId, string type = "Primary", int? index = null,
        ImageOptions? options = null, CancellationToken cancellationToken = default) =>
        GetImageAsync(BuildImagePath("Items", itemId, type, index), options, cancellationToken, true);

    public Task<byte[]> GetUserImageAsync(string userId, string type = "Primary", int? index = null,
        ImageOptions? options = null, CancellationToken cancellationToken = default) =>
        GetImageAsync(BuildImagePath("Users", userId, type, index), options, cancellationToken, IsAuthenticated);

    public Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string itemId, PlaybackInfoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSameUser(request.UserId);
        return PostAsync($"Items/{Segment(itemId)}/PlaybackInfo", null, request with { UserId = RequireUserId() },
            EmbyJsonContext.Default.PlaybackInfoRequest, EmbyJsonContext.Default.PlaybackInfoResponse,
            cancellationToken);
    }

    public Task ReportPlaybackStartAsync(PlaybackStartInfo report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidatePlaybackReport(report.ItemId, report.PlaySessionId, report.PositionTicks);
        return PostEmptyAsync("Sessions/Playing", null, report, EmbyJsonContext.Default.PlaybackStartInfo, cancellationToken);
    }

    public Task ReportPlaybackProgressAsync(PlaybackProgressInfo report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidatePlaybackReport(report.ItemId, report.PlaySessionId, report.PositionTicks);
        return PostEmptyAsync("Sessions/Playing/Progress", null, report,
            EmbyJsonContext.Default.PlaybackProgressInfo, cancellationToken);
    }

    public Task ReportPlaybackStoppedAsync(PlaybackStopInfo report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidatePlaybackReport(report.ItemId, report.PlaySessionId, report.PositionTicks);
        return PostEmptyAsync("Sessions/Playing/Stopped", null, report, EmbyJsonContext.Default.PlaybackStopInfo, cancellationToken);
    }

    public Task<LiveStreamResponse> OpenLiveStreamAsync(LiveStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OpenToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlaySessionId);
        EnsureSameUser(request.UserId);
        return PostAsync("LiveStreams/Open", null, request with { UserId = RequireUserId() },
            EmbyJsonContext.Default.LiveStreamRequest, EmbyJsonContext.Default.LiveStreamResponse, cancellationToken);
    }

    public Task CloseLiveStreamAsync(string liveStreamId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveStreamId);
        return SendEmptyAsync(HttpMethod.Post, "LiveStreams/Close", new Query().Add("LiveStreamId", liveStreamId),
            null, cancellationToken);
    }

    public Task StopActiveEncodingsAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playSessionId);
        return SendEmptyAsync(HttpMethod.Delete, "Videos/ActiveEncodings",
            new Query().Add("DeviceId", Identity.DeviceId).Add("PlaySessionId", playSessionId), null, cancellationToken);
    }

    public Task SetCapabilitiesAsync(string sessionId, ClientCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(capabilities);
        return PostEmptyAsync("Sessions/Capabilities/Full", new Query().Add("Id", sessionId), capabilities,
            EmbyJsonContext.Default.ClientCapabilities, cancellationToken);
    }

    /// <summary>Resolves a negotiated URL without adding or changing authentication query parameters.</summary>
    public Uri ResolveMediaUri(string mediaUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
        if (mediaUrl.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase))
        {
            mediaUrl = mediaUrl[6..];
        }

        if (!Uri.TryCreate(ApiRoot, mediaUrl, out var result) || !IsHttp(result)
            || result.UserInfo.Length != 0 || result.Fragment.Length != 0)
        {
            throw new ArgumentException("The media URL must be an HTTP or HTTPS URL without embedded credentials or a fragment.", nameof(mediaUrl));
        }

        return result;
    }

    /// <summary>Returns Emby headers only for the configured origin. Redirect handling remains the player's responsibility.</summary>
    public IReadOnlyDictionary<string, string> GetMediaRequestHeaders(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireUserId();
        if (!IsSameOrigin(target, ApiRoot))
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Emby-Authorization"] = _authenticatedIdentityHeader,
            ["X-Emby-Token"] = _accessToken!
        };
    }

    /// <summary>Constructs the original-byte HTTP fallback. It does not imply engine compatibility.</summary>
    public Uri BuildVideoStreamUri(string itemId, string mediaSourceId, string playSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(playSessionId);
        return BuildUri($"Videos/{Segment(itemId)}/stream", new Query()
            .Add("Static", true).Add("MediaSourceId", mediaSourceId).Add("PlaySessionId", playSessionId)
            .Add("DeviceId", Identity.DeviceId));
    }

    public static Uri NormalizeApiRoot(Uri serverAddress)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        if (!serverAddress.IsAbsoluteUri || !IsHttp(serverAddress) || serverAddress.UserInfo.Length != 0
            || serverAddress.Query.Length != 0 || serverAddress.Fragment.Length != 0)
        {
            throw new ArgumentException("Use an absolute HTTP or HTTPS server address without credentials, query, or fragment.", nameof(serverAddress));
        }

        var path = serverAddress.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Trim('/');
        if (!path.Equals("emby", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/emby", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Length == 0 ? "emby" : $"{path}/emby";
        }

        return new Uri($"{serverAddress.GetLeftPart(UriPartial.Authority)}/{path}/", UriKind.Absolute);
    }

    private async Task<QueryResult<BaseItemDto>> GetItemsResultAsync(string path, Query? query, CancellationToken ct)
    {
        var result = await GetAsync(path, query, EmbyJsonContext.Default.QueryResultBaseItemDto, ct).ConfigureAwait(false);
        if (result.Items is null)
        {
            throw new EmbyProtocolException("Emby returned a query result without an item array.");
        }

        return result;
    }

    private Task<T> GetAsync<T>(string path, Query? query, JsonTypeInfo<T> resultType,
        CancellationToken ct, bool authenticated = true) =>
        SendJsonAsync(HttpMethod.Get, path, query, null, resultType, ct, authenticated);

    private Task<TResponse> PostAsync<TRequest, TResponse>(string path, Query? query, TRequest payload,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        CancellationToken ct, bool authenticated = true) =>
        SendJsonAsync(HttpMethod.Post, path, query, CreateJsonContent(payload, requestType), responseType, ct, authenticated);

    private Task PostEmptyAsync<T>(string path, Query? query, T payload, JsonTypeInfo<T> requestType, CancellationToken ct) =>
        SendEmptyAsync(HttpMethod.Post, path, query, CreateJsonContent(payload, requestType), ct);

    private static ByteArrayContent CreateJsonContent<T>(T payload, JsonTypeInfo<T> typeInfo)
    {
        // Emby 4.9.5 rejects chunked JSON requests, so advertise the UTF-8 byte count before sending.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentLength = bytes.LongLength;
        return content;
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, Query? query, HttpContent? content,
        JsonTypeInfo<T> resultType, CancellationToken ct, bool authenticated = true)
    {
        using var request = CreateRequest(method, path, query, content, authenticated);
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync(stream, resultType, timeout.Token).ConfigureAwait(false);
            return result ?? throw new EmbyProtocolException("Emby returned an empty JSON value for a required response.");
        }
        catch (JsonException)
        {
            throw new EmbyProtocolException("Emby returned JSON that does not match the expected response contract.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new EmbyTransportException("The Emby server could not be reached or the connection failed.");
        }
    }

    private async Task SendEmptyAsync(HttpMethod method, string path, Query? query, HttpContent? content,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, path, query, content, true);
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new EmbyTransportException("The Emby server could not be reached or the connection failed.");
        }
    }

    private async Task<byte[]> GetImageAsync(string path, ImageOptions? options, CancellationToken ct, bool authenticated)
    {
        options ??= new();
        if (options.MaxWidth is <= 0 || options.MaxHeight is <= 0 || options.Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image dimensions must be positive and quality must be between 0 and 100.");
        }

        var query = new Query().Add("Tag", options.Tag).Add("MaxWidth", options.MaxWidth)
            .Add("MaxHeight", options.MaxHeight).Add("Format", options.Format).Add("Quality", options.Quality);
        using var request = CreateRequest(HttpMethod.Get, path, query, null, authenticated);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is > MaximumImageBytes)
            {
                throw new EmbyProtocolException("The requested image exceeds the client size limit.");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbyProtocolException("Emby returned a non-image response for an image request.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumImageBytes)
                {
                    throw new EmbyProtocolException("The requested image exceeds the client size limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby image request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new EmbyTransportException("The Emby image transfer failed.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, Query? query, HttpContent? content,
        bool authenticated)
    {
        EnsureSafeDefaultHeaders();
        if (authenticated) RequireUserId();
        var request = new HttpRequestMessage(method, BuildUri(path, query)) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Emby-Authorization", authenticated ? _authenticatedIdentityHeader : _publicIdentityHeader);
        if (authenticated) request.Headers.Add("X-Emby-Token", _accessToken!);
        return request;
    }

    private Uri BuildUri(string path, Query? query)
    {
        var relative = path.TrimStart('/');
        if (query is { Count: > 0 }) relative += "?" + query;
        return new Uri(ApiRoot, relative);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException("The request timeout must be a finite positive duration.");
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(RequestTimeout);
        return source;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string? applicationErrorCode = null;
        if (response.Headers.TryGetValues("X-Application-Error-Code", out var values)
            && values.Any(value => value.Equals("ParentalControl", StringComparison.Ordinal)))
        {
            applicationErrorCode = "ParentalControl";
        }

        throw new EmbyApiException(response.StatusCode, applicationErrorCode);
    }

    private string RequireUserId() => IsAuthenticated
        ? UserId!
        : throw new InvalidOperationException("This operation requires an authenticated Emby user context.");

    private void EnsureSafeDefaultHeaders()
    {
        foreach (var name in new[] { "Authorization", "X-Emby-Authorization", "X-Emby-Token", "Cookie" })
        {
            if (_httpClient.DefaultRequestHeaders.Contains(name))
            {
                throw new InvalidOperationException("The shared HTTP client must not contain default identity or credential headers.");
            }
        }
    }

    private void EnsureSameUser(string? userId)
    {
        var current = RequireUserId();
        if (userId is not null && !string.Equals(current, userId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request user does not match this client context.", nameof(userId));
        }
    }

    private static void ValidatePlaybackReport(string? itemId, string? playSessionId, long? positionTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(playSessionId);
        if (positionTicks < 0) throw new ArgumentOutOfRangeException(nameof(positionTicks));
    }

    private static Query BuildItemQuery(ItemQuery query)
    {
        ValidatePaging(query.StartIndex, query.Limit);
        return new Query().Add("ParentId", query.ParentId).Add("StartIndex", query.StartIndex)
            .Add("Limit", query.Limit).Add("Recursive", query.Recursive).Add("SearchTerm", query.SearchTerm)
            .AddList("IncludeItemTypes", query.IncludeItemTypes).AddList("MediaTypes", query.MediaTypes)
            .AddList("SortBy", query.SortBy).AddList("SortOrder", query.SortOrder).AddList("Fields", query.Fields)
            .AddList("Filters", query.Filters).AddList("Ids", query.Ids).AddList("Genres", query.Genres, '|')
            .AddList("Tags", query.Tags, '|').Add("IsPlayed", query.IsPlayed).Add("IsFavorite", query.IsFavorite)
            .Add("EnableUserData", query.EnableUserData).Add("EnableImages", query.EnableImages)
            .AddList("EnableImageTypes", query.EnableImageTypes).Add("ImageTypeLimit", query.ImageTypeLimit);
    }

    private static void ValidatePaging(int? startIndex, int? limit)
    {
        if (startIndex < 0) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static string BuildImagePath(string resource, string id, string type, int? index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var path = $"{resource}/{Segment(id)}/Images/{Segment(type)}";
        return index.HasValue ? $"{path}/{index.Value.ToString(CultureInfo.InvariantCulture)}" : path;
    }

    private static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or "..") throw new ArgumentException("An identifier cannot be a relative path segment.", nameof(value));
        return Uri.EscapeDataString(value);
    }

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSameOrigin(Uri first, Uri second) => IsHttp(first)
        && string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port && first.UserInfo.Length == 0;

    private static void ValidateHeaderValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("An identity or credential header contains an invalid value.", name);
        }
    }

    private static string EscapeHeader(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class Query
    {
        private readonly List<KeyValuePair<string, string>> _values = [];
        public int Count => _values.Count;

        public Query Add(string name, string? value)
        {
            if (value is not null) _values.Add(new(name, value));
            return this;
        }

        public Query Add(string name, bool? value) => Add(name, value.HasValue ? (value.Value ? "true" : "false") : null);
        public Query Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));
        public Query AddList(string name, string[]? values, char separator = ',') =>
            Add(name, values is { Length: > 0 } ? string.Join(separator, values) : null);

        public override string ToString() => string.Join("&", _values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }
}
