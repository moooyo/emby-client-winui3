using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AccountCancellationTests
{
    [Theory(Timeout = 15000)]
    [InlineData(RequestBoundary.Authentication, false)]
    [InlineData(RequestBoundary.CurrentUser, false)]
    [InlineData(RequestBoundary.CurrentUser, true)]
    [InlineData(RequestBoundary.Capabilities, false)]
    [InlineData(RequestBoundary.Capabilities, true)]
    public async Task Cancelling_a_new_sign_in_preserves_the_previous_account_and_revokes_only_a_known_new_token(
        RequestBoundary boundary, bool lateResponse)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.PauseAt = boundary;
        scenario.Handler.IgnoreRequestCancellation = lateResponse;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        scenario.Handler.ReleaseRequest.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Equal(boundary == RequestBoundary.Authentication ? [] : new[] { "new-token-1" },
            scenario.Handler.LogoutTokens.ToArray());
    }

    [Theory(Timeout = 15000)]
    [InlineData(RequestBoundary.PublicInfo)]
    [InlineData(RequestBoundary.CurrentUser)]
    public async Task Cancelling_saved_account_restore_keeps_both_saved_credentials_and_never_revokes_them(
        RequestBoundary boundary)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.PauseAt = boundary;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.Service.RestoreAsync(scenario.TargetAccount, cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Empty(scenario.Handler.LogoutTokens);
        Assert.Equal(0, scenario.Handler.AuthenticationRequests);
        Assert.Equal(0, scenario.Handler.CapabilityRequests);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_waiting_for_settings_persistence_never_publishes_the_new_account(bool restore)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var storeGate = PrivateGate(scenario.Fixture.Store, "_gate");
        var settingsGate = PrivateGate(scenario.Service, "_settingsGate");
        var context = new QueuedContext();
        await storeGate.WaitAsync(token);
        try
        {
            var operation = context.Start(() => restore
                ? scenario.Service.RestoreAsync(scenario.TargetAccount, cancelled.Token)
                : scenario.SignInAsync(cancelled.Token, remember: true));
            await context.RunUntilAsync(() => settingsGate.CurrentCount == 0, token);
            Assert.False(operation.IsCompleted);
            cancelled.Cancel();
            await context.RunUntilAsync(() => operation.IsCompleted, token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally { storeGate.Release(); }

        await scenario.AssertUnchangedAsync(token);
        Assert.Equal(restore ? [] : new[] { "new-token-1" }, scenario.Handler.LogoutTokens.ToArray());
        Assert.Empty(Directory.GetFiles(scenario.Fixture.DirectoryPath, "*.tmp"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_atomic_settings_commit_keeps_the_successful_connection(bool restore)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var context = new QueuedContext();
        var storeGate = PrivateGate(scenario.Fixture.Store, "_gate");
        var settingsGate = PrivateGate(scenario.Service, "_settingsGate");
        Task<ConnectedSession> operation;
        await storeGate.WaitAsync(token);
        try
        {
            operation = context.Start(() => restore
                ? scenario.Service.RestoreAsync(scenario.TargetAccount, cancelled.Token)
                : scenario.SignInAsync(cancelled.Token, remember: true));
            // Force SaveAsync to suspend even if the file I/O would complete synchronously.
            await context.RunUntilAsync(() => settingsGate.CurrentCount == 0, token);
        }
        finally { storeGate.Release(); }
        var cancelledAfterCommit = false;
        while (!operation.IsCompleted)
        {
            var continuation = await context.TakeAsync(token);
            var persisted = await scenario.Fixture.Store.LoadAsync(token);
            if (!cancelledAfterCommit && persisted.LastAccountKey == scenario.TargetAccount.Key)
            {
                // File.Move has finished, but the service has not resumed from SaveAsync.
                Assert.Same(scenario.OriginalSettings, scenario.Service.Settings);
                cancelled.Cancel();
                cancelledAfterCommit = true;
            }
            context.Run(continuation);
        }

        var session = await operation;
        Assert.True(cancelledAfterCommit);
        Assert.Equal(scenario.TargetAccount.Key, session.AccountKey);
        Assert.Equal(session.AccountKey, scenario.Service.Settings.LastAccountKey);
        var saved = await scenario.Fixture.Store.LoadAsync(token);
        Assert.Equal(session.AccountKey, saved.LastAccountKey);
        Assert.Equal("Dark", saved.Theme);
        var savedAccount = Assert.Single(saved.Accounts, account => account.Key == session.AccountKey);
        Assert.Equal(restore ? "saved-token" : "new-token-1",
            await scenario.Fixture.Store.UnprotectTokenAsync(savedAccount.ProtectedToken, token));
        Assert.Empty(scenario.Handler.LogoutTokens);
        Assert.Equal(!restore, session.CapabilitiesRegistered);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_authentication_that_reuses_a_saved_token_does_not_sign_out_the_existing_account()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.AuthenticationToken = "saved-token";
        scenario.Handler.PauseAt = RequestBoundary.Capabilities;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Empty(scenario.Handler.LogoutTokens);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_a_reused_unsaved_or_temporary_session_token_keeps_the_accepted_session(bool temporary)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token, temporary);
        scenario.Handler.AuthenticationToken = "accepted-token";
        var accepted = await scenario.SignInAsync(token);
        await scenario.CaptureAsync(token);
        scenario.Handler.PauseAt = RequestBoundary.CurrentUser;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Empty(scenario.Handler.LogoutTokens);
        scenario.Handler.PauseAt = RequestBoundary.None;
        Assert.Equal("user-b", (await accepted.Api.GetCurrentUserAsync(token)).Id);
        Assert.Equal("accepted-token", scenario.Handler.CurrentUserTokens.Last());
    }

    [Fact(Timeout = 15000)]
    public async Task An_unreadable_saved_credential_does_not_allow_cleanup_to_revoke_a_possibly_shared_token()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token, damagedToken: true);
        scenario.Handler.PauseAt = RequestBoundary.CurrentUser;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Empty(scenario.Handler.LogoutTokens);
    }

    [Theory(Timeout = 15000)]
    [InlineData("http")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task Optional_capability_failure_still_commits_a_successful_account(string failure)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.CapabilityFailure = failure;

        var session = await scenario.SignInAsync(token);

        Assert.False(session.CapabilitiesRegistered);
        Assert.Null(session.PersistenceWarning);
        Assert.Equal(session.AccountKey, scenario.Service.Settings.LastAccountKey);
        Assert.Equal(session.AccountKey, (await scenario.Fixture.Store.LoadAsync(token)).LastAccountKey);
        Assert.Empty(scenario.Handler.LogoutTokens);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_inconsistent_authentication_identity_fails_without_persisting_and_cleans_its_new_token(
        bool mismatchCurrentUser)
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        if (mismatchCurrentUser) scenario.Handler.CurrentUserId = "unexpected-user";
        else scenario.Handler.AuthenticationServerId = "unexpected-server";

        await Assert.ThrowsAsync<EmbyProtocolException>(() => scenario.SignInAsync(token));

        await scenario.AssertUnchangedAsync(token);
        Assert.Equal(new[] { "new-token-1" }, scenario.Handler.LogoutTokens.ToArray());
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_new_token_revocation_does_not_replace_the_original_cancellation()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.PauseAt = RequestBoundary.CurrentUser;
        scenario.Handler.FailLogout = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        var operation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        await scenario.AssertUnchangedAsync(token);
        Assert.Equal(new[] { "new-token-1" }, scenario.Handler.LogoutTokens.ToArray());
    }

    [Fact(Timeout = 15000)]
    public async Task Newer_authentication_waits_until_cancelled_token_cleanup_is_finished()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.PauseAt = RequestBoundary.CurrentUser;
        scenario.Handler.DelayLogout = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var oldOperation = scenario.SignInAsync(cancelled.Token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        cancelled.Cancel();
        await scenario.Handler.LogoutStarted.Task.WaitAsync(token);
        scenario.Handler.PauseAt = RequestBoundary.None;

        var newOperation = scenario.SignInAsync(token);
        Assert.False(newOperation.IsCompleted);
        Assert.Equal(1, scenario.Handler.AuthenticationRequests);
        scenario.Handler.ReleaseLogout.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldOperation);
        var newSession = await newOperation;

        Assert.Equal(2, scenario.Handler.AuthenticationRequests);
        Assert.Equal(new[] { "new-token-1" }, scenario.Handler.LogoutTokens.ToArray());
        Assert.Equal(newSession.AccountKey, scenario.Service.Settings.LastAccountKey);
        Assert.Equal("new-token-2", scenario.Handler.CurrentUserTokens.Last());
    }

    [Fact(Timeout = 15000)]
    public async Task A_theme_change_queued_during_account_commit_updates_the_committed_settings()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        var storeGate = PrivateGate(scenario.Fixture.Store, "_gate");
        var settingsGate = PrivateGate(scenario.Service, "_settingsGate");
        var context = new QueuedContext();
        await storeGate.WaitAsync(token);
        Task<ConnectedSession> operation;
        Task themeChange;
        try
        {
            operation = context.Start(() => scenario.SignInAsync(token));
            await context.RunUntilAsync(() => settingsGate.CurrentCount == 0, token);
            themeChange = scenario.Service.SetThemeAsync("Light");
            Assert.False(themeChange.IsCompleted);
        }
        finally { storeGate.Release(); }
        await context.RunUntilAsync(() => operation.IsCompleted, token);
        var session = await operation;
        await themeChange;

        Assert.Equal("Light", scenario.Service.Settings.Theme);
        var persisted = await scenario.Fixture.Store.LoadAsync(token);
        Assert.Equal("Light", persisted.Theme);
        Assert.Equal(session.AccountKey, persisted.LastAccountKey);
        Assert.Equal(2, persisted.Accounts.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task An_account_removed_during_authentication_is_not_restored_by_the_later_commit()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        scenario.Handler.PauseAt = RequestBoundary.Capabilities;
        var operation = scenario.SignInAsync(token);
        await scenario.Handler.RequestPaused.Task.WaitAsync(token);
        await scenario.Service.RemoveSavedAccountAsync(scenario.OriginalSettings.Accounts[0].Key, token);
        scenario.Handler.ReleaseRequest.TrySetResult();

        var session = await operation;

        Assert.Equal(session.AccountKey, Assert.Single(scenario.Service.Settings.Accounts).Key);
        Assert.Equal(session.AccountKey, Assert.Single((await scenario.Fixture.Store.LoadAsync(token)).Accounts).Key);
    }

    [Fact(Timeout = 15000)]
    public async Task A_persistence_failure_returns_an_unsaved_success_without_revoking_the_new_session()
    {
        var token = TestContext.Current.CancellationToken;
        using var scenario = await Scenario.CreateAsync(token);
        using var blocker = new FileStream(scenario.Fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var session = await scenario.SignInAsync(token);

        Assert.NotNull(session.PersistenceWarning);
        Assert.True(session.CapabilitiesRegistered);
        await scenario.AssertUnchangedAsync(token);
        Assert.Empty(scenario.Handler.LogoutTokens);
    }

    private static SemaphoreSlim PrivateGate(object owner, string name) =>
        Assert.IsType<SemaphoreSlim>(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));

    public enum RequestBoundary { None, PublicInfo, Authentication, CurrentUser, Capabilities }

    private sealed class Scenario : IDisposable
    {
        public TemporaryAccountStore Fixture { get; } = new();
        public BoundaryHandler Handler { get; } = new();
        public HttpClient Http { get; }
        public ConnectionService Service { get; }
        public AppSettings OriginalSettings { get; private set; } = null!;
        public SavedAccount TargetAccount { get; private set; } = null!;
        private byte[] _originalBytes = [];
        private SavedAccount[] _originalAccounts = [];
        private string[] _originalTokens = [];
        private string _originalDeviceId = "";
        private string _originalTheme = "";
        private string? _originalLastAccountKey;

        private Scenario()
        {
            Http = new HttpClient(Handler);
            Service = new ConnectionService(Http, Fixture.Store);
        }

        public static async Task<Scenario> CreateAsync(CancellationToken cancellationToken, bool temporary = false,
            bool damagedToken = false)
        {
            var scenario = new Scenario();
            try
            {
                var old = TemporaryAccountStore.Account("server-a", "user-a",
                    await scenario.Fixture.Store.ProtectTokenAsync("old-token", cancellationToken));
                var target = TemporaryAccountStore.Account("server-b", "user-b", damagedToken ? "dpapi-user-v1:AQID"
                    : await scenario.Fixture.Store.ProtectTokenAsync("saved-token", cancellationToken));
                old.ApiRoot = target.ApiRoot = "https://synthetic.example/emby/";
                await scenario.Fixture.Store.SaveAsync(new AppSettings
                {
                    Theme = "Dark", Accounts = [old, target], LastAccountKey = old.Key
                }, cancellationToken);
                if (temporary) await scenario.Service.UseTemporarySessionAsync(cancellationToken);
                else await scenario.Service.InitializeAsync(cancellationToken);
                scenario.TargetAccount = temporary ? target : scenario.Service.Settings.Accounts[1];
                await scenario.CaptureAsync(cancellationToken);
                return scenario;
            }
            catch
            {
                scenario.Dispose();
                throw;
            }
        }

        public Task<ConnectedSession> SignInAsync(CancellationToken cancellationToken, bool remember = false) =>
            Service.SignInAsync("https://synthetic.example", "synthetic-user", "synthetic-password", remember,
                cancellationToken);

        public async Task CaptureAsync(CancellationToken cancellationToken)
        {
            OriginalSettings = Service.Settings;
            _originalAccounts = OriginalSettings.Accounts.ToArray();
            _originalTokens = OriginalSettings.Accounts.Select(account => account.ProtectedToken).ToArray();
            _originalDeviceId = OriginalSettings.DeviceId;
            _originalTheme = OriginalSettings.Theme;
            _originalLastAccountKey = OriginalSettings.LastAccountKey;
            _originalBytes = await File.ReadAllBytesAsync(Fixture.SettingsPath, cancellationToken);
        }

        public async Task AssertUnchangedAsync(CancellationToken cancellationToken)
        {
            Assert.Same(OriginalSettings, Service.Settings);
            Assert.Equal(_originalDeviceId, Service.Settings.DeviceId);
            Assert.Equal(_originalTheme, Service.Settings.Theme);
            Assert.Equal(_originalLastAccountKey, Service.Settings.LastAccountKey);
            Assert.Equal(_originalAccounts.Length, Service.Settings.Accounts.Count);
            for (var index = 0; index < _originalAccounts.Length; index++)
                Assert.Same(_originalAccounts[index], Service.Settings.Accounts[index]);
            Assert.Equal(_originalTokens, Service.Settings.Accounts.Select(account => account.ProtectedToken).ToArray());
            Assert.Equal(_originalBytes, await File.ReadAllBytesAsync(Fixture.SettingsPath, cancellationToken));
        }

        public void Dispose()
        {
            Service.Dispose();
            Http.Dispose();
            Handler.Dispose();
            Fixture.Dispose();
        }
    }

    private sealed class BoundaryHandler : HttpMessageHandler
    {
        private int _authenticationRequests;
        private int _capabilityRequests;
        public RequestBoundary PauseAt { get; set; }
        public bool IgnoreRequestCancellation { get; set; }
        public bool DelayLogout { get; set; }
        public bool FailLogout { get; set; }
        public string? AuthenticationToken { get; set; }
        public string AuthenticationServerId { get; set; } = "server-b";
        public string CurrentUserId { get; set; } = "user-b";
        public string? CapabilityFailure { get; set; }
        public int AuthenticationRequests => Volatile.Read(ref _authenticationRequests);
        public int CapabilityRequests => Volatile.Read(ref _capabilityRequests);
        public ConcurrentQueue<string> LogoutTokens { get; } = new();
        public ConcurrentQueue<string> CurrentUserTokens { get; } = new();
        public TaskCompletionSource RequestPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LogoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLogout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/emby/System/Info/Public":
                    await PauseAsync(RequestBoundary.PublicInfo, cancellationToken);
                    return Json("{\"Id\":\"server-b\",\"ServerName\":\"Synthetic Server\"}");
                case "/emby/Users/AuthenticateByName":
                    var number = Interlocked.Increment(ref _authenticationRequests);
                    await PauseAsync(RequestBoundary.Authentication, cancellationToken);
                    return Json("{\"AccessToken\":\"" + (AuthenticationToken ?? "new-token-" + number)
                        + "\",\"ServerId\":\"" + AuthenticationServerId
                        + "\",\"User\":{\"Id\":\"user-b\"},\"SessionInfo\":{\"Id\":\"session-b\"}}");
                case "/emby/Users/user-b":
                    CurrentUserTokens.Enqueue(Assert.Single(request.Headers.GetValues("X-Emby-Token")));
                    await PauseAsync(RequestBoundary.CurrentUser, cancellationToken);
                    return Json("{\"Id\":\"" + CurrentUserId + "\",\"Name\":\"Synthetic User\"}");
                case "/emby/Sessions/Capabilities/Full":
                    Interlocked.Increment(ref _capabilityRequests);
                    await PauseAsync(RequestBoundary.Capabilities, cancellationToken);
                    if (CapabilityFailure == "transport") throw new HttpRequestException("Synthetic failure.");
                    if (CapabilityFailure == "timeout") throw new OperationCanceledException("Synthetic timeout.");
                    return new HttpResponseMessage(CapabilityFailure == "http"
                        ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
                case "/emby/Sessions/Logout":
                    LogoutTokens.Enqueue(Assert.Single(request.Headers.GetValues("X-Emby-Token")));
                    LogoutStarted.TrySetResult();
                    if (DelayLogout) await ReleaseLogout.Task.WaitAsync(cancellationToken);
                    if (FailLogout) throw new HttpRequestException("Synthetic cleanup failure.");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    throw new InvalidOperationException("Unexpected synthetic account request: " + request.RequestUri.AbsolutePath);
            }
        }

        private async Task PauseAsync(RequestBoundary boundary, CancellationToken cancellationToken)
        {
            if (PauseAt != boundary) return;
            RequestPaused.TrySetResult();
            await ReleaseRequest.Task.WaitAsync(IgnoreRequestCancellation ? CancellationToken.None : cancellationToken);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _callbacks =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Writer.TryWrite((callback, state));

        public Task<T> Start<T>(Func<Task<T>> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public ValueTask<(SendOrPostCallback Callback, object? State)> TakeAsync(CancellationToken cancellationToken) =>
            _callbacks.Reader.ReadAsync(cancellationToken);

        public void Run((SendOrPostCallback Callback, object? State) continuation)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { continuation.Callback(continuation.State); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task RunUntilAsync(Func<bool> completed, CancellationToken cancellationToken)
        {
            while (!completed()) Run(await TakeAsync(cancellationToken));
        }
    }
}
