using System.Buffers.Binary;
using EmbyClient.FixtureServer;

internal static class MatroskaInfoContractChecks
{
    public static void Run(Action<bool, string> check)
    {
        var measuredInfo = Element([0x15, 0x49, 0xa9, 0x66], Duration(100_011));
        var measured = Document(measuredInfo);
        using (var stream = new MemoryStream(measured))
        {
            stream.Position = 3;
            check(FixtureMatroskaInfo.ReadDurationTicks(stream) == 1_000_110_000 && stream.Position == 3,
                "Bounded Matroska parsing reads the actual hundred-second duration and restores the caller position.");
        }
        check(Read(Document(measuredInfo, unknownSegment: true)) == 1_000_110_000,
            "A finite Info inside an unknown-sized Segment remains supported.");
        var scaledInfo = Element([0x15, 0x49, 0xa9, 0x66], Join(
            Element([0x2a, 0xd7, 0xb1], [0x03, 0xd0, 0x90]), Duration(400_044)));
        check(Read(Document(scaledInfo)) == 1_000_110_000,
            "Matroska TimestampScale units are applied instead of assuming milliseconds.");
        var singleBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(singleBytes, BitConverter.SingleToInt32Bits(100_000f));
        check(Read(Document(Element([0x15, 0x49, 0xa9, 0x66], Element([0x44, 0x89], singleBytes)))) == 1_000_000_000,
            "Four-byte big-endian Matroska duration is supported.");
        check(Read(Document(Join(Element([0x1f, 0x43, 0xb6, 0x75], Duration(double.NaN)), measuredInfo))) == 1_000_110_000,
            "Finite Cluster payloads are skipped without interpreting media packets as Info.");

        Reject([], "an empty container");
        Reject(Document(measuredInfo, docType: "webm"u8.ToArray()), "a different EBML document type");
        Reject(Document(Join(measuredInfo, measuredInfo)), "duplicate Segment Info");
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], [])), "missing duration");
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], Join(Duration(100_000), Duration(100_000)))), "duplicate duration");
        var scale = Element([0x2a, 0xd7, 0xb1], [0x0f, 0x42, 0x40]);
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], Join(scale, scale, Duration(100_000)))), "duplicate TimestampScale");
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], Join(Element([0x2a, 0xd7, 0xb1], [0]), Duration(100_000)))), "zero TimestampScale");
        foreach (var duration in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.MaxValue })
            Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], Duration(duration))), "a nonpositive, nonfinite, or overflowing duration");
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], Element([0x44, 0x89], new byte[3]))), "an invalid floating-point width");
        Reject(Document([0x15, 0x49, 0xa9, 0x66, 0xff]), "unknown-sized Info");
        Reject(Document([0x15, 0x49, 0xa9, 0x66, 0x8a, 0x44]), "a truncated Info payload");
        Reject(Document(Element([0x15, 0x49, 0xa9, 0x66], new byte[64 * 1024 + 1])), "Info above the 64 KiB bound");
        Reject(Document(Join(Enumerable.Repeat(Element([0xec], []), 4097).ToArray())), "a header count above the bounded element budget");
        Reject(Document(Join(Element([0xbf], new byte[3]), measuredInfo)), "an invalid CRC element width");
        Reject(Document([0, 0x80]), "an invalid variable-length element ID");
        Reject(Document(measuredInfo)[..^1], "a Segment truncated at the end of its duration");

        void Reject(byte[] bytes, string description)
        {
            using var stream = new MemoryStream(bytes);
            var initial = Math.Min(3, stream.Length);
            stream.Position = initial;
            var rejected = false;
            try { FixtureMatroskaInfo.ReadDurationTicks(stream); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && stream.Position == initial, "Matroska duration parsing rejects " + description + " and restores position.");
        }
    }

    private static long Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return FixtureMatroskaInfo.ReadDurationTicks(stream);
    }

    private static byte[] Duration(double value)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, BitConverter.DoubleToInt64Bits(value));
        return Element([0x44, 0x89], payload);
    }

    private static byte[] Document(byte[] segment, bool unknownSegment = false, byte[]? docType = null)
    {
        var header = Element([0x1a, 0x45, 0xdf, 0xa3], Element([0x42, 0x82], docType ?? "matroska"u8.ToArray()));
        return unknownSegment
            ? Join(header, [0x18, 0x53, 0x80, 0x67, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff], segment)
            : Join(header, Element([0x18, 0x53, 0x80, 0x67], segment));
    }

    private static byte[] Element(byte[] id, byte[] payload)
    {
        var width = 1;
        while ((ulong)payload.Length >= (1UL << (width * 7)) - 1) width++;
        var size = new byte[width];
        var value = payload.Length;
        for (var index = width - 1; index >= 0; index--) { size[index] = (byte)value; value >>= 8; }
        size[0] |= (byte)(1 << (8 - width));
        return Join(id, size, payload);
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
}
