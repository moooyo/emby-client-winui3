using System.Buffers.Binary;

namespace EmbyClient.FixtureServer;

/// <summary>Reads bounded Matroska Info metadata without decoding tracks or media packets.</summary>
internal static class FixtureMatroskaInfo
{
    private const ulong EbmlId = 0x1A45DFA3;
    private const ulong SegmentId = 0x18538067;
    private const ulong InfoId = 0x1549A966;
    private const ulong TimestampScaleId = 0x2AD7B1;
    private const ulong DurationId = 0x4489;
    private const ulong VoidId = 0xEC;
    private const ulong Crc32Id = 0xBF;
    private const long MaximumMediaBytes = 64L * 1024 * 1024;
    private const int MaximumInfoBytes = 64 * 1024;
    private const int MaximumHeaderBytes = 64 * 1024;

    internal static long ReadDurationTicks(Stream media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!media.CanRead || !media.CanSeek) throw InvalidInput();
        var originalPosition = media.Position;
        try
        {
            var length = media.Length;
            if (length is <= 0 or > MaximumMediaBytes) throw InvalidInput();
            media.Position = 0;
            var reader = new BoundedReader(media, length);
            var header = reader.ReadElement(length);
            if (header.Id != EbmlId || header.Length > MaximumHeaderBytes) throw InvalidInput();
            ReadEbmlHeader(reader, header.End);

            long? durationTicks = null;
            while (reader.Position < length)
            {
                var element = reader.ReadElement(length, allowUnknownSegment: true);
                if (element.Id == SegmentId)
                {
                    if (durationTicks is not null) throw InvalidInput();
                    durationTicks = ReadSegment(reader, element.End);
                }
                else if (element.Id is VoidId or Crc32Id) reader.Skip(element);
                else throw InvalidInput();
            }
            if (durationTicks is null || media.Length != length) throw InvalidInput();
            return durationTicks.Value;
        }
        finally
        {
            media.Position = originalPosition;
        }
    }

    private static void ReadEbmlHeader(BoundedReader reader, long end)
    {
        var seen = new HashSet<ulong>();
        var hasMatroskaDocType = false;
        ulong version = 1, readVersion = 1, maxIdLength = 4, maxSizeLength = 8;
        ulong docTypeVersion = 1, docTypeReadVersion = 1;
        Span<byte> docType = stackalloc byte[8];
        while (reader.Position < end)
        {
            var element = reader.ReadElement(end);
            if (element.Id is VoidId or Crc32Id) { reader.Skip(element); continue; }
            if (!seen.Add(element.Id)) throw InvalidInput();
            switch (element.Id)
            {
                case 0x4282:
                    if (element.Length != 8) throw InvalidInput();
                    reader.ReadBytes(docType, element.End);
                    hasMatroskaDocType = docType.SequenceEqual("matroska"u8);
                    break;
                case 0x4286: version = reader.ReadUnsigned(element); break;
                case 0x42F7: readVersion = reader.ReadUnsigned(element); break;
                case 0x42F2: maxIdLength = reader.ReadUnsigned(element); break;
                case 0x42F3: maxSizeLength = reader.ReadUnsigned(element); break;
                case 0x4287: docTypeVersion = reader.ReadUnsigned(element); break;
                case 0x4285: docTypeReadVersion = reader.ReadUnsigned(element); break;
                default: throw InvalidInput();
            }
        }
        if (!hasMatroskaDocType || version != 1 || readVersion != 1 || maxIdLength is < 1 or > 4
            || maxSizeLength is < 1 or > 8 || docTypeReadVersion is < 1 or > 4
            || docTypeVersion < docTypeReadVersion)
            throw InvalidInput();
        reader.MaximumIdLength = (int)maxIdLength;
        reader.MaximumSizeLength = (int)maxSizeLength;
    }

    private static long ReadSegment(BoundedReader reader, long end)
    {
        long? durationTicks = null;
        while (reader.Position < end)
        {
            var element = reader.ReadElement(end);
            if (element.Id == InfoId)
            {
                if (durationTicks is not null || element.Length > MaximumInfoBytes) throw InvalidInput();
                durationTicks = ReadInfo(reader, element.End);
            }
            else
            {
                if (element.Id is EbmlId or SegmentId) throw InvalidInput();
                // Finite top-level elements, including Clusters, are skipped as opaque payloads.
                reader.Skip(element);
            }
        }
        return durationTicks ?? throw InvalidInput();
    }

    private static long ReadInfo(BoundedReader reader, long end)
    {
        ulong timestampScale = 1_000_000;
        var hasTimestampScale = false;
        double? duration = null;
        Span<byte> bytes = stackalloc byte[8];
        while (reader.Position < end)
        {
            var element = reader.ReadElement(end);
            if (element.Id == TimestampScaleId)
            {
                if (hasTimestampScale) throw InvalidInput();
                timestampScale = reader.ReadUnsigned(element);
                if (timestampScale == 0) throw InvalidInput();
                hasTimestampScale = true;
            }
            else if (element.Id == DurationId)
            {
                if (duration is not null || element.Length is not (4 or 8)) throw InvalidInput();
                reader.ReadBytes(bytes[..(int)element.Length], element.End);
                duration = element.Length == 4
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes));
                if (!double.IsFinite(duration.Value) || duration.Value <= 0) throw InvalidInput();
            }
            else
            {
                if (element.Id is EbmlId or SegmentId or InfoId) throw InvalidInput();
                reader.Skip(element);
            }
        }
        if (duration is null) throw InvalidInput();
        // TimestampScale is nanoseconds per Segment Tick; one TimeSpan tick is 100 ns.
        var ticks = duration.Value * timestampScale / 100d;
        var rounded = Math.Round(ticks, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(rounded) || rounded < 1 || rounded >= (double)long.MaxValue) throw InvalidInput();
        return checked((long)rounded);
    }

    private readonly record struct Element(ulong Id, long End, long Length);

    private sealed class BoundedReader(Stream media, long length)
    {
        private const int MaximumElements = 4096;
        private const int MaximumInspectedBytes = 128 * 1024;
        private int _elements;
        private int _inspectedBytes;

        public long Position => media.Position;
        public int MaximumIdLength { get; set; } = 4;
        public int MaximumSizeLength { get; set; } = 8;

        public Element ReadElement(long parentEnd, bool allowUnknownSegment = false)
        {
            if (++_elements > MaximumElements || parentEnd > length || Position >= parentEnd) throw InvalidInput();
            var (id, idWidth) = ReadVint(parentEnd, MaximumIdLength, retainMarker: true);
            var idData = id & ((1UL << (idWidth * 7)) - 1);
            if (idData == 0 || idData == (1UL << (idWidth * 7)) - 1
                || idWidth > 1 && idData < (1UL << ((idWidth - 1) * 7)) - 1)
                throw InvalidInput();
            var (size, sizeWidth) = ReadVint(parentEnd, MaximumSizeLength, retainMarker: false);
            var unknownSize = size == (1UL << (sizeWidth * 7)) - 1;
            if (unknownSize)
            {
                if (!allowUnknownSegment || id != SegmentId) throw InvalidInput();
                return new Element(id, parentEnd, parentEnd - Position);
            }
            if (size > (ulong)(parentEnd - Position)) throw InvalidInput();
            return new Element(id, Position + (long)size, (long)size);
        }

        public void Skip(Element element)
        {
            if (element.Id == Crc32Id && element.Length != 4) throw InvalidInput();
            media.Position = element.End;
        }

        public ulong ReadUnsigned(Element element)
        {
            if (element.Length is < 1 or > 8) throw InvalidInput();
            ulong value = 0;
            while (Position < element.End) value = (value << 8) | ReadByte(element.End);
            return value;
        }

        public void ReadBytes(Span<byte> bytes, long end)
        {
            if (bytes.Length != end - Position) throw InvalidInput();
            foreach (ref var value in bytes) value = ReadByte(end);
        }

        private (ulong Value, int Width) ReadVint(long end, int maximumWidth, bool retainMarker)
        {
            var first = ReadByte(end);
            var marker = 0x80;
            var width = 1;
            while ((first & marker) == 0)
            {
                if (++width > maximumWidth) throw InvalidInput();
                marker >>= 1;
            }
            ulong value = retainMarker ? first : (uint)(first & (marker - 1));
            for (var index = 1; index < width; index++) value = (value << 8) | ReadByte(end);
            return (value, width);
        }

        private byte ReadByte(long end)
        {
            if (Position >= end || ++_inspectedBytes > MaximumInspectedBytes) throw InvalidInput();
            var value = media.ReadByte();
            return value >= 0 ? (byte)value : throw InvalidInput();
        }
    }

    private static ArgumentException InvalidInput() => new("The explicit long PGS MKV has invalid or unbounded Matroska duration information.");
}
