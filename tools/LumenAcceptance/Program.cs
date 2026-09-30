using System.Net;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.LumenAcceptance;

ConnectedSession? createdSession = null;
ConnectionService? connection = null;
HttpClient? http = null;
try
{
    var options = BootstrapOptions.Parse(args);
    options.CheckOwnedOutput();
    Directory.CreateDirectory(options.RunDirectory);
    using var runLock = new FileStream(Path.Combine(options.RunDirectory, "bootstrap-once.lock"),
        FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
    http = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout.InfiniteTimeSpan };
    var officialCredentials = options.OfficialCredentialsFile is null ? null
        : await OfficialBootstrap.LoadOwnedCredentialsAsync(options, deadline.Token);
    if (officialCredentials is null)
        await VerifySyntheticEnvironmentAsync(http, options, deadline.Token);
    else
    {
        var publicApi = new EmbyApiClient(http, options.ServerUrl,
            new("LumenAcceptance", "Owned official Windows check", Guid.NewGuid().ToString("N"), "1.0"));
        var identity = await publicApi.GetPublicSystemInfoAsync(deadline.Token);
        if (identity.Id != officialCredentials.ServerId || identity.Version != officialCredentials.ExpectedServerVersion)
            throw new InvalidOperationException("The live official server does not match its owned deployment receipt.");
    }
    var expectedServerId = officialCredentials?.ServerId ?? options.ExpectedFixtureServerId;

    var settingsPath = Path.Combine(options.ProfileDirectory, "settings.json");
    var store = new AccountStore(settingsPath);
    connection = new ConnectionService(http, store);
    createdSession = await connection.SignInAsync(options.ServerUrl.AbsoluteUri,
        officialCredentials?.Username ?? "demo", officialCredentials?.Password ?? "demo", true, deadline.Token);
    if (createdSession.PersistenceWarning is not null || createdSession.Server.Id != expectedServerId
        || (officialCredentials is null && createdSession.User.Id != "synthetic-user-demo")
        || (officialCredentials is not null && !OfficialBootstrap.MatchesExpectedRole(
            createdSession.User.Policy?.IsAdministrator, officialCredentials.ExpectedIsAdministrator)))
        throw new InvalidOperationException("The dedicated test sign-in was not persisted under the expected identity.");
    await connection.SetThemeAsync(options.Theme);
    var saved = await new AccountStore(settingsPath).LoadAsync(deadline.Token);
    if (saved.Accounts.Count != 1 || saved.LastAccountKey != createdSession.AccountKey
        || !saved.Accounts[0].ProtectedToken.StartsWith("dpapi-user-v1:", StringComparison.Ordinal))
        throw new InvalidOperationException("The bootstrap did not produce a normal protected saved account.");

    using var restoringConnection = new ConnectionService(http, new AccountStore(settingsPath));
    var restored = await restoringConnection.RestoreAsync(saved.Accounts[0], deadline.Token);
    if (restored.AccountKey != createdSession.AccountKey || restored.PersistenceWarning is not null
        || restored.Server.Id != expectedServerId
        || (officialCredentials is not null && !OfficialBootstrap.MatchesExpectedRole(
            restored.User.Policy?.IsAdministrator, officialCredentials.ExpectedIsAdministrator)))
        throw new InvalidOperationException("The normal saved-account restore did not succeed.");

    var receipt = new BootstrapReceipt
    {
        Scope = officialCredentials is null
            ? "Synthetic local profile only; not real Emby compatibility or native UI evidence."
            : "Owned official Emby test account with synthetic media; not native UI evidence or broad compatibility.",
        CompletedAtUtc = DateTimeOffset.UtcNow,
        FixturePort = options.ServerUrl.Port,
        ServerId = restored.Server.Id!,
        UserId = restored.User.Id!,
        AccountKey = restored.AccountKey,
        Theme = options.Theme,
        ProfileDirectory = options.ProfileDirectory,
        ProductionRestoreVerified = true,
        OfficialServer = officialCredentials is not null,
        DesignCatalog = options.DesignCatalog,
        IsAdministrator = restored.User.Policy?.IsAdministrator == true
    };
    Directory.CreateDirectory(options.RunDirectory);
    await using (var output = new FileStream(options.ReceiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await JsonSerializer.SerializeAsync(output, receipt, AcceptanceJsonContext.Default.BootstrapReceipt, deadline.Token);
    Console.WriteLine("Owned acceptance profile prepared; production saved-account restore verified. No credentials were printed.");
    return 0;
}
catch (Exception error)
{
    if (createdSession is not null && connection is not null)
    {
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await connection.SignOutAsync(createdSession, cleanupDeadline.Token); }
        catch { /* Retain the generated evidence if task-owned cleanup is unavailable. */ }
    }
    var category = error switch
    {
        ArgumentException => "InvalidArguments",
        AccountStoreException accountError => "AccountStore:" + accountError.Error,
        EmbyApiException apiError => "AcceptanceHttp:" + (int)apiError.StatusCode,
        EmbyProtocolException => "AcceptanceProtocol",
        EmbyTransportException => "AcceptanceTransport",
        OperationCanceledException => "DeadlineExceeded",
        InvalidOperationException => "EnvironmentRejected",
        IOException => "ArtifactIO",
        UnauthorizedAccessException => "ArtifactAccess",
        _ => error.GetType().Name
    };
    Console.Error.WriteLine("Acceptance bootstrap failed: " + category + ". No exception payload or credentials were printed.");
    return 1;
}
finally
{
    connection?.Dispose();
    http?.Dispose();
}

static async Task VerifySyntheticEnvironmentAsync(HttpClient http, BootstrapOptions options, CancellationToken cancellationToken)
{
    using var response = await http.GetAsync(new Uri(options.ServerUrl, "_fixture/stats"),
        HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (response.StatusCode != HttpStatusCode.OK
        || !response.Headers.TryGetValues("X-Synthetic-Fixture", out var values)
        || !values.Contains(BootstrapOptions.FixtureHeader, StringComparer.Ordinal))
        throw new InvalidOperationException("The selected endpoint is not the expected synthetic fixture.");
    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int count;
    while ((count = await body.ReadAsync(chunk, cancellationToken)) != 0)
    {
        if (buffer.Length + count > 2 * 1024 * 1024)
            throw new InvalidOperationException("Synthetic fixture statistics exceeded their bounded size.");
        buffer.Write(chunk, 0, count);
    }
    using var statistics = JsonDocument.Parse(buffer.ToArray());
    if (!statistics.RootElement.TryGetProperty("Synthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.True
        || !statistics.RootElement.TryGetProperty("LumenCatalog", out var lumen) || lumen.ValueKind != JsonValueKind.True)
        throw new InvalidOperationException("The selected endpoint does not expose the rich synthetic acceptance catalog.");
    var isDesignCatalog = statistics.RootElement.TryGetProperty("LumenDesignCatalog", out var design)
        && design.ValueKind == JsonValueKind.True;
    if (isDesignCatalog != options.DesignCatalog)
        throw new InvalidOperationException("The selected synthetic catalog does not match the explicit bootstrap mode.");
    var api = new EmbyApiClient(http, options.ServerUrl, new("LumenAcceptance", "Synthetic Windows check", Guid.NewGuid().ToString("N"), "1.0"));
    var server = await api.GetPublicSystemInfoAsync(cancellationToken);
    if (server.Id != options.ExpectedFixtureServerId || server.Version != BootstrapOptions.FixtureVersion
        || server.ServerName != options.ExpectedFixtureServerName)
        throw new InvalidOperationException("Synthetic server identity did not match the acceptance allowlist.");
}
