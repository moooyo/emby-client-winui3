using System.Buffers;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>A seekable, bounded-cache stream over a server's byte-range representation.</summary>
/// <remarks>The caller owns the transport. Disposing this stream cancels only this stream's reads.</remarks>
internal sealed partial class HttpRangeStream : Stream
{
    private const int BlockSize = 256 * 1024;
    private const int MaximumCachedBlocks = 16;
    private readonly ScopedMediaTransport _transport;
    private readonly Uri _uri;
    private readonly long _length;
    private readonly string? _entityTag;
    private readonly DateTimeOffset? _lastModified;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Dictionary<long, LinkedListNode<CachedBlock>> _blocks = [];
    private readonly LinkedList<CachedBlock> _recency = new();
    private long _position;
    private int _disposed;

    private HttpRangeStream(ScopedMediaTransport transport, Uri uri, MediaResource first)
        : this(transport, uri, first.TotalLength!.Value, first.ContentType, first.EntityTag, first.LastModified)
    {
        AddBlock(0, first.Data);
    }

    private HttpRangeStream(ScopedMediaTransport transport, Uri uri, long length, string contentType,
        string? entityTag, DateTimeOffset? lastModified)
    {
        _transport = transport;
        _uri = uri;
        _length = length;
        _entityTag = entityTag;
        _lastModified = lastModified;
        _shutdownToken = _shutdown.Token;
        ContentType = contentType;
    }

    public string ContentType { get; }
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return Interlocked.Read(ref _position);
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    internal static async Task<HttpRangeStream> OpenAsync(ScopedMediaTransport transport, Uri uri,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(uri);
        var first = await transport.DownloadRangeAsync(uri, 0, BlockSize, null, null, null, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (first.TotalLength is not > 0 || first.Data.LongLength != Math.Min(BlockSize, first.TotalLength.Value))
            throw new PlaybackException("UnsupportedFormat");
        // Retain the original URI so expiring CDN redirects can be renewed on subsequent requests.
        return new HttpRangeStream(transport, uri, first);
    }

    /// <summary>Creates an independent zero-based cursor without issuing a network request.</summary>
    internal HttpRangeStream CloneCursor()
    {
        ThrowIfDisposed();
        return new HttpRangeStream(_transport, _uri, _length, ContentType, _entityTag, _lastModified);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0) return 0;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, BlockSize));
        try
        {
            var read = Read(rented, 0, Math.Min(buffer.Length, BlockSize));
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            operation.Token.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || _position >= _length) return 0;
            var blockOffset = _position / BlockSize * BlockSize;
            var bytes = await GetBlockAsync(blockOffset, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            var offset = (int)(_position - blockOffset);
            var count = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            Interlocked.Add(ref _position, count);
            return count;
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0) ClearCache();
            _gate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        _gate.Wait(_shutdownToken);
        try
        {
            ThrowIfDisposed();
            var basis = origin switch
            {
                SeekOrigin.Begin => 0L,
                SeekOrigin.Current => _position,
                SeekOrigin.End => _length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            long position;
            try
            {
                position = checked(basis + offset);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            Interlocked.Exchange(ref _position, position);
            return position;
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0) ClearCache();
            _gate.Release();
        }
    }

    private async Task<byte[]> GetBlockAsync(long offset, CancellationToken ct)
    {
        if (_blocks.TryGetValue(offset, out var cached))
        {
            _recency.Remove(cached);
            _recency.AddFirst(cached);
            return cached.Value.Data;
        }

        var length = (int)Math.Min(BlockSize, _length - offset);
        var resource = await _transport.DownloadRangeAsync(_uri, offset, length, _length,
            _entityTag, _lastModified, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (resource.Data.Length != length) throw new PlaybackException("UnsupportedFormat");
        AddBlock(offset, resource.Data);
        return resource.Data;
    }

    private void AddBlock(long offset, byte[] bytes)
    {
        if (_blocks.Count == MaximumCachedBlocks)
        {
            var oldest = _recency.Last!;
            _blocks.Remove(oldest.Value.Offset);
            _recency.RemoveLast();
            Array.Clear(oldest.Value.Data);
        }
        _blocks.Add(offset, _recency.AddFirst(new CachedBlock(offset, bytes)));
    }

    private void ClearCache()
    {
        foreach (var block in _recency) Array.Clear(block.Data);
        _blocks.Clear();
        _recency.Clear();
    }

    public override void Flush() => ThrowIfDisposed();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public override void SetLength(long value) => throw new NotSupportedException("The media stream is read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The media stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shutdown.Cancel();
            // An active read clears its cache before releasing the gate. Never block the UI on HTTP cleanup.
            if (_gate.Wait(0))
            {
                try { ClearCache(); }
                finally { _gate.Release(); }
            }
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ClearCache(); }
        finally { _gate.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed partial record CachedBlock(long Offset, byte[] Data);
}
