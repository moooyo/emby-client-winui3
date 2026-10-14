using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class AudioPassthroughPolicyTests
{
    [Theory]
    [InlineData("ac3", null)]
    [InlineData("eac3", "JOC")]
    [InlineData("dts", null)]
    [InlineData("dca", "DTS")]
    [InlineData(" AC-3 ", null)]
    [InlineData("E-AC-3", null)]
    [InlineData("DcA", "Core")]
    public void Dolby_digital_and_dts_core_are_allowed_in_both_enabled_modes(string codec, string? profile)
    {
        Assert.True(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.DolbyDts, codec, profile));
        Assert.True(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.All, codec, profile));
    }

    [Theory]
    [InlineData("truehd", "Atmos")]
    [InlineData("TRUE-HD", null)]
    [InlineData("true_hd", null)]
    [InlineData("dts-hd", null)]
    [InlineData("dtshd", "DTS")]
    [InlineData(" DTS_HD ", "Core")]
    [InlineData("dts", "HD")]
    [InlineData("dca", "ma")]
    [InlineData("dts", " HRA ")]
    [InlineData("dts", "DTS-HD MA")]
    [InlineData("dca", "DTS-HD HRA")]
    [InlineData("DTS", "DTS HD MA")]
    [InlineData("dts", "DTS_HD_HRA")]
    [InlineData("dts", "DTS-HD Master Audio")]
    [InlineData("dca", "DTS-HD High Resolution Audio")]
    public void Explicit_truehd_and_dts_hd_require_all_mode(string codec, string? profile)
    {
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.Off, codec, profile));
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.DolbyDts, codec, profile));
        Assert.True(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.All, codec, profile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Disabled_and_unrecognized_modes_reject_even_known_codecs(int mode)
    {
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed((AudioPassthroughMode)mode, "ac3"));
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed((AudioPassthroughMode)mode, "dts"));
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed((AudioPassthroughMode)mode, "truehd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("aac")]
    [InlineData("PCM")]
    [InlineData("pcm_s16le")]
    [InlineData("flac")]
    [InlineData("unknown")]
    [InlineData("dts-hd-unknown")]
    public void Missing_and_unlisted_codecs_are_rejected_even_with_an_hd_profile(string? codec)
    {
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.DolbyDts, codec, "DTS-HD MA"));
        Assert.False(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.All, codec, "DTS-HD MA"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DTS")]
    [InlineData("Core")]
    [InlineData("unknown")]
    [InlineData("Main")]
    [InlineData("Normal")]
    [InlineData("HDMI")]
    public void Missing_or_non_hd_dts_profiles_do_not_claim_hd_delivery(string? profile)
    {
        Assert.True(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.DolbyDts, "dts", profile));
        Assert.True(AudioPassthroughPolicy.IsCodecAllowed(AudioPassthroughMode.DolbyDts, "dca", profile));
    }

    [Fact]
    public void Unchanged_unmuted_controls_allow_a_bitstream_request()
    {
        Assert.True(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(100, false, 1d));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Volume_must_be_exactly_one_hundred_without_clamping(int volumeLevel)
    {
        Assert.False(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(volumeLevel, false, 1d));
    }

    [Fact]
    public void Muting_disables_unchanged_bitstream_controls()
    {
        Assert.False(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(100, true, 1d));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(2d)]
    public void Invalid_and_changed_playback_rates_disable_unchanged_bitstream_controls(double playbackRate)
    {
        Assert.False(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(100, false, playbackRate));
    }

    [Fact]
    public void Rates_immediately_next_to_one_are_not_treated_as_unchanged()
    {
        Assert.False(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(100, false, Math.BitDecrement(1d)));
        Assert.False(AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(100, false, Math.BitIncrement(1d)));
    }
}
