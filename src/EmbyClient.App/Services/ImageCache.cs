using EmbyClient.Api;

namespace EmbyClient.App.Services;

public enum ArtworkKind { Poster, Landscape, Backdrop }

/// <summary>Caches authenticated image bytes without creating thread-affine UI objects.</summary>
public sealed class ImageCache
{
    private const long MaximumBytes = 32L * 1024 * 1024;
    private const int MaximumEntries = 128;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly Dictionary<DownloadKey, SharedDownload> _downloads = [];
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recency = new();
    private long _bytes;
    private long _generation;

    /// <summary>Returns image bytes, or null when the item has no usable image reference.</summary>
    /// <remarks>The returned array is shared by the cache and must not be modified.</remarks>
    public Task<byte[]?> GetAsync(EmbyApiClient api, string serverId, string userId,
        BaseItemDto item, int width, int height, CancellationToken cancellationToken = default) =>
        GetAsync(api, serverId, userId, item, width, height, ArtworkKind.Poster, cancellationToken);

    /// <summary>Returns artwork bytes selected for the requested presentation, or null when none is available.</summary>
    /// <remarks>The returned array is shared by the cache and must not be modified.</remarks>
    public async Task<byte[]?> GetAsync(EmbyApiClient api, string serverId, string userId,
        BaseItemDto item, int width, int height, ArtworkKind artworkKind, CancellationToken cancellationToken = default)
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
        var reference = SelectImage(item, artworkKind);
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

        var downloadKey = new DownloadKey(generation, key);
        while (true)
        {
            SharedDownload? shared;
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation == _generation && TryGet(key, out var cached)) return cached;
                _downloads.TryGetValue(downloadKey, out shared);
            }
            if (shared is not null)
            {
                var result = await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is not null) return result;
                // A failed/cancelled owner releases the key. Each surviving caller may retry
                // with its own token instead of inheriting another caller's cancellation.
                continue;
            }

            await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            var slotHeld = true;
            try
            {
                var ownsDownload = false;
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation == _generation && TryGet(key, out var cached)) return cached;
                    if (!_downloads.TryGetValue(downloadKey, out shared))
                    {
                        shared = new SharedDownload();
                        _downloads.Add(downloadKey, shared);
                        ownsDownload = true;
                    }
                }
                if (!ownsDownload)
                {
                    _requests.Release();
                    slotHeld = false;
                    var result = await shared!.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result is not null) return result;
                    continue;
                }

                byte[]? completed = null;
                try
                {
                    var bytes = await api.GetItemImageAsync(key.ItemId, key.Type, key.Index,
                        new ImageOptions { Tag = key.Tag, MaxWidth = width, MaxHeight = height },
                        cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (generation == _generation && bytes.LongLength <= MaximumBytes)
                        {
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
                    completed = bytes;
                    return bytes;
                }
                finally
                {
                    lock (_sync)
                    {
                        _downloads.Remove(downloadKey);
                        shared!.Completion.TrySetResult(completed);
                    }
                }
            }
            finally
            {
                if (slotHeld) _requests.Release();
            }
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

    private static ImageReference? SelectImage(BaseItemDto item, ArtworkKind artworkKind)
    {
        return artworkKind switch
        {
            ArtworkKind.Poster => SelectTaggedImage(item, "Primary") ?? SelectParentThumb(item),
            ArtworkKind.Landscape =>
                (item.Type == "Episode" || item.PrimaryImageAspectRatio is > 1
                    ? SelectTaggedImage(item, "Primary") : null)
                ?? SelectTaggedImage(item, "Thumb")
                ?? SelectBackdrop(item.Id, item.BackdropImageTags)
                ?? SelectParentThumb(item)
                ?? SelectBackdrop(item.ParentBackdropItemId, item.ParentBackdropImageTags)
                ?? SelectTaggedImage(item, "Primary"),
            ArtworkKind.Backdrop => SelectBackdrop(item.Id, item.BackdropImageTags)
                ?? SelectBackdrop(item.ParentBackdropItemId, item.ParentBackdropImageTags)
                ?? SelectTaggedImage(item, "Thumb")
                ?? SelectParentThumb(item),
            _ => throw new ArgumentOutOfRangeException(nameof(artworkKind))
        };
    }

    private static ImageReference? SelectTaggedImage(BaseItemDto item, string type)
    {
        if (!string.IsNullOrWhiteSpace(item.Id) && item.ImageTags is not null &&
            item.ImageTags.TryGetValue(type, out var tag) && !string.IsNullOrWhiteSpace(tag))
        {
            return new ImageReference(item.Id, type, null, tag);
        }

        return null;
    }

    private static ImageReference? SelectParentThumb(BaseItemDto item) =>
        !string.IsNullOrWhiteSpace(item.ParentThumbItemId) && !string.IsNullOrWhiteSpace(item.ParentThumbImageTag)
            ? new ImageReference(item.ParentThumbItemId, "Thumb", null, item.ParentThumbImageTag)
            : null;

    private static ImageReference? SelectBackdrop(string? itemId, string[]? tags)
    {
        if (string.IsNullOrWhiteSpace(itemId) || tags is null) return null;
        for (var index = 0; index < tags.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(tags[index])) return new ImageReference(itemId, "Backdrop", index, tags[index]);
        }
        return null;
    }

    private readonly record struct ImageReference(string ItemId, string Type, int? Index, string Tag);

    private readonly record struct CacheKey(string ServerId, string ApiRoot, string UserId, string ItemId,
        string Type, int? Index, string Tag, int Width, int Height);

    private readonly record struct DownloadKey(long Generation, CacheKey Image);

    // A key is registered only after acquiring an HTTP slot, so this table never exceeds four
    // entries. Completed state is removed even when the initiating caller fails or cancels.
    private sealed class SharedDownload
    {
        public TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record CacheEntry(CacheKey Key, byte[] Bytes);
}
