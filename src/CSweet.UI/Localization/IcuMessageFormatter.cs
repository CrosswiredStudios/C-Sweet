using System.Globalization;
using System.Text;

namespace CSweet.UI.Localization;

/// <summary>
/// A compact ICU MessageFormat implementation covering what the resource files use:
/// <c>{name}</c>, <c>{name, number[, integer|percent|currency]}</c>, <c>{name, date[, short|medium|long|full]}</c>,
/// <c>{name, time[, short|medium|long]}</c>, <c>{name, plural, offset:n =0{…} one{…} other{…}}</c>,
/// <c>{name, selectordinal, …}</c> and <c>{name, select, key{…} other{…}}</c>, with <c>#</c> inside plural
/// branches and ICU apostrophe quoting (<c>'{'</c> is a literal brace, <c>''</c> is an apostrophe).
/// Malformed patterns never throw: the offending span is emitted as written so a bad translation shows up
/// on screen instead of taking the page down.
/// </summary>
public static class IcuMessageFormatter
{
    public static string Format(string pattern, IReadOnlyDictionary<string, object?>? args, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(pattern) || (pattern.IndexOf('{') < 0 && pattern.IndexOf('\'') < 0))
        {
            return pattern ?? string.Empty;
        }

        var output = new StringBuilder(pattern.Length + 16);
        FormatInto(output, pattern, args ?? EmptyArgs, culture, pluralValue: null);
        return output.ToString();
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyArgs = new Dictionary<string, object?>();

    private static void FormatInto(StringBuilder output, string pattern, IReadOnlyDictionary<string, object?> args, CultureInfo culture, double? pluralValue)
    {
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                i = ConsumeQuote(output, pattern, i, pluralValue.HasValue);
                continue;
            }

            if (c == '#' && pluralValue.HasValue)
            {
                output.Append(FormatNumber(pluralValue.Value, null, culture));
                i++;
                continue;
            }

            if (c == '{')
            {
                var close = FindClosingBrace(pattern, i);
                if (close < 0)
                {
                    output.Append(pattern, i, pattern.Length - i);
                    return;
                }

                FormatArgument(output, pattern.AsSpan(i + 1, close - i - 1).ToString(), args, culture);
                i = close + 1;
                continue;
            }

            output.Append(c);
            i++;
        }
    }

    private static int ConsumeQuote(StringBuilder output, string pattern, int i, bool inPlural)
    {
        // '' → literal apostrophe.
        if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
        {
            output.Append('\'');
            return i + 2;
        }

        // An apostrophe only starts a quoted span when it precedes a syntax character; "don't" stays literal.
        if (i + 1 < pattern.Length && IsQuotable(pattern[i + 1], inPlural))
        {
            var end = pattern.IndexOf('\'', i + 1);
            if (end < 0)
            {
                output.Append(pattern, i + 1, pattern.Length - i - 1);
                return pattern.Length;
            }

            output.Append(pattern, i + 1, end - i - 1);
            return end + 1;
        }

        output.Append('\'');
        return i + 1;
    }

    private static bool IsQuotable(char c, bool inPlural) => c is '{' or '}' or '|' || (inPlural && c == '#');

    private static int FindClosingBrace(string pattern, int open)
    {
        var depth = 0;
        var i = open;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                if (i + 1 < pattern.Length && IsQuotable(pattern[i + 1], true))
                {
                    var end = pattern.IndexOf('\'', i + 1);
                    i = end < 0 ? pattern.Length : end + 1;
                    continue;
                }
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static void FormatArgument(StringBuilder output, string body, IReadOnlyDictionary<string, object?> args, CultureInfo culture)
    {
        // body is "name" | "name, type" | "name, type, style…"
        var firstComma = IndexOfTopLevel(body, ',', 0);
        var name = (firstComma < 0 ? body : body[..firstComma]).Trim();
        if (name.Length == 0)
        {
            output.Append('{').Append(body).Append('}');
            return;
        }

        args.TryGetValue(name, out var value);

        if (firstComma < 0)
        {
            output.Append(FormatValue(value, null, culture));
            return;
        }

        var secondComma = IndexOfTopLevel(body, ',', firstComma + 1);
        var type = (secondComma < 0 ? body[(firstComma + 1)..] : body[(firstComma + 1)..secondComma]).Trim();
        var style = secondComma < 0 ? string.Empty : body[(secondComma + 1)..].Trim();

        switch (type)
        {
            case "number":
                output.Append(ToDouble(value, out var number) ? FormatNumber(number, style, culture) : FormatValue(value, null, culture));
                return;
            case "date":
                output.Append(FormatDate(value, style, culture, isTime: false));
                return;
            case "time":
                output.Append(FormatDate(value, style, culture, isTime: true));
                return;
            case "plural":
            case "selectordinal":
                FormatPlural(output, value, style, args, culture, ordinal: type == "selectordinal");
                return;
            case "select":
                FormatSelect(output, value, style, args, culture);
                return;
            default:
                output.Append('{').Append(body).Append('}');
                return;
        }
    }

    private static void FormatPlural(StringBuilder output, object? value, string style, IReadOnlyDictionary<string, object?> args, CultureInfo culture, bool ordinal)
    {
        if (!ToDouble(value, out var n))
        {
            n = 0;
        }

        var branches = ParseBranches(style, out var offset);
        var adjusted = n - offset;
        string? chosen = null;
        var exactKey = "=" + n.ToString(CultureInfo.InvariantCulture);
        if (branches.TryGetValue(exactKey, out var exact))
        {
            chosen = exact;
        }
        else
        {
            var category = ordinal ? PluralRules.Ordinal(culture, adjusted) : PluralRules.Cardinal(culture, adjusted);
            if (!branches.TryGetValue(category, out chosen))
            {
                branches.TryGetValue("other", out chosen);
            }
        }

        if (chosen is null)
        {
            output.Append(FormatNumber(n, null, culture));
            return;
        }

        FormatInto(output, chosen, args, culture, adjusted);
    }

    private static void FormatSelect(StringBuilder output, object? value, string style, IReadOnlyDictionary<string, object?> args, CultureInfo culture)
    {
        var branches = ParseBranches(style, out _);
        var key = value switch
        {
            null => "other",
            bool b => b ? "true" : "false",
            Enum e => e.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "other"
        };

        if (!branches.TryGetValue(key, out var chosen) && !branches.TryGetValue("other", out chosen))
        {
            output.Append(key);
            return;
        }

        FormatInto(output, chosen, args, culture, pluralValue: null);
    }

    /// <summary>Parses "offset:1 =0{none} one{# item} other{# items}" into selector → raw sub-message.</summary>
    private static Dictionary<string, string> ParseBranches(string style, out double offset)
    {
        offset = 0;
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < style.Length)
        {
            while (i < style.Length && char.IsWhiteSpace(style[i])) i++;
            if (i >= style.Length) break;

            var open = style.IndexOf('{', i);
            if (open < 0) break;
            var selector = style[i..open].Trim();

            if (selector.StartsWith("offset:", StringComparison.Ordinal))
            {
                // "offset:1 one" — the offset shares the selector slot with the first real selector.
                var rest = selector["offset:".Length..].Trim();
                var space = rest.IndexOfAny([' ', '\t', '\n', '\r']);
                var offsetText = space < 0 ? rest : rest[..space];
                double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out offset);
                selector = space < 0 ? string.Empty : rest[(space + 1)..].Trim();
            }

            var close = FindClosingBrace(style, open);
            if (close < 0) break;
            if (selector.Length > 0)
            {
                branches[selector] = style[(open + 1)..close];
            }

            i = close + 1;
        }

        return branches;
    }

    private static int IndexOfTopLevel(string text, char target, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == target && depth == 0) return i;
        }

        return -1;
    }

    private static bool ToDouble(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case double d: number = d; return true;
            case float f: number = f; return true;
            case int n: number = n; return true;
            case long l: number = l; return true;
            case short s: number = s; return true;
            case byte b: number = b; return true;
            case uint u: number = u; return true;
            case ulong ul: number = ul; return true;
            case decimal m: number = (double)m; return true;
            case string text:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default:
                number = 0;
                return false;
        }
    }

    private static string FormatNumber(double number, string? style, CultureInfo culture)
    {
        var isInteger = Math.Abs(number % 1) < double.Epsilon && Math.Abs(number) < 1e15;
        return style switch
        {
            "integer" => Math.Round(number).ToString("N0", culture),
            "percent" => number.ToString("P0", culture),
            "currency" => number.ToString("C", culture),
            null or "" => isInteger ? number.ToString("N0", culture) : number.ToString("#,##0.###", culture),
            _ => number.ToString(style, culture)
        };
    }

    private static string FormatDate(object? value, string style, CultureInfo culture, bool isTime)
    {
        var format = (style, isTime) switch
        {
            ("short", false) => "d",
            ("medium", false) or ("long", false) or ("full", false) or ("", false) => "D",
            ("short", true) => "t",
            (_, true) => "T",
            _ => style
        };

        return value switch
        {
            DateTimeOffset dto => dto.ToString(format, culture),
            DateTime dt => dt.ToString(format, culture),
            DateOnly d => d.ToString(format == "t" || format == "T" ? "d" : format, culture),
            TimeOnly t => t.ToString(format == "d" || format == "D" ? "t" : format, culture),
            _ => FormatValue(value, null, culture)
        };
    }

    private static string FormatValue(object? value, string? style, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case DateTimeOffset or DateTime or DateOnly or TimeOnly:
                return FormatDate(value, "short", culture, isTime: false);
            case IFormattable when ToDouble(value, out var number):
                return FormatNumber(number, style, culture);
            case IFormattable formattable:
                return formattable.ToString(null, culture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }
}
