using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LocalAssSubtitlePolicyTests
{
    private const string AssColumns = "Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";
    private const string AssStyle = "Default,Segoe UI,24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,2,20,20,20,1";
    private const string SsaStyle = "Default,Segoe UI,24,16777215,255,0,0,-1,0,1,2,0,2,20,20,20,0,1";

    [Fact]
    public void AcceptsSimpleAssStylesAndStaticTextOverrides()
    {
        var text = BuildAss(@"{\fnSegoe UI\fs28\b1\i1\u1\s1\c&H00FF00&\1c&H00FFFF&\3c&H000000&\bord2\an2}Styled{\b0\i0\u0\s0} captions\NSecond\nThird\hline, with punctuation.");

        LocalAssSubtitlePolicy.Validate(text);
    }

    [Fact]
    public void AcceptsSimpleSsaStylesAndLegacyAlignment()
    {
        var text = BuildSsa(@"{\a6\b1}Legacy title{\b0}\NSecond line.");

        LocalAssSubtitlePolicy.Validate(text);
    }

    [Theory]
    [InlineData(@"{\an2\pos(320,460)}Positioned caption.")]
    [InlineData(@"{\pos(20.5,30.25)\an7}Positioned title.")]
    [InlineData(@"{\pos(320, 460)}Positioned caption.")]
    public void AcceptsStaticPositionsWithScriptDimensions(string payload)
    {
        LocalAssSubtitlePolicy.Validate(BuildAss(payload));
    }

    [Theory]
    [InlineData(@"\t(0,500,\fs40)")]
    [InlineData(@"\move(0,0,100,100)")]
    [InlineData(@"\fad(100,100)")]
    [InlineData(@"\fade(255,0,255,0,100,900,1000)")]
    [InlineData(@"\k20")]
    [InlineData(@"\kf20")]
    [InlineData(@"\ko20")]
    [InlineData(@"\p1")]
    [InlineData(@"\clip(0,0,100,100)")]
    [InlineData(@"\iclip(0,0,100,100)")]
    [InlineData(@"\fscx150")]
    [InlineData(@"\fscy150")]
    [InlineData(@"\fsp2")]
    [InlineData(@"\frz15")]
    [InlineData(@"\fax0.2")]
    [InlineData(@"\org(100,100)")]
    [InlineData(@"\rAlternate")]
    [InlineData(@"\q2")]
    [InlineData(@"\shad2")]
    [InlineData(@"\xbord2")]
    [InlineData(@"\be1")]
    [InlineData(@"\alpha&H00&")]
    [InlineData(@"\1a&H80&")]
    [InlineData(@"\3a&H80&")]
    [InlineData(@"\unknown1")]
    public void RejectsUnsupportedEffectsInsteadOfDiscardingThem(string tag)
    {
        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(BuildAss("{" + tag + "}Caption.")));
    }

    [Fact]
    public void RejectsDrawingPayloadsInsteadOfRemovingVisibleContent()
    {
        var text = BuildAss(@"{\p1}m 0 0 l 10 10{\p0}Caption.");

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    [Theory]
    [InlineData(@"{\b1Caption.")]
    [InlineData(@"Caption}")]
    [InlineData(@"{comment}Caption.")]
    [InlineData(@"{{\b1}}Caption.")]
    [InlineData(@"{\}Caption.")]
    [InlineData(@"{\b1\}Caption.")]
    [InlineData(@"{\b2}Caption.")]
    [InlineData(@"{\fn}Caption.")]
    [InlineData(@"{\fs}Caption.")]
    [InlineData(@"{\fs+2}Caption.")]
    [InlineData(@"{\fs20junk}Caption.")]
    [InlineData(@"{\bord-1}Caption.")]
    [InlineData(@"{\an22}Caption.")]
    [InlineData(@"{\pos(10)}Caption.")]
    [InlineData(@"{\pos(10,20,30)}Caption.")]
    [InlineData(@"{\pos(10,20)junk}Caption.")]
    [InlineData(@"{\pos(NaN,20)}Caption.")]
    [InlineData(@"{\pos(10,Infinity)}Caption.")]
    [InlineData(@"{\pos(320,460)\pos(100,100)}Caption.")]
    [InlineData(@"{\c&HZZ00FF&}Caption.")]
    [InlineData(@"{\c&H8000FF00&}Caption.")]
    [InlineData(@"Caption\unknown")]
    [InlineData(@"Caption\b1")]
    [InlineData("Caption\\")]
    public void RejectsMalformedOverridesAndUnknownTextEscapes(string payload)
    {
        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(BuildAss(payload)));
    }

    [Fact]
    public void RejectsPositionsWithoutBothScriptDimensions()
    {
        var text = BuildAss(@"{\pos(320,460)}Caption.").Replace("PlayResY: 480", string.Empty, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    [Theory]
    [InlineData(11, "150")]
    [InlineData(12, "75")]
    [InlineData(13, "2")]
    [InlineData(14, "15")]
    [InlineData(15, "3")]
    [InlineData(17, "2")]
    [InlineData(3, "&H80FFFFFF")]
    [InlineData(4, "&H800000FF")]
    [InlineData(5, "&H40000000")]
    [InlineData(6, "&H80000000")]
    public void RejectsStyleFeaturesThatTheNativeProviderCannotPreserve(int column, string value)
    {
        var fields = AssStyle.Split(',');
        fields[column] = value;
        var text = BuildAss("Caption.", style: string.Join(',', fields));

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    [Fact]
    public void RejectsSsaAlphaLevels()
    {
        var fields = SsaStyle.Split(',');
        fields[16] = "128";
        var text = BuildSsa("Caption.", string.Join(',', fields));

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    [Theory]
    [InlineData("Scroll up;0;480;10")]
    [InlineData("Banner;12;0")]
    public void RejectsNonemptyDialogueEffects(string effect)
    {
        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(BuildAss("Caption.", effect: effect)));
    }

    [Theory]
    [InlineData("[Fonts]\nfontname: embedded.ttf\nencoded attachment")]
    [InlineData("[Graphics]\nfilename: image.bmp\nencoded attachment")]
    public void RejectsEmbeddedResources(string section)
    {
        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(BuildAss("Caption.") + "\n" + section));
    }

    [Fact]
    public void RejectsReorderedStyleColumns()
    {
        var text = BuildAss("Caption.").Replace("Name, Fontname, Fontsize", "Name, Fontsize, Fontname", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    [Fact]
    public void RejectsMixedStyleColorNotations()
    {
        var fields = AssStyle.Split(',');
        fields[5] = "0";

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(BuildAss("Caption.", style: string.Join(',', fields))));
    }

    [Fact]
    public void RejectsDialogueWithAnUnknownStyle()
    {
        var text = BuildAss("Caption.").Replace(",Default,,0000", ",Missing,,0000", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => LocalAssSubtitlePolicy.Validate(text));
    }

    private static string BuildAss(string payload, string? style = null, string effect = "") => $"""
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 640
        PlayResY: 480
        WrapStyle: 0
        ScaledBorderAndShadow: yes

        [V4+ Styles]
        Format: {AssColumns}
        Style: {style ?? AssStyle}

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0000,0000,0000,{effect},{payload}
        """;

    private static string BuildSsa(string payload, string? style = null) => $"""
        [Script Info]
        ScriptType: v4.00
        PlayResX: 640
        PlayResY: 480

        [V4 Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding
        Style: {style ?? SsaStyle}

        [Events]
        Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: Marked=0,0:00:01.00,0:00:04.00,Default,,0000,0000,0000,,{payload}
        """;
}
