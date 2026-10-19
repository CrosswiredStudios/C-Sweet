using System.Globalization;

namespace CSweet.UI.Localization;

/// <summary>
/// CLDR cardinal plural categories for the cultures C-Sweet ships, plus a sensible default for the rest.
/// Categories follow Unicode CLDR: zero, one, two, few, many, other.
/// </summary>
public static class PluralRules
{
    public static string Cardinal(CultureInfo culture, double n)
    {
        var language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        var isInteger = Math.Abs(n % 1) < double.Epsilon;
        var i = isInteger ? (long)Math.Abs(n) : -1;

        switch (language)
        {
            case "ja":
            case "zh":
            case "ko":
            case "vi":
            case "th":
            case "id":
            case "ms":
                return "other";

            case "fr":
            case "pt":
            case "hi":
                // CLDR: one when the integer part is 0 or 1 (0.5 items → one in French/Brazilian Portuguese).
                return Math.Abs(n) < 2 ? "one" : "other";

            case "ru":
            case "uk":
            case "be":
                if (!isInteger)
                {
                    return "other";
                }
                if (i % 10 == 1 && i % 100 != 11) return "one";
                if (i % 10 >= 2 && i % 10 <= 4 && (i % 100 < 12 || i % 100 > 14)) return "few";
                return "many";

            case "pl":
                if (!isInteger)
                {
                    return "other";
                }
                if (i == 1) return "one";
                if (i % 10 >= 2 && i % 10 <= 4 && (i % 100 < 12 || i % 100 > 14)) return "few";
                return "many";

            case "cs":
            case "sk":
                if (!isInteger) return "many";
                if (i == 1) return "one";
                if (i >= 2 && i <= 4) return "few";
                return "other";

            case "ar":
                if (!isInteger) return "other";
                if (i == 0) return "zero";
                if (i == 1) return "one";
                if (i == 2) return "two";
                if (i % 100 >= 3 && i % 100 <= 10) return "few";
                if (i % 100 >= 11 && i % 100 <= 99) return "many";
                return "other";

            case "he":
                if (!isInteger) return "other";
                if (i == 1) return "one";
                if (i == 2) return "two";
                return "other";

            default:
                // en, de, es, it, nl, sv, da, nb, fi, tr, el, hu, bg, ...: one for exactly 1, otherwise other.
                return isInteger && i == 1 ? "one" : "other";
        }
    }

    /// <summary>Ordinal categories (1st, 2nd, 3rd). Only English has distinct forms among shipped cultures.</summary>
    public static string Ordinal(CultureInfo culture, double n)
    {
        if (culture.TwoLetterISOLanguageName != "en" || Math.Abs(n % 1) >= double.Epsilon)
        {
            return "other";
        }

        var i = (long)Math.Abs(n);
        if (i % 10 == 1 && i % 100 != 11) return "one";
        if (i % 10 == 2 && i % 100 != 12) return "two";
        if (i % 10 == 3 && i % 100 != 13) return "few";
        return "other";
    }
}
