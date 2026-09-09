using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Caches authenticated image bytes without creating thread-affine UI objects.</summary>
public sealed class ImageCache
{
    private const long MaximumBytes = 32L * 1024 * 1024;
    private const int MaximumEntries = 128;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recency = new();
    private long _bytes;
    private long _generation;

    /// <summary>Returns image bytes, or null when the item has no usable image reference.</summary>
    /// <remarks>The returned array is shared by the cache and must not be modified.</remarks>
    public async Task<byte[]?> GetAsync(EmbyApiClient api, string serverId, string userId,
        BaseItemDto item, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!string.Equals(userId, api.UserId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The image cache user must match the authenticated API user.", nameof(userId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reference = SelectImage(item);
        if (reference is null)
        {
            return null;
        }

        var key = new CacheKey(serverId, api.ApiRoot.AbsoluteUri, userId, reference.Value.ItemId,
            reference.Value.Type, reference.Value.Index, reference.Value.Tag, width, height);
        long generation;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generation = _generation;
            if (TryGet(key, out var cached))
            {
                return cached;
            }
        }

        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation == _generation && TryGet(key, out var cached))
                {
                    return cached;
                }
            }

            var bytes = await api.GetItemImageAsync(key.ItemId, key.Type, key.Index,
                new ImageOptions { Tag = key.Tag, MaxWidth = width, MaxHeight = height },
                cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation == _generation && bytes.LongLength <= MaximumBytes)
                {
                    // Another request may have completed for this key while the response was in flight.
                    if (TryGet(key, out var cached))
                    {
                        return cached;
                    }

                    while (_entries.Count >= MaximumEntries || _bytes + bytes.LongLength > MaximumBytes)
                    {
                        var oldest = _recency.Last!;
                        _entries.Remove(oldest.Value.Key);
                        _bytes -= oldest.Value.Bytes.LongLength;
                        _recency.RemoveLast();
                    }

                    var node = _recency.AddFirst(new CacheEntry(key, bytes));
                    _entries.Add(key, node);
                    _bytes += bytes.LongLength;
                }
            }

            return bytes;
        }
        finally
        {
            _requests.Release();
        }
    }

    /// <summary>Removes cached images and prevents outstanding requests from repopulating them.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _generation++;
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }
    }

    private bool TryGet(CacheKey key, out byte[]? bytes)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
            bytes = node.Value.Bytes;
            return true;
        }

        bytes = null;
        return false;
    }

    private static ImageReference? SelectImage(BaseItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id) && item.ImageTags is not null &&
            item.ImageTags.TryGetValue("Primary", out var primaryTag) && !string.IsNullOrWhiteSpace(primaryTag))
        {
            return new ImageReference(item.Id, "Primary", null, primaryTag);
        }

        if (!string.IsNullOrWhiteSpace(item.ParentThumbItemId) && !string.IsNullOrWhiteSpace(item.ParentThumbImageTag))
        {
            return new ImageReference(item.ParentThumbItemId, "Thumb", null, item.ParentThumbImageTag);
        }

        return null;
    }

    private readonly record struct ImageReference(string ItemId, string Type, int? Index, string Tag);

    private readonly record struct CacheKey(string ServerId, string ApiRoot, string UserId, string ItemId,
        string Type, int? Index, string Tag, int Width, int Height);

    private sealed record CacheEntry(CacheKey Key, byte[] Bytes);
}
