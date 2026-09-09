namespace EmbyClient.App.Services;

/// <summary>Rejects deferred container work after recycling, reset, or a different item assignment.</summary>
internal sealed class PosterRealization<T> where T : class
{
    private T? _item;
    private long _version;

    public long Activate(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(_item, item))
        {
            _item = item;
            _version++;
        }
        return _version;
    }

    public void Retire()
    {
        _item = null;
        _version++;
    }

    public bool Owns(long version, T item) => version == _version && ReferenceEquals(_item, item);

    public bool TryGetVersion(T item, out long version)
    {
        version = _version;
        return ReferenceEquals(_item, item);
    }
}

/// <summary>One image load per realized binding, with explicit invalidation for cancellation and reuse.</summary>
internal sealed class PosterLoadState<T> where T : class
{
    private T? _item;
    private object? _owner;
    private long _ownerVersion;
    private int _width;
    private int _height;
    private long _version;
    private bool _loading;
    private bool _completed;
    private bool _assigned;

    public bool TryBegin(T item, object owner, long ownerVersion, int width, int height, bool hasSource, out long version)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(owner);
        version = _version;
        if (ReferenceEquals(_item, item) && ReferenceEquals(_owner, owner) && _ownerVersion == ownerVersion
            && _width == width && _height == height && (_loading || _completed && (!_assigned || hasSource)))
            return false;
        _item = item;
        _owner = owner;
        _ownerVersion = ownerVersion;
        _width = width;
        _height = height;
        _loading = true;
        _completed = _assigned = false;
        version = ++_version;
        return true;
    }

    public bool Owns(long version) => _loading && version == _version;

    public bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);

    public void Complete(long version, bool assigned)
    {
        if (!Owns(version)) return;
        _loading = false;
        _completed = true;
        _assigned = assigned;
    }

    public void Reset()
    {
        _version++;
        _item = null;
        _owner = null;
        _loading = _completed = _assigned = false;
    }
}
