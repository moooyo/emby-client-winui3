using System.Net;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackAccessFailureTests
{
    [Theory(Timeout = 15000)]
    [InlineData(HttpStatusCode.Unauthorized, null, "AuthenticationExpired")]
    [InlineData(HttpStatusCode.Unauthorized, "ParentalControl", "AccessRestricted")]
    [InlineData(HttpStatusCode.Forbidden, null, "AccessRestricted")]
    [InlineData(HttpStatusCode.InternalServerError, null, "ServerRejected")]
    public async Task Negotiation_failures_preserve_the_access_classification_in_the_exception_and_failed_status(
        HttpStatusCode httpStatus, string? applicationError, string expectedError)
    {
        await using var context = new PlaybackTestContext();
        PlaybackStatusChangedEventArgs? failed = null;
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Failed) failed = args;
        };
        context.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? Failure(httpStatus, applicationError) : context.Respond(request));

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));

        Assert.Equal(expectedError, error.ErrorCode);
        Assert.NotNull(failed);
        Assert.Equal(expectedError, failed.ErrorCode);
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(HttpStatusCode.Unauthorized, null, "AuthenticationExpired")]
    [InlineData(HttpStatusCode.Unauthorized, "ParentalControl", "AccessRestricted")]
    [InlineData(HttpStatusCode.Forbidden, null, "AccessRestricted")]
    [InlineData(HttpStatusCode.InternalServerError, null, "ServerRejected")]
    public async Task Progress_failures_preserve_the_access_classification_in_the_owned_session_diagnostic(
        HttpStatusCode httpStatus, string? applicationError, string expectedError)
    {
        await using var context = new PlaybackTestContext();
        var diagnostics = new List<PlaybackDiagnosticEventArgs>();
        context.Coordinator.Diagnostic += (_, args) => diagnostics.Add(args);
        context.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.Uri.AbsolutePath == "/emby/Sessions/Playing/Progress"
                ? Failure(httpStatus, applicationError) : context.Respond(request));
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var playbackId = context.Coordinator.ActiveContext!.PlaybackId;

        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Progress", diagnostic.Operation);
        Assert.Equal(playbackId, diagnostic.PlaybackId);
        Assert.Equal(expectedError, diagnostic.ErrorCode);
        Assert.Equal(playbackId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.Single(context.Handler.At("Sessions/Playing/Progress"));
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
    }

    private static HttpResponseMessage Failure(HttpStatusCode status, string? applicationError)
    {
        var response = new HttpResponseMessage(status);
        if (applicationError is not null) response.Headers.Add("X-Application-Error-Code", applicationError);
        return response;
    }
}
