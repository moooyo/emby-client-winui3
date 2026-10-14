using System.Security.Cryptography;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed class FixtureBif
{
    private readonly byte[] _bytes;

    private FixtureBif(byte[] bytes, BifThumbnailSet parsed)
    {
        _bytes = bytes;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        TotalImageCount = parsed.TotalImageCount;
        AvailableImageCount = parsed.AvailableImageCount;
        SelectedImageCount = parsed.Thumbnails.Length;
        var first = parsed.Thumbnails[0];
        var consistentRatio = parsed.Thumbnails.All(sample =>
            (long)sample.PixelWidth * first.PixelHeight == (long)first.PixelWidth * sample.PixelHeight);
        Metadata = new ThumbnailSetInfo
        {
            AspectRatio = consistentRatio ? (double)first.PixelWidth / first.PixelHeight : null,
            Thumbnails = parsed.Thumbnails.Select(sample => new ThumbnailInfo
            {
                PositionTicks = sample.PositionTicks,
                ImageTag = Convert.ToHexString(SHA256.HashData(sample.JpegBytes))
            }).ToArray()
        };
    }

    public ReadOnlyMemory<byte> Bytes => _bytes;
    public string Sha256 { get; }
    public int TotalImageCount { get; }
    public int AvailableImageCount { get; }
    public int SelectedImageCount { get; }
    public ThumbnailSetInfo Metadata { get; }

    public static FixtureBif Read(string path, long durationTicks)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.GetExtension(path).Equals(".bif", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The explicit BIF input must be an existing absolute .bif file.");
        using var stream = File.OpenRead(path);
        if (stream.Length is < 72 or > BifThumbnailReader.MaximumPackageBytes)
            throw new ArgumentException("The BIF input must fit the supported 32 MiB archive bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new ArgumentException("The BIF file grew beyond its bounded input snapshot.");
        BifThumbnailSet parsed;
        try { parsed = BifThumbnailReader.Read(bytes, durationTicks); }
        catch (EmbyProtocolException exception)
        {
            throw new ArgumentException("The BIF input does not match the supported archive and JPEG contract.", exception);
        }
        if (parsed.Thumbnails.Length == 0)
            throw new ArgumentException("The BIF input must contain a real sample within the measured synthetic media duration.");
        return new FixtureBif(bytes, parsed);
    }
}

internal sealed partial class FixtureState
{
    private int _bifArchivesServed;
    private long _bifBytesServed;
    private int _thumbnailSetsServed;

    public FixtureBif? Bif(string itemId)
    {
        lock (_gate)
        {
            return Options.Bif is { } bif && _items.TryGetValue(itemId, out var item)
                && IsPlayable(item) && item.RunTimeTicks > 0 ? bif : null;
        }
    }

    public void BifServed(int length)
    {
        Interlocked.Increment(ref _bifArchivesServed);
        Interlocked.Add(ref _bifBytesServed, length);
    }

    public void ThumbnailSetServed() => Interlocked.Increment(ref _thumbnailSetsServed);
}
