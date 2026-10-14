using System.Globalization;
using System.Text;

namespace EmbyClient.App.Playback;

/// <summary>Restricts local ASS and SSA to the static features preserved by the native timed text provider.</summary>
internal static class LocalAssSubtitlePolicy
{
    private static readonly string[] AssStyleColumns =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "OutlineColour", "BackColour",
        "Bold", "Italic", "Underline", "StrikeOut", "ScaleX", "ScaleY", "Spacing", "Angle", "BorderStyle",
        "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV", "Encoding"
    ];

    private static readonly string[] SsaStyleColumns =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "TertiaryColour", "BackColour",
        "Bold", "Italic", "BorderStyle", "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV",
        "AlphaLevel", "Encoding"
    ];

    private static readonly string[] EventColumns =
    [
        "Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"
    ];

    internal static void Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("The ASS or SSA document contains no text.");
        var section = Section.None;
        bool? isAss = null;
        var scriptSeen = false;
        var stylesSeen = false;
        var eventsSeen = false;
        var styleFormatSeen = false;
        var eventFormatSeen = false;
        var dialogueCount = 0;
        int? playResX = null;
        int? playResY = null;
        var scriptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                switch (line)
                {
                    case "[Script Info]" when !scriptSeen && !stylesSeen && !eventsSeen:
                        scriptSeen = true;
                        section = Section.ScriptInfo;
                        break;
                    case "[V4+ Styles]" when isAss == true && !stylesSeen && !eventsSeen:
                    case "[V4 Styles]" when isAss == false && !stylesSeen && !eventsSeen:
                        stylesSeen = true;
                        section = Section.Styles;
                        break;
                    case "[Events]" when stylesSeen && styleFormatSeen && styles.Count > 0 && !eventsSeen:
                        eventsSeen = true;
                        section = Section.Events;
                        break;
                    case "[Aegisub Project Garbage]" when scriptSeen && !stylesSeen && !eventsSeen:
                        section = Section.Metadata;
                        break;
                    case "[Fonts]":
                    case "[Graphics]":
                        throw new FormatException("Embedded ASS fonts and graphics are not supported.");
                    default:
                        throw new FormatException("The ASS or SSA section is unsupported, duplicated or out of order.");
                }
                continue;
            }

            switch (section)
            {
                case Section.ScriptInfo:
                    var separator = line.IndexOf(':');
                    if (separator <= 0) throw new FormatException("The ASS or SSA script header is invalid.");
                    var key = line[..separator];
                    var value = line[(separator + 1)..].Trim();
                    if (key != key.Trim() || !scriptKeys.Add(key))
                        throw new FormatException("The ASS or SSA script header is duplicated or invalid.");
                    if (key is "Style" or "Format" or "Dialogue" or "Comment")
                        throw new FormatException("ASS and SSA records must remain inside their canonical sections.");
                    switch (key)
                    {
                        case "ScriptType":
                            RequireNativeHeaderPrefix(line, key);
                            isAss = value switch
                            {
                                "v4.00+" => true,
                                "v4.00" => false,
                                _ => throw new FormatException("Only ASS v4.00+ and SSA v4.00 scripts are supported.")
                            };
                            break;
                        case "PlayResX":
                            RequireNativeHeaderPrefix(line, key);
                            playResX = ReadPositiveInteger(value);
                            break;
                        case "PlayResY":
                            RequireNativeHeaderPrefix(line, key);
                            playResY = ReadPositiveInteger(value);
                            break;
                        case "Timer" when ReadNumber(value) != 100:
                        case "WrapStyle" when ReadInteger(value) != 0:
                        case "Collisions" when value != "Normal":
                        case "ScaledBorderAndShadow" when value != "yes":
                            throw new FormatException("The ASS or SSA script requests an unsupported layout or timing feature.");
                    }
                    break;

                case Section.Styles:
                    if (line.StartsWith("Format: ", StringComparison.Ordinal) && !styleFormatSeen && styles.Count == 0)
                    {
                        ValidateColumns(line[8..], isAss == true ? AssStyleColumns : SsaStyleColumns);
                        styleFormatSeen = true;
                    }
                    else if (line.StartsWith("Style: ", StringComparison.Ordinal) && styleFormatSeen)
                    {
                        var fields = line[7..].Split(',');
                        ValidateStyle(fields, isAss == true);
                        if (!styles.Add(fields[0])) throw new FormatException("The ASS or SSA style name is duplicated.");
                    }
                    else throw new FormatException("The ASS or SSA style header or record is invalid.");
                    break;

                case Section.Events:
                    if (line.StartsWith("Format: ", StringComparison.Ordinal) && !eventFormatSeen && dialogueCount == 0)
                    {
                        var expected = (string[])EventColumns.Clone();
                        if (isAss == false) expected[0] = "Marked";
                        ValidateColumns(line[8..], expected);
                        eventFormatSeen = true;
                    }
                    else if (line.StartsWith("Dialogue: ", StringComparison.Ordinal) && eventFormatSeen)
                    {
                        ValidateDialogue(line[10..], isAss == true, styles, playResX, playResY);
                        dialogueCount++;
                    }
                    else if (!line.StartsWith("Comment: ", StringComparison.Ordinal) || !eventFormatSeen)
                        throw new FormatException("The ASS or SSA event header or record is invalid.");
                    break;

                case Section.Metadata:
                    // Aegisub project metadata does not contribute to native subtitle presentation.
                    if (line.IndexOf(':') <= 0 || line.StartsWith("Style:", StringComparison.Ordinal)
                        || line.StartsWith("Format:", StringComparison.Ordinal) || line.StartsWith("Dialogue:", StringComparison.Ordinal)
                        || line.StartsWith("Comment:", StringComparison.Ordinal))
                        throw new FormatException("The Aegisub project metadata is invalid.");
                    break;
                default:
                    throw new FormatException("The ASS or SSA document is missing its canonical sections.");
            }
        }

        if (isAss is null || !styleFormatSeen || styles.Count == 0 || !eventFormatSeen || dialogueCount == 0)
            throw new FormatException("The ASS or SSA document has no supported styles or dialogue.");
    }

    private static void RequireNativeHeaderPrefix(string line, string key)
    {
        if (!line.StartsWith(key + ": ", StringComparison.Ordinal))
            throw new FormatException("The ASS or SSA script header does not use the native parser's canonical syntax.");
    }

    private static void ValidateColumns(string value, string[] expected)
    {
        var columns = value.Split(',').Select(column => column.Trim());
        if (!columns.SequenceEqual(expected, StringComparer.Ordinal))
            throw new FormatException("The ASS or SSA format columns do not match the native parser's canonical order.");
    }

    private static void ValidateStyle(string[] fields, bool isAss)
    {
        if (fields.Length != (isAss ? AssStyleColumns.Length : SsaStyleColumns.Length))
            throw new FormatException("The ASS or SSA style record has an invalid column count.");
        if (string.IsNullOrWhiteSpace(fields[0]) || fields[0] != fields[0].Trim()
            || Encoding.UTF8.GetByteCount(fields[0]) > 255)
            throw new FormatException("The ASS or SSA style name is invalid.");
        ValidateFontFamily(fields[1]);
        if (Encoding.UTF8.GetByteCount(fields[1]) > 255 || ReadNumber(fields[2]) <= 0)
            throw new FormatException("The ASS or SSA font definition is invalid.");

        var hexColors = fields[3].StartsWith("&H", StringComparison.Ordinal);
        foreach (var color in fields[3..7])
        {
            if (color.StartsWith("&H", StringComparison.Ordinal) != hexColors)
                throw new FormatException("The native ASS or SSA parser requires a consistent color notation.");
            ValidateColor(color, allowDecimal: true, allowTerminator: false);
        }
        ValidateStyleFlag(fields[7]);
        ValidateStyleFlag(fields[8]);
        if (isAss)
        {
            ValidateStyleFlag(fields[9]);
            ValidateStyleFlag(fields[10]);
            if (ReadNumber(fields[11]) != 100 || ReadNumber(fields[12]) != 100
                || ReadNumber(fields[13]) != 0 || ReadNumber(fields[14]) != 0
                || ReadNativeInteger(fields[15]) != 1 || ReadNumber(fields[17]) != 0)
                throw new FormatException("ASS scaling, spacing, rotation, opaque boxes and shadows are not supported.");
            RequireNonnegativeNumber(fields[16]);
            ValidateAlignment(fields[18], numpad: true, nativeField: true);
            foreach (var margin in fields[19..22]) ReadInteger(margin);
            ValidateEncoding(fields[22]);
        }
        else
        {
            if (ReadNativeInteger(fields[9]) != 1 || ReadNativeInteger(fields[11]) != 0 || ReadNumber(fields[16]) != 0)
                throw new FormatException("SSA opaque boxes, shadows and alpha levels are not supported.");
            RequireNonnegativeNumber(fields[10]);
            ValidateAlignment(fields[12], numpad: false, nativeField: true);
            foreach (var margin in fields[13..16]) ReadInteger(margin);
            ValidateEncoding(fields[17]);
        }
    }

    private static void ValidateStyleFlag(string value)
    {
        if (value is not ("0" or "-1" or "1")) throw new FormatException("The ASS or SSA style flag is invalid.");
    }

    private static void ValidateEncoding(string value)
    {
        if (ReadNativeInteger(value) > 255) throw new FormatException("The ASS or SSA font encoding is invalid.");
    }

    private static void ValidateFontFamily(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\\', '/', ':', '#', '@', '{', '}']) >= 0)
            throw new FormatException("Only a plain installed font family is supported for local ASS or SSA subtitles.");
    }

    private static void ValidateDialogue(string value, bool isAss, HashSet<string> styles, int? playResX, int? playResY)
    {
        var fields = value.Split(',', 10, StringSplitOptions.None);
        if (fields.Length != 10) throw new FormatException("The ASS or SSA dialogue record has an invalid column count.");
        if (isAss) ReadInteger(fields[0]);
        else if (fields[0] != "Marked=0") throw new FormatException("Only unmarked SSA dialogue is supported.");
        if (ReadTimestamp(fields[2]) <= ReadTimestamp(fields[1]))
            throw new FormatException("An ASS or SSA dialogue must end after it starts.");
        if (!styles.Contains(fields[3])) throw new FormatException("The ASS or SSA dialogue refers to an unknown style.");
        foreach (var margin in fields[5..8]) ReadInteger(margin);
        if (fields[8].Length > 0) throw new FormatException("ASS and SSA dialogue effects are not supported.");
        ValidatePayload(fields[9], playResX, playResY);
    }

    private static long ReadTimestamp(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3 || parts[0].Length is 0 or > 6 || parts[1].Length != 2
            || parts[2].Length != 5 || parts[2][2] != '.')
            throw new FormatException("The ASS or SSA timestamp is invalid.");
        var hours = ReadInteger(parts[0]);
        var minutes = ReadInteger(parts[1]);
        var seconds = ReadInteger(parts[2][..2]);
        var fraction = ReadInteger(parts[2][3..]);
        if (minutes > 59 || seconds > 59) throw new FormatException("ASS and SSA minutes and seconds must be below 60.");
        return hours * 360_000L + minutes * 6_000L + seconds * 100L + fraction;
    }

    private static void ValidatePayload(string value, int? playResX, int? playResY)
    {
        var visibleText = false;
        var positionSeen = false;
        var alignmentSeen = false;
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (character == '{')
            {
                var end = value.IndexOf('}', index + 1);
                if (end < 0 || end <= index + 2 || value[index + 1] != '\\')
                    throw new FormatException("The ASS or SSA override block is incomplete or invalid.");
                var block = value[(index + 2)..end];
                if (block.Contains('{')) throw new FormatException("Nested ASS and SSA override blocks are not supported.");
                foreach (var tag in block.Split('\\'))
                    ValidateTag(tag, playResX, playResY, ref positionSeen, ref alignmentSeen);
                index = end + 1;
            }
            else if (character == '}') throw new FormatException("The ASS or SSA text contains an unmatched closing brace.");
            else if (character == '\\')
            {
                if (index + 1 >= value.Length || value[index + 1] is not ('N' or 'n' or 'h'))
                    throw new FormatException("The ASS or SSA text contains an unsupported escape command.");
                index += 2;
            }
            else
            {
                if (!char.IsWhiteSpace(character)) visibleText = true;
                index++;
            }
        }
        if (!visibleText) throw new FormatException("The ASS or SSA dialogue contains no visible text.");
    }

    private static void ValidateTag(string tag, int? playResX, int? playResY, ref bool positionSeen, ref bool alignmentSeen)
    {
        if (tag is "b0" or "b1" or "i0" or "i1" or "u0" or "u1" or "s0" or "s1") return;
        if (tag.StartsWith("fn", StringComparison.Ordinal))
        {
            ValidateFontFamily(tag[2..]);
            return;
        }
        if (tag.StartsWith("fs", StringComparison.Ordinal))
        {
            if (ReadNumber(tag[2..]) <= 0) throw new FormatException("The ASS or SSA font size must be positive and absolute.");
            return;
        }
        if (tag.StartsWith("bord", StringComparison.Ordinal))
        {
            RequireNonnegativeNumber(tag[4..]);
            return;
        }
        if (tag.StartsWith("1c", StringComparison.Ordinal) || tag.StartsWith("3c", StringComparison.Ordinal)
            || tag.StartsWith('c'))
        {
            ValidateColor(tag[(tag[0] == 'c' ? 1 : 2)..], allowDecimal: false, allowTerminator: true);
            return;
        }
        if (tag.StartsWith("an", StringComparison.Ordinal) || tag.StartsWith('a'))
        {
            if (alignmentSeen) throw new FormatException("An ASS or SSA dialogue cannot redefine its alignment.");
            var numpad = tag.StartsWith("an", StringComparison.Ordinal);
            ValidateAlignment(tag[(numpad ? 2 : 1)..], numpad);
            alignmentSeen = true;
            return;
        }
        if (tag.StartsWith("pos", StringComparison.Ordinal))
        {
            if (positionSeen || playResX is null || playResY is null)
                throw new FormatException("Static ASS or SSA positioning requires one position and both script dimensions.");
            var parameter = tag[3..];
            if (parameter.Length < 5 || parameter[0] != '(' || parameter[^1] != ')')
                throw new FormatException("The ASS or SSA position must contain a complete coordinate pair.");
            var coordinates = parameter[1..^1].Split(',');
            if (coordinates.Length != 2) throw new FormatException("The ASS or SSA position must contain two coordinates.");
            var x = ReadNumber(coordinates[0].Trim());
            var y = ReadNumber(coordinates[1].Trim());
            if (x > playResX.Value || y > playResY.Value)
                throw new FormatException("The ASS or SSA position must lie within the script dimensions.");
            positionSeen = true;
            return;
        }
        throw new FormatException("The ASS or SSA override tag is unsupported or invalid.");
    }

    private static void ValidateAlignment(string value, bool numpad, bool nativeField = false)
    {
        var alignment = nativeField ? ReadNativeInteger(value) : ReadInteger(value);
        if (numpad ? alignment is < 1 or > 9 : alignment is not (1 or 2 or 3 or 5 or 6 or 7 or 9 or 10 or 11))
            throw new FormatException("The ASS or SSA alignment is invalid.");
    }

    private static void ValidateColor(string value, bool allowDecimal, bool allowTerminator)
    {
        uint color;
        if (value.StartsWith("&H", StringComparison.Ordinal))
        {
            var digits = value.AsSpan(2);
            if (allowTerminator && digits.EndsWith("&", StringComparison.Ordinal)) digits = digits[..^1];
            if (digits.Length is not (6 or 8)
                || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color))
                throw new FormatException("The ASS or SSA color is invalid.");
        }
        else
        {
            if (!allowDecimal || (value.Length > 1 && value[0] == '0'))
                throw new FormatException("The ASS or SSA color notation is unsupported.");
            color = (uint)ReadInteger(value);
        }
        if (color > 0x00ffffff) throw new FormatException("ASS and SSA color transparency is not supported.");
    }

    private static void RequireNonnegativeNumber(string value) => ReadNumber(value);

    private static double ReadNumber(string value)
    {
        if (value.Length == 0 || !double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < 0 || number > float.MaxValue || (number > 0 && (float)number == 0))
            throw new FormatException("The ASS or SSA numeric parameter is invalid or outside the supported range.");
        return number;
    }

    private static int ReadInteger(string value)
    {
        if (value.Length == 0 || !value.All(char.IsAsciiDigit)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new FormatException("The ASS or SSA integer parameter is invalid or outside the supported range.");
        return number;
    }

    private static int ReadPositiveInteger(string value)
    {
        var number = ReadNativeInteger(value);
        return number > 0 ? number : throw new FormatException("The ASS or SSA script dimension must be positive.");
    }

    private static int ReadNativeInteger(string value)
    {
        // The native style and resolution scanners use %i, which interprets leading zeroes as octal.
        if (value.Length > 1 && value[0] == '0')
            throw new FormatException("The native ASS or SSA parser requires an unambiguous decimal integer.");
        return ReadInteger(value);
    }

    private enum Section { None, ScriptInfo, Styles, Events, Metadata }
}
