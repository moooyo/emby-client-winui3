using System.Buffers.Binary;
using System.IO.Compression;

namespace EmbyClient.FixtureServer;

internal static class FixturePng
{
    public static byte[] Create(int width, int height, (int Red, int Green, int Blue) color)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var gradient = 0.35 + 0.65 * (1.0 - (double)y / height);
                var ring = Math.Abs(Math.Sqrt(Math.Pow(x - width * .65, 2) + Math.Pow(y - height * .38, 2)) - width * .39) < width * .04;
                var lines = y > height * .76 && y < height * .89 && y % 14 < 4 && x > width * .12 && x < width * .83;
                raw.WriteByte((byte)(ring || lines ? 226 : color.Red * gradient));
                raw.WriteByte((byte)(ring || lines ? 232 : color.Green * gradient));
                raw.WriteByte((byte)(ring || lines ? 238 : color.Blue * gradient));
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 2;
        header[10] = header[11] = header[12] = 0;
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, compressed.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(name);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in name) crc = UpdateCrc(crc, value);
        foreach (var value in data) crc = UpdateCrc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
