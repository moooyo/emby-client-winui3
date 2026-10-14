using System.Text;
using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LocalSubtitleTextParserTests
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const string UnicodeCaption = "\u4F60\u597D \u2014 caf\u00E9 \U0001F3AC";

    [Fact]
    public void The_actual_three_cue_ASCII_regression_file_preserves_all_times_and_captions()
    {
        const string source = "1\n00:00:00,000 --> 00:00:20,000\nLumen V2 subtitle review.\n\n"
            + "2\n00:00:20,000 --> 00:00:40,000\nLocal SRT captions remain readable.\n\n"
            + "3\n00:00:40,000 --> 00:01:00,000\nSubtitle fidelity test.\n";
        var data = Encoding.UTF8.GetBytes(source);

        var result = LocalSubtitleTextParser.Parse("review-subtitles.srt", data);

        Assert.Equal(184, data.Length);
        Assert.Equal(3, result.CueCount);
        Assert.StartsWith("WEBVTT", result.WebVtt);
        Assert.Contains("00:00:00.000 --> 00:00:20.000\nLumen V2 subtitle review.", NormalizeLines(result.WebVtt));
        Assert.Contains("00:00:20.000 --> 00:00:40.000\nLocal SRT captions remain readable.", NormalizeLines(result.WebVtt));
        Assert.Contains("00:00:40.000 --> 00:01:00.000\nSubtitle fidelity test.", NormalizeLines(result.WebVtt));
        Assert.Equal(Encoding.UTF8.GetBytes(source), data);
    }

    [Theory]
    [InlineData("\n", ",", true)]
    [InlineData("\r\n", ".", true)]
    [InlineData("\r", ",", false)]
    [InlineData("\n", ".", false)]
    public void SRT_accepts_supported_line_endings_optional_numbers_and_a_final_cue_without_newline(
        string newline, string fractionSeparator, bool numbered)
    {
        var source = (numbered ? "7" + newline : string.Empty)
            + $"01:02:03{fractionSeparator}125 --> 01:02:05{fractionSeparator}750" + newline
            + "First line." + newline + "Second line.";

        var result = LocalSubtitleTextParser.Parse("captions.SRT", Encoding.UTF8.GetBytes(source));

        Assert.Equal(1, result.CueCount);
        Assert.Contains("01:02:03.125 --> 01:02:05.750\nFirst line.\nSecond line.", NormalizeLines(result.WebVtt));
    }

    [Fact]
    public void SRT_preserves_overlapping_cues_and_their_distinct_text()
    {
        const string source = "00:00:01,000 --> 00:00:05,000\nFirst voice\n\n"
            + "00:00:03,000 --> 00:00:07,000\nSecond voice";

        var result = LocalSubtitleTextParser.Parse("dialogue.srt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(2, result.CueCount);
        Assert.Contains("00:00:01.000 --> 00:00:05.000\nFirst voice", NormalizeLines(result.WebVtt));
        Assert.Contains("00:00:03.000 --> 00:00:07.000\nSecond voice", NormalizeLines(result.WebVtt));
    }

    [Fact]
    public void SRT_strips_supported_formatting_and_escapes_plain_text_without_losing_lines()
    {
        const string source = "1\n00:00:01,000 --> 00:00:04,000\n"
            + "<i>Hello</i> <b><u>world</u></b> <font color=\"red\">again</font>\n"
            + "A & B, 2 < 3, 5 > 4.";

        var result = LocalSubtitleTextParser.Parse("styled.srt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(1, result.CueCount);
        Assert.Contains("Hello world again\nA &amp; B, 2 &lt; 3, 5 &gt; 4.", NormalizeLines(result.WebVtt));
        Assert.DoesNotContain("<i>", result.WebVtt);
        Assert.DoesNotContain("<font", result.WebVtt);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void SRT_decodes_Unicode_and_each_supported_BOM_without_mutating_the_source(string encodingName)
    {
        var source = "1\n00:00:00,000 --> 00:00:02,000\n" + UnicodeCaption;
        var data = Encode(source, encodingName);
        var original = data.ToArray();

        var result = LocalSubtitleTextParser.Parse("unicode.srt", data);

        Assert.Equal(1, result.CueCount);
        Assert.StartsWith("WEBVTT", result.WebVtt);
        Assert.Contains(UnicodeCaption, result.WebVtt);
        Assert.DoesNotContain("\uFFFD", result.WebVtt);
        Assert.Equal(original, data);
        Assert.Equal(UnicodeCaption, LocalSubtitleTextParser.Decode(Encode(UnicodeCaption, encodingName)));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void VTT_decodes_Unicode_without_reinterpreting_its_markup(string encodingName)
    {
        var source = "WEBVTT\n\ncaption-one\n00:00:00.000 --> 00:00:02.000 align:center\n"
            + "<i>" + UnicodeCaption + "</i> &amp; preserved.";
        var data = Encode(source, encodingName);

        var result = LocalSubtitleTextParser.Parse("unicode.vtt", data);

        Assert.Equal(1, result.CueCount);
        Assert.StartsWith("WEBVTT", result.WebVtt);
        Assert.Contains("00:00:00.000 --> 00:00:02.000 align:center", result.WebVtt);
        Assert.Contains("<i>" + UnicodeCaption + "</i> &amp; preserved.", result.WebVtt);
        Assert.DoesNotContain("&lt;i&gt;", result.WebVtt);
        Assert.DoesNotContain("\uFFFD", result.WebVtt);
        Assert.Equal(Encode(source, encodingName), data);
    }

    [Fact]
    public void VTT_accepts_identifiers_and_comments_while_retaining_style_regions_settings_and_caption_markup()
    {
        const string source = "WEBVTT Review captions\n\n"
            + "STYLE\n::cue { color: lime; }\n\n"
            + "REGION\nid:main\nwidth:80%\nlines:3\n\n"
            + "NOTE This timestamp is a comment\n00:10.000 --> 00:11.000\nDo not count me.\n\n"
            + "opening\n00:00:00.000 --> 00:00:02.500 line:90% position:50% align:center\n"
            + "<v Narrator><i>Hello</i> &amp; welcome.</v>\n\n"
            + "00:00:01.500 --> 00:00:03.000 region:main\n<c.highlight>Second caption</c>";

        var result = LocalSubtitleTextParser.Parse("complete.VTT", Encoding.UTF8.GetBytes(source));

        Assert.Equal(2, result.CueCount);
        Assert.StartsWith("WEBVTT", result.WebVtt);
        Assert.Contains("STYLE\n::cue { color: lime; }", NormalizeLines(result.WebVtt));
        Assert.Contains("REGION\nid:main\nwidth:80%\nlines:3", NormalizeLines(result.WebVtt));
        Assert.Contains("00:00:00.000 --> 00:00:02.500 line:90% position:50% align:center", result.WebVtt);
        Assert.Contains("<v Narrator><i>Hello</i> &amp; welcome.</v>", result.WebVtt);
        Assert.Contains("00:00:01.500 --> 00:00:03.000 region:main\n<c.highlight>Second caption</c>", NormalizeLines(result.WebVtt));
    }

    [Fact]
    public void VTT_accepts_minute_second_timestamps_and_normalizes_them_without_changing_their_values()
    {
        const string source = "WEBVTT\n\n59:58.125 --> 59:59.750\nLast seconds of the hour.";

        var result = LocalSubtitleTextParser.Parse("short-times.vtt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(1, result.CueCount);
        Assert.Contains("00:59:58.125 --> 00:59:59.750\nLast seconds of the hour.", NormalizeLines(result.WebVtt));
    }

    [Fact]
    public void SRT_scales_one_and_two_digit_fractions_to_the_correct_milliseconds()
    {
        const string source = "00:00:01,5 --> 00:00:02.25\nA short caption.";

        var result = LocalSubtitleTextParser.Parse("short-fractions.srt", Encoding.UTF8.GetBytes(source));

        Assert.Equal(1, result.CueCount);
        Assert.Contains("00:00:01.500 --> 00:00:02.250\nA short caption.", NormalizeLines(result.WebVtt));
    }

    [Theory]
    [InlineData("00:60:00,000 --> 01:00:01,000")]
    [InlineData("00:00:60,000 --> 00:01:01,000")]
    [InlineData("00:00:00,000 --> 00:60:00,000")]
    [InlineData("00:00:00,000 --> 00:00:60,000")]
    [InlineData("00:00:02,000 --> 00:00:01,000")]
    [InlineData("00:00:01,000 --> 00:00:01,000")]
    [InlineData("00:00:01,000 -->")]
    [InlineData("00:00:01,000 -> 00:00:02,000")]
    [InlineData("not a timestamp")]
    public void SRT_rejects_invalid_time_ranges(string timeline)
    {
        var data = Encoding.UTF8.GetBytes("1\n" + timeline + "\nCaption");

        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("invalid.srt", data));
    }

    [Theory]
    [InlineData("00:60:00.000 --> 01:00:01.000")]
    [InlineData("00:00:60.000 --> 00:01:01.000")]
    [InlineData("00:00:00.000 --> 00:60:00.000")]
    [InlineData("00:00:00.000 --> 00:00:60.000")]
    [InlineData("60:00.000 --> 60:01.000")]
    [InlineData("00:02.000 --> 00:01.000")]
    [InlineData("00:01.000 --> 00:01.000")]
    [InlineData("00:01.000 -->")]
    [InlineData("00:01.000 -> 00:02.000")]
    [InlineData("00:01,000 --> 00:02,000")]
    public void VTT_rejects_invalid_time_ranges(string timeline)
    {
        var data = Encoding.UTF8.GetBytes("WEBVTT\n\n" + timeline + "\nCaption");

        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("invalid.vtt", data));
    }

    [Theory]
    [InlineData("empty.srt", "")]
    [InlineData("missing.srt", "1\nCaption with no timestamp")]
    [InlineData("empty-cue.srt", "1\n00:00:00,000 --> 00:00:01,000\n   ")]
    [InlineData("empty-style.srt", "1\n00:00:00,000 --> 00:00:01,000\n<i></i>")]
    [InlineData("empty.vtt", "WEBVTT\n\n")]
    [InlineData("comment-only.vtt", "WEBVTT\n\nNOTE No cues\n00:00.000 --> 00:02.000\nIgnored")]
    [InlineData("style-only.vtt", "WEBVTT\n\nSTYLE\n::cue { color: red; }")]
    [InlineData("region-only.vtt", "WEBVTT\n\nREGION\nid:main\nwidth:80%")]
    [InlineData("empty-cue.vtt", "WEBVTT\n\n00:00.000 --> 00:01.000\n   ")]
    [InlineData("missing.vtt", "WEBVTT\n\ncaption-id\nNo timestamp")]
    [InlineData("bad-header.vtt", "WEBVTTinvalid\n\n00:00.000 --> 00:01.000\nCaption")]
    [InlineData("bad-header.vtt", "WEBVTT --> injected\n\n00:00.000 --> 00:01.000\nCaption")]
    [InlineData("missing-header.vtt", "00:00.000 --> 00:01.000\nCaption")]
    public void A_file_without_a_valid_caption_is_rejected(string fileName, string source)
    {
        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse(fileName, Encoding.UTF8.GetBytes(source)));
    }

    [Fact]
    public void A_malformed_later_cue_does_not_return_a_partial_document_or_mutate_previous_data()
    {
        const string valid = "1\n00:00:00,000 --> 00:00:01,000\nKeep this caption.\n\n";
        var originalData = Encoding.UTF8.GetBytes(valid);
        var previous = LocalSubtitleTextParser.Parse("previous.srt", originalData);
        var previousText = previous.WebVtt;
        var invalidData = Encoding.UTF8.GetBytes(valid + "2\n00:00:09,000 --> 00:00:03,000\nInvalid later caption.");
        var invalidCopy = invalidData.ToArray();

        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("replacement.srt", invalidData));

        Assert.Equal(1, previous.CueCount);
        Assert.Equal(previousText, previous.WebVtt);
        Assert.Equal(Encoding.UTF8.GetBytes(valid), originalData);
        Assert.Equal(invalidCopy, invalidData);
    }

    [Theory]
    [MemberData(nameof(InvalidEncodedText))]
    public void Malformed_encoded_bytes_are_rejected_without_replacement_characters(byte[] data)
    {
        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Decode(data));
    }

    public static TheoryData<byte[]> InvalidEncodedText => new()
    {
        new byte[] { 0xc3, 0x28 },
        new byte[] { 0xef, 0xbb, 0xbf, 0xf0, 0x9f },
        new byte[] { 0xff, 0xfe, 0x41 },
        new byte[] { 0xfe, 0xff, 0x00 },
        new byte[] { 0xff, 0xfe, 0x00, 0xd8 },
        new byte[] { 0xff, 0xfe, 0x00, 0x00, 0x41, 0x00 },
        new byte[] { 0x00, 0x00, 0xfe, 0xff, 0x00, 0x00, 0x41 },
        new byte[] { 0xff, 0xfe, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00 }
    };

    [Fact]
    public void The_raw_file_limit_is_enforced_even_when_the_content_has_a_valid_cue()
    {
        const string cue = "00:00:00,000 --> 00:00:01,000\nCaption";
        var data = Encoding.UTF8.GetBytes(cue + new string(' ', MaximumFileBytes - cue.Length + 1));

        Assert.Equal(MaximumFileBytes + 1, data.Length);
        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("oversized.srt", data));
    }

    [Fact]
    public void The_raw_file_limit_applies_to_encoded_bytes_not_just_decoded_characters()
    {
        const string cue = "WEBVTT\n\n00:00.000 --> 00:01.000\nCaption";
        var data = Encode(cue + new string(' ', MaximumFileBytes / 2 - cue.Length), "utf16-le");

        Assert.Equal(MaximumFileBytes + 2, data.Length);
        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("oversized.vtt", data));
    }

    [Fact]
    public void The_cue_limit_accepts_fifty_thousand_and_rejects_one_more()
    {
        const string cue = "00:00:00,000 --> 00:00:01,000\nCaption\n\n";
        var source = new StringBuilder(cue.Length * 50_001);
        for (var index = 0; index < 50_000; index++) source.Append(cue);

        var accepted = LocalSubtitleTextParser.Parse("at-limit.srt", Encoding.UTF8.GetBytes(source.ToString()));

        Assert.Equal(50_000, accepted.CueCount);
        source.Append(cue);
        Assert.Throws<FormatException>(() => LocalSubtitleTextParser.Parse("above-limit.srt", Encoding.UTF8.GetBytes(source.ToString())));
    }

    [Fact]
    public void Null_arguments_are_distinguished_from_invalid_subtitle_content()
    {
        Assert.Throws<ArgumentNullException>(() => LocalSubtitleTextParser.Parse(null!, []));
        Assert.Throws<ArgumentNullException>(() => LocalSubtitleTextParser.Parse("captions.srt", null!));
        Assert.Throws<ArgumentNullException>(() => LocalSubtitleTextParser.Decode(null!));
    }

    private static byte[] Encode(string text, string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
            "utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
            "utf32-le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
            "utf32-be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
