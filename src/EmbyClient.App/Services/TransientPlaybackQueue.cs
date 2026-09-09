using System.Collections.ObjectModel;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

internal enum QueueAddResult { Added, Full, InvalidItem }

/// <summary>A bounded queue of upcoming items. The owning view mutates it on the UI thread.</summary>
internal sealed class TransientPlaybackQueue
{
    public const int MaximumItems = 100;
    private readonly ObservableCollection<PlaybackQueueEntry> _items = [];

    public TransientPlaybackQueue() => Items = new(_items);

    public event EventHandler? Changed;
    public ReadOnlyObservableCollection<PlaybackQueueEntry> Items { get; }
    public int Count => _items.Count;
    public PlaybackQueueEntry? Next => _items.Count == 0 ? null : _items[0];

    public QueueAddResult TryAdd(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id) || item.IsFolder == true) return QueueAddResult.InvalidItem;
        if (_items.Count >= MaximumItems) return QueueAddResult.Full;
        _items.Add(new(item));
        Changed?.Invoke(this, EventArgs.Empty);
        return QueueAddResult.Added;
    }

    public int IndexOf(Guid entryId)
    {
        for (var index = 0; index < _items.Count; index++)
            if (_items[index].EntryId == entryId) return index;
        return -1;
    }

    public bool Remove(Guid entryId)
    {
        var index = IndexOf(entryId);
        if (index < 0) return false;
        _items.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool MoveUp(Guid entryId) => Move(entryId, -1);
    public bool MoveDown(Guid entryId) => Move(entryId, 1);

    private bool Move(Guid entryId, int offset)
    {
        var index = IndexOf(entryId);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= _items.Count) return false;
        _items.Move(index, destination);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryConsume(Guid expectedEntryId, out BaseItemDto? item)
    {
        item = null;
        if (_items.Count == 0 || _items[0].EntryId != expectedEntryId) return false;
        item = _items[0].Item;
        _items.RemoveAt(0);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Prepares the observed head without removing it, then consumes it only while the request still owns playback.
    /// Call on the owning UI thread; awaiting preparation preserves that context for the identity check and mutation.
    /// </summary>
    public async Task<BaseItemDto?> TryPrepareAndConsumeAsync(Guid expectedEntryId,
        Func<BaseItemDto, CancellationToken, Task<BaseItemDto>> prepareAsync, Func<bool> ownsRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepareAsync);
        ArgumentNullException.ThrowIfNull(ownsRequest);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ownsRequest() || Next is not { } observed || observed.EntryId != expectedEntryId) return null;
        var prepared = await prepareAsync(observed.Item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ownsRequest()) return null;
        return TryConsume(expectedEntryId, out _) ? prepared : null;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class PlaybackQueueEntry
{
    internal PlaybackQueueEntry(BaseItemDto item) => Item = item;
    public Guid EntryId { get; } = Guid.NewGuid();
    public BaseItemDto Item { get; }
    public string Title => string.IsNullOrWhiteSpace(Item.Name) ? "Untitled media" : Item.Name;
    public string Detail => string.IsNullOrWhiteSpace(Item.SeriesName) ? Item.Type ?? "Video" : Item.SeriesName;
    public string AutomationName => $"{Title}, {Detail}";
}
