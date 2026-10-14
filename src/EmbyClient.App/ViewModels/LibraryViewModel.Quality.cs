using EmbyClient.Api;
using System.Runtime.CompilerServices;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private const int QualityPageSize = 200;

    [Flags]
    private enum VideoQuality { Unknown = 0, FourK = 1, FullHd = 2 }

    private sealed record QualityItem(BaseItemDto Item, VideoQuality Quality);

    private static async Task<QueryResult<BaseItemDto>> QueryQualityItemsAsync(EmbyApiClient api,
        BrowseLocation location, int offset, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var quality = location.QualityFilter switch
        {
            "4K" => VideoQuality.FourK,
            "1080p" => VideoQuality.FullHd,
            _ => throw new EmbyProtocolException("The requested video quality filter is unsupported.")
        };
        var catalog = location.QualityCatalog ?? new QualityCatalog();
        var entries = await catalog.ReadAsync(api, location, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var items = new List<BaseItemDto>(PageSize);
        var total = 0;
        foreach (var entry in entries)
        {
            if ((entry.Quality & quality) == 0) continue;
            if (total >= offset && items.Count < PageSize) items.Add(entry.Item);
            total++;
        }
        return new QueryResult<BaseItemDto> { Items = items.ToArray(), TotalRecordCount = total };
    }

    private sealed class QualityCatalog
    {
        private const int ScopeLimit = 2;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _sync = new();
        private readonly Dictionary<QualityScope, QualityItem[]> _entries = [];
        private long _generation;
        private readonly int _shuffleSeed = Random.Shared.Next();

        public void Invalidate()
        {
            lock (_sync)
            {
                _generation++;
                _entries.Clear();
            }
        }

        public async Task<QualityItem[]> ReadAsync(EmbyApiClient api, BrowseLocation location,
            CancellationToken token)
        {
            var scope = location with { QualityFilter = null, QualityCatalog = null };
            // Arrays use reference equality in records; encode the item types in the cache key instead.
            var key = new QualityScope(api, api.UserId, scope with { ItemTypes = null },
                scope.ItemTypes is null ? null : string.Join("\u001f", scope.ItemTypes));
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    long generation;
                    int shuffleSeed;
                    lock (_sync)
                    {
                        generation = _generation;
                        shuffleSeed = _shuffleSeed;
                        if (_entries.TryGetValue(key, out var cached)) return cached;
                    }
                    var entries = await ReadQualityCatalogAsync(api, scope, shuffleSeed, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        // A refresh or user-data mutation must never publish an older in-flight read.
                        if (generation != _generation) continue;
                        if (_entries.Count >= ScopeLimit) _entries.Remove(_entries.Keys.First());
                        _entries.Add(key, entries);
                        return entries;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private sealed record QualityScope(EmbyApiClient Api, string? UserId, BrowseLocation Location,
            string? ItemTypes);
    }

    private static async Task<QualityItem[]> ReadQualityCatalogAsync(EmbyApiClient api,
        BrowseLocation scope, int shuffleSeed, CancellationToken token)
    {
        var random = scope.SortKey == "Random";
        var source = random ? scope with { SortKey = "SortName", SortDescending = false } : scope;
        var fields = ListFields.Concat(["MediaStreams", "MediaSources"])
            .Distinct(StringComparer.Ordinal).ToArray();
        var query = CreateItemsQuery(source, 0, QualityPageSize, fields);
        var items = new List<BaseItemDto>();
        await foreach (var item in EnumerateQualityItemsAsync(api, query, token).ConfigureAwait(false))
            items.Add(item);

        var entries = new QualityItem[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
            async (index, cancellationToken) =>
            {
                var item = items[index];
                var quality = GetVideoQuality(item, out var hasResolution);
                if (!hasResolution && IsQualityFolder(item))
                    quality = await ReadDescendantQualityAsync(api, item.Id!, cancellationToken).ConfigureAwait(false);
                entries[index] = new QualityItem(item, quality);
            }).ConfigureAwait(false);

        if (random)
        {
            // Stable randomized ranks avoid moving retained items between pages after cache invalidation.
            return entries.OrderBy(entry => HashCode.Combine(shuffleSeed, entry.Item.Id))
                .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal).ToArray();
        }
        return entries;
    }

    private static async Task<VideoQuality> ReadDescendantQualityAsync(EmbyApiClient api,
        string parentId, CancellationToken token)
    {
        var query = new ItemQuery
        {
            ParentId = parentId,
            Recursive = true,
            MediaTypes = ["Video"],
            SortBy = ["SortName"],
            SortOrder = ["Ascending"],
            Fields = ["MediaStreams", "MediaSources"],
            EnableImages = false,
            EnableUserData = false
        };
        var quality = VideoQuality.Unknown;
        await foreach (var item in EnumerateQualityItemsAsync(api, query, token).ConfigureAwait(false))
        {
            quality |= GetVideoQuality(item, out _);
            if (quality == (VideoQuality.FourK | VideoQuality.FullHd)) break;
        }
        return quality;
    }

    private static async IAsyncEnumerable<BaseItemDto> EnumerateQualityItemsAsync(EmbyApiClient api,
        ItemQuery query, [EnumeratorCancellation] CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        int? total = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = await api.GetItemsAsync(query with { StartIndex = offset, Limit = QualityPageSize }, token)
                .ConfigureAwait(false);
            if (page.TotalRecordCount is < 0)
                throw new EmbyProtocolException("The server returned a negative item count for video quality filtering.");
            if (page.TotalRecordCount is >= 0) total = page.TotalRecordCount;
            if (page.Items.Length == 0)
            {
                if (total is { } expected && offset < expected)
                    throw new EmbyProtocolException("The server ended video quality pagination before all items were returned.");
                yield break;
            }

            var added = new List<BaseItemDto>(page.Items.Length);
            foreach (var item in page.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id))
                    throw new EmbyProtocolException("The server returned a video quality item without an ID.");
                if (ids.Add(item.Id)) added.Add(item);
            }
            if (added.Count == 0)
                throw new EmbyProtocolException("The server returned a repeated video quality page without any new item IDs.");
            if (offset > int.MaxValue - page.Items.Length)
                throw new EmbyProtocolException("The server returned too many items for video quality pagination.");
            // Advance by the raw server page size, independently of deduplication and quality matches.
            offset += page.Items.Length;
            if (total is { } maximum && offset > maximum)
                throw new EmbyProtocolException("The server returned more video quality items than its reported count.");
            if (total is { } count && offset >= count && ids.Count < count)
                throw new EmbyProtocolException("The server returned an incomplete set of item IDs for video quality filtering.");
            foreach (var item in added) yield return item;
            if (total is { } completed && offset >= completed) yield break;
        }
    }

    private static bool IsQualityFolder(BaseItemDto item) => item.IsFolder == true
        || item.Type is "Series" or "Season" or "BoxSet" or "Folder" or "CollectionFolder" or "UserView" or "Playlist";

    private static VideoQuality GetVideoQuality(BaseItemDto item, out bool hasResolution)
    {
        var quality = VideoQuality.Unknown;
        hasResolution = false;
        foreach (var stream in item.MediaStreams ?? [])
            AddStreamQuality(stream, ref quality, ref hasResolution);
        foreach (var source in item.MediaSources ?? [])
            foreach (var stream in source?.MediaStreams ?? [])
                AddStreamQuality(stream, ref quality, ref hasResolution);
        return quality;
    }

    private static void AddStreamQuality(MediaStream? stream, ref VideoQuality quality, ref bool hasResolution)
    {
        if (stream is null || !string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase)) return;
        hasResolution |= stream.Width is > 0 || stream.Height is > 0;
        if (stream.Width is >= 3840 || stream.Height is >= 2160)
            quality |= VideoQuality.FourK;
        else if (stream.Width is >= 1900 and <= 2100 || stream.Height is >= 1000 and <= 1080)
            quality |= VideoQuality.FullHd;
    }
}
