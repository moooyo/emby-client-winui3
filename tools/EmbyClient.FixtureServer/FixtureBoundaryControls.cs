using System.Globalization;

namespace EmbyClient.FixtureServer;

internal sealed record FixtureItemDetailControl(string ItemId, int Attempt, bool Fail, int DelayMilliseconds);

internal sealed record FixtureBoundaryOptions(FixtureItemDetailControl[] ItemDetails,
    int FirstMediaDelayMilliseconds, int StopDelayMilliseconds, int LogoutDelayMilliseconds)
{
    public static FixtureBoundaryOptions Disabled { get; } = new([], 0, 0, 0);
    public bool Enabled => ItemDetails.Length > 0 || FirstMediaDelayMilliseconds > 0
        || StopDelayMilliseconds > 0 || LogoutDelayMilliseconds > 0;
}

internal sealed class FixtureBoundaryOptionsBuilder
{
    private readonly Dictionary<(string ItemId, int Attempt), FixtureItemDetailControl> _details = [];
    public int FirstMediaDelayMilliseconds { get; set; }
    public int StopDelayMilliseconds { get; set; }
    public int LogoutDelayMilliseconds { get; set; }

    public void AddDetailFailure(string value)
    {
        var parts = ParseDetail(value, 2);
        var key = (parts[0], ParseAttempt(parts[1]));
        var previous = GetDetail(key);
        if (previous.Fail) throw new ArgumentException("An item-detail failure may be configured only once per item and attempt.");
        _details[key] = previous with { Fail = true };
    }

    public void AddDetailDelay(string value)
    {
        var parts = ParseDetail(value, 3);
        var key = (parts[0], ParseAttempt(parts[1]));
        var previous = GetDetail(key);
        if (previous.DelayMilliseconds > 0) throw new ArgumentException("An item-detail delay may be configured only once per item and attempt.");
        var delay = ParseDelay(parts[2]);
        if (delay == 0) throw new ArgumentException("An item-detail delay must be between 1 and 30000 milliseconds.");
        _details[key] = previous with { DelayMilliseconds = delay };
    }

    public FixtureBoundaryOptions Build() => new(_details.Values.ToArray(), FirstMediaDelayMilliseconds,
        StopDelayMilliseconds, LogoutDelayMilliseconds);

    public static int ParseDelay(string value) => int.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var milliseconds) && milliseconds is >= 0 and <= 30000
            ? milliseconds : throw new ArgumentException("A boundary delay must be between 0 and 30000 milliseconds.");

    private FixtureItemDetailControl GetDetail((string ItemId, int Attempt) key)
    {
        if (_details.TryGetValue(key, out var previous)) return previous;
        if (_details.Count >= 32) throw new ArgumentException("At most 32 distinct item-detail controls may be configured.");
        return new FixtureItemDetailControl(key.ItemId, key.Attempt, false, 0);
    }

    private static string[] ParseDetail(string value, int length)
    {
        var parts = value.Split(':');
        if (parts.Length != length || string.IsNullOrWhiteSpace(parts[0]) || parts[0].Length > 64)
            throw new ArgumentException("An item-detail control must use <item-id>:<attempt> or <item-id>:<attempt>:<milliseconds>.");
        return parts;
    }

    private static int ParseAttempt(string value) => int.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var attempt) && attempt is >= 1 and <= 10000
            ? attempt : throw new ArgumentException("An item-detail attempt must be between 1 and 10000.");
}

internal sealed class FixtureBoundaryControls
{
    private readonly object _gate = new();
    private readonly FixtureBoundaryOptions _options;
    private readonly Dictionary<string, int> _detailAttempts;
    private readonly List<FixtureBoundaryEvent> _events = [];
    private long _requestSequence;
    private long _eventSequence;
    private int _activeDelays;
    private string? _firstMediaSessionId;
    private Task? _firstMediaDelay;

    public FixtureBoundaryControls(FixtureBoundaryOptions? options, IReadOnlySet<string> itemIds)
    {
        _options = options ?? FixtureBoundaryOptions.Disabled;
        if (_options.ItemDetails.Any(control => !itemIds.Contains(control.ItemId)))
            throw new ArgumentException("Every item-detail control must name an item in the configured synthetic catalog.");
        _detailAttempts = _options.ItemDetails.Select(control => control.ItemId)
            .Distinct(StringComparer.Ordinal).ToDictionary(itemId => itemId, _ => 0, StringComparer.Ordinal);
    }

    public Task ItemDetailAsync(string itemId, Func<bool, Task<int>> respond, CancellationToken cancellationToken)
    {
        int attempt;
        bool configured;
        lock (_gate)
        {
            configured = _detailAttempts.TryGetValue(itemId, out attempt);
            if (configured) _detailAttempts[itemId] = ++attempt;
        }
        if (!configured) return respond(false);
        var control = _options.ItemDetails.FirstOrDefault(control => control.ItemId == itemId && control.Attempt == attempt);
        var delay = control?.DelayMilliseconds ?? 0;
        return ExecuteAsync("ItemDetail", itemId, null, attempt, delay, null,
            () => respond(control?.Fail == true), cancellationToken);
    }

    public Task MediaAsync(string itemId, string playSessionId, Func<Task<int>> respond, CancellationToken cancellationToken)
    {
        if (_options.FirstMediaDelayMilliseconds == 0) return respond();
        Task? delay;
        lock (_gate)
        {
            if (_firstMediaSessionId is null)
            {
                _firstMediaSessionId = playSessionId;
                // All concurrent GET, HEAD, and range requests for this session share one deadline.
                // Canceling one request cannot release the gate or restart its bounded delay.
                _firstMediaDelay = Task.Delay(_options.FirstMediaDelayMilliseconds);
            }
            delay = _firstMediaSessionId == playSessionId ? _firstMediaDelay : null;
        }
        if (delay is null) return respond();
        return ExecuteAsync("MediaOpening", itemId, playSessionId, null,
            _options.FirstMediaDelayMilliseconds, delay, respond, cancellationToken);
    }

    public Task StopAsync(string itemId, string playSessionId, Func<Task<int>> respond, CancellationToken cancellationToken) =>
        _options.StopDelayMilliseconds == 0 ? respond() : ExecuteAsync("Stop", itemId, playSessionId, null,
            _options.StopDelayMilliseconds, null, respond, cancellationToken);

    public Task LogoutAsync(Func<Task<int>> respond, CancellationToken cancellationToken) =>
        _options.LogoutDelayMilliseconds == 0 ? respond() : ExecuteAsync("Logout", null, null, null,
            _options.LogoutDelayMilliseconds, null, respond, cancellationToken);

    public FixtureBoundaryStats Stats()
    {
        lock (_gate)
        {
            return new FixtureBoundaryStats(_options, _activeDelays, _requestSequence, _eventSequence,
                _firstMediaSessionId, _events.ToArray());
        }
    }

    private async Task ExecuteAsync(string operation, string? itemId, string? playSessionId, int? attempt,
        int configuredDelayMilliseconds, Task? sharedDelay, Func<Task<int>> respond, CancellationToken cancellationToken)
    {
        long requestSequence;
        lock (_gate)
        {
            requestSequence = ++_requestSequence;
            AddEvent("Entered", null);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configuredDelayMilliseconds > 0)
            {
                lock (_gate) _activeDelays++;
                try
                {
                    if (sharedDelay is null) await Task.Delay(configuredDelayMilliseconds, cancellationToken);
                    else await sharedDelay.WaitAsync(cancellationToken);
                }
                finally { lock (_gate) _activeDelays--; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) AddEvent("DelayReleased", null);
            var statusCode = await respond();
            lock (_gate) AddEvent("Completed", statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) AddEvent("Canceled", null);
            throw;
        }
        catch
        {
            lock (_gate) AddEvent("Faulted", null);
            throw;
        }

        void AddEvent(string phase, int? statusCode)
        {
            _events.Add(new FixtureBoundaryEvent(++_eventSequence, requestSequence, operation, phase,
                itemId, playSessionId, attempt, configuredDelayMilliseconds, statusCode, DateTimeOffset.UtcNow));
            if (_events.Count > 200) _events.RemoveAt(0);
        }
    }
}

internal sealed record FixtureBoundaryEvent(long Sequence, long RequestSequence, string Operation, string Phase,
    string? ItemId, string? PlaySessionId, int? Attempt, int ConfiguredDelayMilliseconds, int? StatusCode, DateTimeOffset Timestamp);

internal sealed record FixtureBoundaryStats(FixtureBoundaryOptions Configuration, int ActiveDelays,
    long RequestCount, long EventCount, string? FirstMediaSessionId, FixtureBoundaryEvent[] Events);
