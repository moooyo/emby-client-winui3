using System.Buffers.Binary;

namespace EmbyClient.Api;

/// <summary>
/// Reads the published Roku BIF version-0 archive format. This consumes server-generated still images;
/// it never opens a media stream or extracts frames from a video.
/// </summary>
public static class BifThumbnailReader
{
    public const int MaximumPackageBytes = 32 * 1024 * 1024;
    public const int MaximumImageCount = 10_000;
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumSamples = 12;
    public const int MaximumImageDimension = 4096;
    private const long MaximumImagePixels = 8 * 1024 * 1024;
    private const int HeaderBytes = 64;
    private const int IndexEntryBytes = 8;

    /// <summary>
    /// Validates the complete archive and copies at most twelve real samples. A duration boundary filters
    /// actual sample positions; it never changes a timestamp or creates an image at that boundary.
    /// </summary>
    public static BifThumbnailSet Read(ReadOnlySpan<byte> data, long? maximumPositionTicks = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumPositionTicks < 0) throw new ArgumentOutOfRangeException(nameof(maximumPositionTicks));
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < HeaderBytes + IndexEntryBytes || data.Length > MaximumPackageBytes)
            throw InvalidArchive("The BIF archive is truncated or exceeds the client size limit.");
        ReadOnlySpan<byte> magic = [0x89, 0x42, 0x49, 0x46, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!data[..8].SequenceEqual(magic) || ReadUInt32(data, 8) != 0)
            throw InvalidArchive("The BIF archive has an unsupported identifier or version.");
        for (var index = 20; index < HeaderBytes; index++)
            if (data[index] != 0) throw InvalidArchive("The BIF archive contains unsupported header extensions.");

        var declaredCount = ReadUInt32(data, 12);
        if (declaredCount > MaximumImageCount)
            throw InvalidArchive("The BIF image count exceeds the client limit.");
        var count = (int)declaredCount;
        var indexEnd = HeaderBytes + (count + 1) * IndexEntryBytes;
        if (indexEnd > data.Length) throw InvalidArchive("The BIF index is truncated.");
        var sentinelPosition = HeaderBytes + count * IndexEntryBytes;
        if (ReadUInt32(data, sentinelPosition) != uint.MaxValue
            || ReadUInt32(data, sentinelPosition + 4) != (uint)data.Length)
            throw InvalidArchive("The BIF end-of-data entry does not match the archive.");

        var multiplier = ReadUInt32(data, 16);
        if (multiplier == 0) multiplier = 1000;
        var tickMultiplier = (ulong)multiplier * (ulong)TimeSpan.TicksPerMillisecond;
        var frames = new Frame[count];
        var available = 0;
        uint previousTimestamp = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = HeaderBytes + index * IndexEntryBytes;
            var timestamp = ReadUInt32(data, entry);
            if (timestamp == uint.MaxValue || index > 0 && timestamp <= previousTimestamp)
                throw InvalidArchive("BIF timestamps must be distinct and increasing.");
            if ((ulong)timestamp > (ulong)long.MaxValue / tickMultiplier)
                throw InvalidArchive("A BIF timestamp exceeds the supported time range.");
            previousTimestamp = timestamp;
            var positionTicks = (long)((ulong)timestamp * tickMultiplier);
            var offset = ReadUInt32(data, entry + 4);
            var nextOffset = ReadUInt32(data, entry + IndexEntryBytes + 4);
            if (offset < indexEnd || nextOffset <= offset || nextOffset > data.Length
                || nextOffset - offset > MaximumImageBytes)
                throw InvalidArchive("A BIF image offset or size is invalid.");
            var image = data.Slice((int)offset, (int)(nextOffset - offset));
            var (width, height) = ValidateJpeg(image, cancellationToken);
            frames[index] = new Frame(positionTicks, (int)offset, image.Length, width, height);
            if (maximumPositionTicks is null || positionTicks <= maximumPositionTicks.Value) available++;
        }

        var sampleCount = Math.Min(MaximumSamples, available);
        var samples = new BifThumbnailSample[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[sampleCount == 1 ? 0
                : (int)Math.Round(index * (available - 1d) / (sampleCount - 1), MidpointRounding.AwayFromZero)];
            samples[index] = new BifThumbnailSample(frame.PositionTicks,
                data.Slice(frame.Offset, frame.Length).ToArray(), frame.Width, frame.Height);
        }
        return new BifThumbnailSet(count, available, multiplier, samples);
    }

    private static (int Width, int Height) ValidateJpeg(ReadOnlySpan<byte> image, CancellationToken cancellationToken)
    {
        if (image.Length < 4 || image[0] != 0xff || image[1] != 0xd8
            || image[^2] != 0xff || image[^1] != 0xd9)
            throw InvalidArchive("A BIF sample is not a complete JPEG image.");
        var position = 2;
        var width = 0;
        var height = 0;
        var components = 0;
        Span<byte> componentIds = stackalloc byte[4];
        var hasScan = false;
        var inScan = false;
        var cancellationPosition = 0;
        while (position < image.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inScan)
            {
                while (position < image.Length)
                {
                    if (position - cancellationPosition >= 16 * 1024)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        cancellationPosition = position;
                    }
                    if (image[position] != 0xff) { position++; continue; }
                    var markerStart = position;
                    while (position < image.Length && image[position] == 0xff) position++;
                    if (position >= image.Length) throw InvalidArchive("A BIF JPEG scan is truncated.");
                    var scanMarker = image[position];
                    if (scanMarker == 0 || scanMarker is >= 0xd0 and <= 0xd7) { position++; continue; }
                    position = markerStart;
                    inScan = false;
                    break;
                }
                if (inScan) throw InvalidArchive("A BIF JPEG scan has no end marker.");
            }

            if (image[position++] != 0xff) throw InvalidArchive("A BIF JPEG marker is invalid.");
            while (position < image.Length && image[position] == 0xff) position++;
            if (position >= image.Length) throw InvalidArchive("A BIF JPEG marker is truncated.");
            var marker = image[position++];
            if (marker == 0xd9)
            {
                if (!hasScan || width == 0 || position != image.Length)
                    throw InvalidArchive("A BIF JPEG image has no complete frame or has trailing data.");
                return (width, height);
            }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7)
                throw InvalidArchive("A BIF JPEG contains an unexpected standalone marker.");
            if (marker == 1) continue;
            if (!IsStartOfFrame(marker) && marker is not (0xc4 or 0xda or 0xdb or 0xdd or 0xfe)
                && marker is not (>= 0xe0 and <= 0xef))
                throw InvalidArchive("A BIF JPEG contains an unsupported marker.");
            if (position > image.Length - 2) throw InvalidArchive("A BIF JPEG segment is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(position, 2));
            if (length < 2 || length > image.Length - position)
                throw InvalidArchive("A BIF JPEG segment length is invalid.");
            if (IsStartOfFrame(marker))
            {
                if (marker is not (0xc0 or 0xc1 or 0xc2) || width != 0 || length < 8 || image[position + 2] != 8)
                    throw InvalidArchive("A BIF JPEG uses an unsupported frame format.");
                components = image[position + 7];
                height = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(position + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(position + 5, 2));
                if (components is not (1 or 3 or 4) || length != 8 + components * 3
                    || width is <= 0 or > MaximumImageDimension || height is <= 0 or > MaximumImageDimension
                    || (long)width * height > MaximumImagePixels)
                    throw InvalidArchive("A BIF JPEG frame has invalid or excessive dimensions.");
                for (var index = 0; index < components; index++)
                {
                    var component = position + 8 + index * 3;
                    var identifier = image[component];
                    var sampling = image[component + 1];
                    if (componentIds[..index].Contains(identifier) || (sampling >> 4) is < 1 or > 4
                        || (sampling & 15) is < 1 or > 4 || image[component + 2] > 3)
                        throw InvalidArchive("A BIF JPEG component descriptor is invalid.");
                    componentIds[index] = identifier;
                }
            }
            else if (marker == 0xda)
            {
                if (width == 0 || length < 6 || image[position + 2] == 0
                    || image[position + 2] > components || length != 6 + image[position + 2] * 2)
                    throw InvalidArchive("A BIF JPEG scan header is invalid.");
                var scanComponents = image[position + 2];
                for (var index = 0; index < scanComponents; index++)
                {
                    var descriptor = position + 3 + index * 2;
                    if (!componentIds[..components].Contains(image[descriptor])
                        || (image[descriptor + 1] >> 4) > 3 || (image[descriptor + 1] & 15) > 3)
                        throw InvalidArchive("A BIF JPEG scan component is invalid.");
                    for (var previous = 0; previous < index; previous++)
                        if (image[position + 3 + previous * 2] == image[descriptor])
                            throw InvalidArchive("A BIF JPEG scan has repeated components.");
                }
                hasScan = true;
                inScan = true;
            }
            position += length;
        }
        throw InvalidArchive("A BIF JPEG image is incomplete.");
    }

    private static bool IsStartOfFrame(byte marker) => marker is >= 0xc0 and <= 0xcf
        && marker is not (0xc4 or 0xc8 or 0xcc);
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
    private static EmbyProtocolException InvalidArchive(string message) => new(message);
    private readonly record struct Frame(long PositionTicks, int Offset, int Length, int Width, int Height);
}
