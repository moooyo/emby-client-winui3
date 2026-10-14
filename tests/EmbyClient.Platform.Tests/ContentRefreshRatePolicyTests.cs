using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ContentRefreshRatePolicyTests
{
    private static readonly ContentDisplayMode Original = new(3840, 2160, 32, 60, X: 1920);

    [Theory]
    [InlineData(23.976, 24)]
    [InlineData(24000d / 1001d, 24)]
    [InlineData(24, 24)]
    [InlineData(25, 50)]
    [InlineData(29.97, 30)]
    [InlineData(30000d / 1001d, 30)]
    [InlineData(59.94, 60)]
    public void Nominal_video_metadata_selects_the_lowest_compatible_integer_multiple(double frameRate, uint expected)
    {
        var modes = Modes(24, 30, 48, 50, 60, 72, 100, 120);

        var selected = ContentRefreshRatePolicy.Select(Original, modes, frameRate);

        Assert.Equal(Original with { DisplayFrequency = expected }, selected);
    }

    [Theory]
    [InlineData(23.976, 48)]
    [InlineData(24000d / 1001d, 72)]
    [InlineData(29.97, 60)]
    [InlineData(25, 100)]
    public void A_reasonable_multiple_is_available_when_the_single_rate_is_missing(double frameRate, uint frequency)
    {
        var selected = ContentRefreshRatePolicy.Select(Original, Modes(frequency), frameRate);

        Assert.Equal(frequency, selected?.DisplayFrequency);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-24)]
    [InlineData(1001)]
    public void Missing_or_invalid_metadata_cannot_select_a_mode(double frameRate)
    {
        Assert.False(ContentRefreshRatePolicy.IsValidFrameRate(frameRate));
        Assert.Null(ContentRefreshRatePolicy.Select(Original, Modes(24, 48), frameRate));
    }

    [Fact]
    public void Default_frequency_and_noninteger_cadence_are_not_matches()
    {
        Assert.Null(ContentRefreshRatePolicy.Select(Original, Modes(0, 1, 23, 59, 60), 23.976));
        Assert.Null(ContentRefreshRatePolicy.Select(Original with { DisplayFrequency = 59 }, Modes(59), 29.97));
        Assert.Null(ContentRefreshRatePolicy.Select(Original, Modes(48), 25));
    }

    [Fact]
    public void Candidate_modes_cannot_change_resolution_pixel_format_orientation_or_scan_flags()
    {
        ContentDisplayMode[] incompatible =
        [
            Original with { DisplayFrequency = 24, Width = 1920, Height = 1080 },
            Original with { DisplayFrequency = 24, BitsPerPixel = 24 },
            Original with { DisplayFrequency = 24, Orientation = 1 },
            Original with { DisplayFrequency = 24, DisplayFlags = 2 },
            Original with { DisplayFrequency = 24, FixedOutput = 1 }
        ];

        Assert.Null(ContentRefreshRatePolicy.Select(Original, incompatible, 24));
    }

    [Fact]
    public void Enumeration_positions_do_not_replace_the_current_desktop_placement()
    {
        var candidate = Original with { DisplayFrequency = 24, X = 0, Y = 0 };

        Assert.Equal(Original with { DisplayFrequency = 24 },
            ContentRefreshRatePolicy.Select(Original, [candidate], 23.976));
    }

    [Fact]
    public void The_read_back_current_mode_can_be_matched_even_when_enumeration_omits_it()
    {
        var current = Original with { DisplayFrequency = 24 };

        Assert.Equal(current, ContentRefreshRatePolicy.Select(current, [], 23.976));
        Assert.Equal(Original, ContentRefreshRatePolicy.Select(Original, Modes(59), 29.97));
    }

    [Fact]
    public void Candidates_are_ordered_and_duplicate_driver_entries_are_ignored()
    {
        var selected = ContentRefreshRatePolicy.SelectCandidates(Original, Modes(72, 48, 24, 48, 24), 23.976);

        Assert.Equal(new uint[] { 24, 48, 72 }, selected.Select(mode => mode.DisplayFrequency));
    }

    [Fact]
    public void Restoration_requires_the_full_effective_mode_to_remain_owned()
    {
        var ownership = new ContentRefreshRateOwnership("DISPLAY-A", Original,
            Original with { DisplayFrequency = 24 }, Guid.NewGuid(), 23.976);
        var applied = ownership.AppliedMode;

        Assert.Equal(ContentRefreshRateRestoreDecision.Restore,
            ContentRefreshRatePolicy.GetRestoreDecision(ownership, applied));
        Assert.Equal(ContentRefreshRateRestoreDecision.AlreadyRestored,
            ContentRefreshRatePolicy.GetRestoreDecision(ownership, Original));
        foreach (var external in new[]
        {
            applied with { DisplayFrequency = 75 }, applied with { Width = 2560 },
            applied with { Height = 1440 }, applied with { BitsPerPixel = 24 },
            applied with { X = 0 }, applied with { Y = 1080 }, applied with { Orientation = 1 },
            applied with { DisplayFlags = 2 }, applied with { FixedOutput = 1 }
        })
            Assert.Equal(ContentRefreshRateRestoreDecision.AbandonExternalChange,
                ContentRefreshRatePolicy.GetRestoreDecision(ownership, external));
    }

    private static ContentDisplayMode[] Modes(params uint[] frequencies) =>
        frequencies.Select(frequency => Original with { DisplayFrequency = frequency }).ToArray();
}
