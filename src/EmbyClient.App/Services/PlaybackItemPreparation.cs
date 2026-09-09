namespace EmbyClient.App.Services;

/// <summary>
/// Restores a terminal presentation when item preparation ends before playback takes ownership.
/// The caller's ownership predicate protects both retirement and its asynchronous UI continuation.
/// </summary>
internal static class PlaybackItemPreparation
{
    public static async Task RunAsync<T>(Func<CancellationToken, Task<T?>> prepareAsync,
        Func<T, CancellationToken, Task> startAsync, Func<bool> ownsRequest,
        Func<Task> retireAsync, Action<Outcome> restoreTerminal, CancellationToken cancellationToken)
        where T : class
    {
        var handedOff = false;
        var preparationFailed = false;
        try
        {
            var item = await prepareAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || !ownsRequest()) return;
            handedOff = true;
            await startAsync(item, cancellationToken);
        }
        catch
        {
            preparationFailed = true;
            throw;
        }
        finally
        {
            if (!handedOff && ownsRequest())
            {
                var cleanupFailed = false;
                try { await retireAsync(); }
                catch (Exception) { cleanupFailed = true; }
                // Keep the preparation exception intact, and never render over a newer request.
                if (ownsRequest()) restoreTerminal(new(preparationFailed, cleanupFailed));
            }
        }
    }

    internal readonly record struct Outcome(bool PreparationFailed, bool CleanupFailed);
}
