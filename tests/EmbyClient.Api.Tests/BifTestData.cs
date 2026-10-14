using System.Buffers.Binary;

namespace EmbyClient.Api.Tests;

internal static class BifTestData
{
    // An authored one-pixel, baseline grayscale JPEG. Its DC coefficient and AC coefficients are zero.
    // Each Huffman table has one one-bit code; the entropy byte contains those two codes and padding.
    internal static byte[] GrayJpeg()
    {
        var huffmanCounts = new byte[16];
        huffmanCounts[0] = 1;
        return
        [
            0xff, 0xd8,
            0xff, 0xdb, 0, 67, 0, .. Enumerable.Repeat((byte)1, 64),
            0xff, 0xc0, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0,
            0xff, 0xc4, 0, 38,
            0, .. huffmanCounts, 0, 0x10, .. huffmanCounts, 0,
            0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0,
            0x3f, 0xff, 0xd9
        ];
    }

    internal static byte[] Create(uint multiplier, params uint[] timestamps) =>
        CreateWithImages(multiplier, timestamps.Select(timestamp => (timestamp, GrayJpeg())).ToArray());

    internal static byte[] CreateWithImages(uint multiplier, params (uint Timestamp, byte[] Image)[] images)
    {
        var indexEnd = 64 + (images.Length + 1) * 8;
        var result = new byte[indexEnd + images.Sum(image => image.Image.Length)];
        new byte[] { 0x89, 0x42, 0x49, 0x46, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(result, 0);
        PutUInt32(result, 12, (uint)images.Length);
        PutUInt32(result, 16, multiplier);
        var offset = indexEnd;
        for (var index = 0; index < images.Length; index++)
        {
            PutUInt32(result, 64 + index * 8, images[index].Timestamp);
            PutUInt32(result, 68 + index * 8, (uint)offset);
            images[index].Image.CopyTo(result, offset);
            offset += images[index].Image.Length;
        }
        PutUInt32(result, 64 + images.Length * 8, uint.MaxValue);
        PutUInt32(result, 68 + images.Length * 8, (uint)offset);
        return result;
    }

    internal static void PutUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
