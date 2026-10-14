namespace EmbyClient.App.Services;

/// <summary>Coalesces online library changes and bounds per-session notification history.</summary>
public sealed class LibraryEventAccumulator
{
    private const int MaximumPendingAdditions = 64;
    private const int MaximumNotificationHistory = 512;
    private readonly HashSet<string> _pendingAdditions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly Queue<string> _notificationOrder = new();

    public bool NeedsRefresh { get; private set; }
    public bool HasPendingAdditions => _pendingAdditions.Count > 0;
    public long Revision { get; private set; }

    public void Add(IEnumerable<string> added, bool hasChanges)
    {
        if (hasChanges) RequestRefresh();
        foreach (var id in added)
        {
            if (_pendingAdditions.Count >= MaximumPendingAdditions) break;
            if (!string.IsNullOrWhiteSpace(id) && !_notified.Contains(id)) _pendingAdditions.Add(id);
        }
    }

    public string[] TakeAdditions()
    {
        var result = _pendingAdditions.ToArray();
        _pendingAdditions.Clear();
        return result;
    }

    public bool MarkNotified(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !_notified.Add(itemId)) return false;
        _notificationOrder.Enqueue(itemId);
        while (_notificationOrder.Count > MaximumNotificationHistory)
            _notified.Remove(_notificationOrder.Dequeue());
        return true;
    }

    public void RequestRefresh() { NeedsRefresh = true; Revision++; }
    public void MarkRefreshed() => MarkRefreshed(Revision);
    public void MarkRefreshed(long revision) { if (revision == Revision) NeedsRefresh = false; }

    public void Clear()
    {
        _pendingAdditions.Clear();
        _notified.Clear();
        _notificationOrder.Clear();
        NeedsRefresh = false;
        Revision++;
    }
}
