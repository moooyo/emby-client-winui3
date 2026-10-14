using System.Buffers.Binary;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class BifThumbnailReaderTests
{
    [Fact]
    public void Published_timestamps_and_independent_JPEG_images_are_preserved()
    {
        var bytes = BifTestData.Create(250, 0, 7, 42);

        var result = BifThumbnailReader.Read(bytes);

        Assert.Equal(3, result.TotalImageCount);
        Assert.Equal(3, result.AvailableImageCount);
        Assert.Equal(250U, result.TimestampMultiplierMilliseconds);
        Assert.Equal(new long[] { 0, 17_500_000, 105_000_000 }, result.Thumbnails.Select(sample => sample.PositionTicks));
        foreach (var sample in result.Thumbnails)
        {
            Assert.Equal(1, sample.PixelWidth);
            Assert.Equal(1, sample.PixelHeight);
            Assert.Equal(BifTestData.GrayJpeg(), sample.JpegBytes);
        }
        bytes[96] = 0;
        Assert.Equal(0xff, result.Thumbnails[0].JpegBytes[0]);
    }

    [Fact]
    public void Zero_multiplier_uses_the_published_one_second_default()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(0, 2, 17));

        Assert.Equal(1000U, result.TimestampMultiplierMilliseconds);
        Assert.Equal(new long[] { 20_000_000, 170_000_000 }, result.Thumbnails.Select(sample => sample.PositionTicks));
    }

    [Fact]
    public void At_most_twelve_actual_samples_are_selected_across_the_archive()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(1000,
            Enumerable.Range(0, 24).Select(index => (uint)index).ToArray()));

        Assert.Equal(24, result.TotalImageCount);
        Assert.Equal(24, result.AvailableImageCount);
        Assert.Equal(new long[] { 0, 2, 4, 6, 8, 10, 13, 15, 17, 19, 21, 23 },
            result.Thumbnails.Select(sample => sample.PositionTicks / TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void Duration_filters_actual_samples_without_creating_a_boundary_image()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(1000, 0, 1, 2, 3, 4, 5, 6), 45_000_000);

        Assert.Equal(7, result.TotalImageCount);
        Assert.Equal(5, result.AvailableImageCount);
        Assert.Equal(new long[] { 0, 10_000_000, 20_000_000, 30_000_000, 40_000_000 },
            result.Thumbnails.Select(sample => sample.PositionTicks));
    }

    [Fact]
    public void A_real_image_at_the_duration_boundary_keeps_its_original_timestamp()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(1000, 3, 6), 30_000_000);

        Assert.Equal(30_000_000, Assert.Single(result.Thumbnails).PositionTicks);
    }

    [Fact]
    public void Samples_after_the_duration_do_not_become_an_image_at_zero()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(1000, 3, 6), 20_000_000);

        Assert.Equal(2, result.TotalImageCount);
        Assert.Equal(0, result.AvailableImageCount);
        Assert.Empty(result.Thumbnails);
    }

    [Fact]
    public void An_empty_archive_is_a_real_empty_dataset()
    {
        var result = BifThumbnailReader.Read(BifTestData.Create(1000));

        Assert.Equal(0, result.TotalImageCount);
        Assert.Empty(result.Thumbnails);
    }

    [Fact]
    public void Padding_before_the_first_image_is_allowed_by_the_published_offset_contract()
    {
        var original = BifTestData.Create(1000, 0);
        var padded = new byte[original.Length + 4];
        original.AsSpan(0, 80).CopyTo(padded);
        original.AsSpan(80).CopyTo(padded.AsSpan(84));
        BifTestData.PutUInt32(padded, 68, 84);
        BifTestData.PutUInt32(padded, 76, (uint)padded.Length);

        Assert.Equal(BifTestData.GrayJpeg(), Assert.Single(BifThumbnailReader.Read(padded).Thumbnails).JpegBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(71)]
    public void Truncated_headers_are_rejected(int length) =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.Create(1000, 0).AsSpan(0, length)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(20)]
    [InlineData(63)]
    public void Changed_magic_version_or_reserved_header_bytes_are_rejected(int offset)
    {
        var bytes = BifTestData.Create(1000, 0);
        bytes[offset] ^= 1;

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(bytes));
    }

    [Fact]
    public void An_excessive_count_is_rejected_before_allocating_its_index()
    {
        var bytes = BifTestData.Create(1000);
        BifTestData.PutUInt32(bytes, 12, uint.MaxValue);

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(bytes));
    }

    [Fact]
    public void A_truncated_index_is_rejected() =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.Create(1000, 0, 1, 2).AsSpan(0, 95)));

    [Theory]
    [InlineData(64, 0xffffffffU)]
    [InlineData(68, 0U)]
    [InlineData(68, 63U)]
    [InlineData(68, 0xffffffffU)]
    [InlineData(72, 0U)]
    [InlineData(76, 0U)]
    [InlineData(76, 80U)]
    [InlineData(76, 0xffffffffU)]
    public void Invalid_timestamp_image_offset_or_sentinel_is_rejected(int offset, uint value)
    {
        var bytes = BifTestData.Create(1000, 1);
        BifTestData.PutUInt32(bytes, offset, value);

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(bytes));
    }

    [Theory]
    [InlineData(3U, 3U)]
    [InlineData(3U, 2U)]
    public void Duplicate_or_backwards_timestamps_are_rejected(uint first, uint second) =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.Create(1000, first, second)));

    [Fact]
    public void A_timestamp_multiplier_overflow_is_rejected() =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.Create(uint.MaxValue, uint.MaxValue - 1)));

    [Fact]
    public void Zero_length_or_overlapping_images_are_rejected()
    {
        var bytes = BifTestData.Create(1000, 0, 1);
        BifTestData.PutUInt32(bytes, 76, 88);

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(bytes));
    }

    [Fact]
    public void An_image_exceeding_the_limit_is_rejected() =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.CreateWithImages(
            1000, (0, new byte[BifThumbnailReader.MaximumImageBytes + 1]))));

    [Fact]
    public void An_archive_exceeding_the_limit_is_rejected() =>
        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(new byte[BifThumbnailReader.MaximumPackageBytes + 1]));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(4097, 1)]
    [InlineData(1, 4097)]
    [InlineData(4096, 4096)]
    public void Zero_excessive_or_decompression_bomb_dimensions_are_rejected(int width, int height)
    {
        var image = BifTestData.GrayJpeg();
        // The authored fixture's SOF0 marker is at byte 71; its height and width fields follow at 76 and 78.
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(76, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(78, 2), (ushort)width);

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.CreateWithImages(1000, (0, image))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(72)]
    [InlineData(75)]
    [InlineData(82)]
    [InlineData(136)]
    public void Malformed_JPEG_marker_length_frame_or_end_is_rejected(int offset)
    {
        var image = BifTestData.GrayJpeg();
        image[offset] ^= 0x7f;

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.CreateWithImages(1000, (0, image))));
    }

    [Fact]
    public void A_corrupt_unselected_image_still_invalidates_the_archive()
    {
        var images = Enumerable.Range(0, 24).Select(index => ((uint)index, BifTestData.GrayJpeg())).ToArray();
        images[1].Item2[0] = 0;

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(BifTestData.CreateWithImages(1000, images)));
    }

    [Fact]
    public void A_corrupt_image_after_the_duration_still_invalidates_the_archive()
    {
        var invalid = BifTestData.GrayJpeg();
        invalid[^1] = 0;

        Assert.Throws<EmbyProtocolException>(() => BifThumbnailReader.Read(
            BifTestData.CreateWithImages(1000, (0, BifTestData.GrayJpeg()), (1, invalid)), 0));
    }

    [Fact]
    public void Cancellation_and_negative_duration_are_not_protocol_failures()
    {
        var bytes = BifTestData.Create(1000, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => BifThumbnailReader.Read(bytes, -1));
        Assert.ThrowsAny<OperationCanceledException>(() => BifThumbnailReader.Read(bytes,
            cancellationToken: new CancellationToken(true)));
    }
}
