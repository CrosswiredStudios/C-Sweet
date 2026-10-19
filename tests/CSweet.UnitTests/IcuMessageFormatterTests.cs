using System.Globalization;
using CSweet.Contracts.Localization;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class IcuMessageFormatterTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");

    private static string Format(string pattern, object? args = null, string culture = "en") =>
        IcuMessageFormatter.Format(pattern, ToArgs(args), CultureInfo.GetCultureInfo(culture));

    private static IReadOnlyDictionary<string, object?>? ToArgs(object? args) =>
        args is null ? null : args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));

    [Fact]
    public void Plain_text_passes_through() => Assert.Equal("Hello, world.", Format("Hello, world."));

    [Fact]
    public void Simple_argument_is_substituted() => Assert.Equal("Hello, Ada.", Format("Hello, {name}.", new { name = "Ada" }));

    [Fact]
    public void Missing_argument_renders_empty() => Assert.Equal("Hello, .", Format("Hello, {name}.", new { other = 1 }));

    [Theory]
    [InlineData(0, "0 items")]
    [InlineData(1, "1 item")]
    [InlineData(2, "2 items")]
    [InlineData(1234, "1,234 items")]
    public void English_plural_picks_one_or_other(int count, string expected) =>
        Assert.Equal(expected, Format("{count, plural, one {# item} other {# items}}", new { count }));

    [Fact]
    public void Exact_match_wins_over_category() =>
        Assert.Equal("No items", Format("{count, plural, =0 {No items} one {# item} other {# items}}", new { count = 0 }));

    [Theory]
    [InlineData(1, "one")]
    [InlineData(2, "few")]
    [InlineData(5, "many")]
    [InlineData(11, "many")]
    [InlineData(21, "one")]
    [InlineData(22, "few")]
    [InlineData(112, "many")]
    public void Russian_plural_categories(int count, string expected) =>
        Assert.Equal(expected, Format("{count, plural, one {one} few {few} many {many} other {other}}", new { count }, "ru"));

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(1, "one")]
    [InlineData(2, "two")]
    [InlineData(3, "few")]
    [InlineData(11, "many")]
    [InlineData(100, "other")]
    public void Arabic_plural_categories(int count, string expected) =>
        Assert.Equal(expected, Format("{count, plural, zero {zero} one {one} two {two} few {few} many {many} other {other}}", new { count }, "ar"));

    [Theory]
    [InlineData(0, "one")]
    [InlineData(1, "one")]
    [InlineData(2, "other")]
    public void French_treats_zero_and_one_as_singular(int count, string expected) =>
        Assert.Equal(expected, Format("{count, plural, one {one} other {other}}", new { count }, "fr"));

    [Fact]
    public void Japanese_only_has_other() =>
        Assert.Equal("1件", Format("{count, plural, other {#件}}", new { count = 1 }, "ja"));

    [Fact]
    public void Offset_adjusts_the_hash_value() =>
        Assert.Equal("Ada and 2 others", Format("{count, plural, offset:1 =1 {{name} alone} one {{name} and # other} other {{name} and # others}}", new { count = 3, name = "Ada" }));

    [Fact]
    public void Select_chooses_branch_and_falls_back_to_other()
    {
        const string pattern = "{status, select, active {Active} paused {Paused} other {Unknown}}";
        Assert.Equal("Paused", Format(pattern, new { status = "paused" }));
        Assert.Equal("Unknown", Format(pattern, new { status = "weird" }));
        Assert.Equal("Running", Format("{status, select, Active {Running} other {Stopped}}", new { status = Status.Active }));
    }

    [Fact]
    public void Nested_arguments_inside_branches_resolve() =>
        Assert.Equal("Ada has 2 tasks", Format("{name} has {count, plural, one {# task} other {# tasks}}", new { name = "Ada", count = 2 }));

    [Fact]
    public void Apostrophes_follow_icu_quoting()
    {
        Assert.Equal("don't", Format("don't"));
        Assert.Equal("it's {literal}", Format("it''s '{literal}'"));
        Assert.Equal("# stays in text", Format("# stays in text"));
        Assert.Equal("1 # 1", Format("{count, plural, other {# '#' #}}", new { count = 1 }));
    }

    [Fact]
    public void Numbers_follow_culture()
    {
        Assert.Equal("1,234.5", Format("{value}", new { value = 1234.5 }, "en"));
        Assert.Equal("1.234,5", Format("{value}", new { value = 1234.5 }, "de"));
        Assert.Equal("1,234", Format("{value, number, integer}", new { value = 1234.4 }));
        Assert.Equal("50%", Format("{value, number, percent}", new { value = 0.5 }));
    }

    [Fact]
    public void Dates_follow_culture()
    {
        var date = new DateTimeOffset(2026, 10, 19, 15, 30, 0, TimeSpan.Zero);
        Assert.Equal(date.ToString("d", En), Format("{when, date, short}", new { when = date }));
        Assert.Equal(date.ToString("t", En), Format("{when, time, short}", new { when = date }));
        Assert.Equal(date.ToString("d", CultureInfo.GetCultureInfo("de")), Format("{when, date, short}", new { when = date }, "de"));
    }

    [Fact]
    public void Malformed_patterns_do_not_throw()
    {
        Assert.Equal("open {brace", Format("open {brace"));
        Assert.Equal("{count, plural, one {x}", Format("{count, plural, one {x}", new { count = 1 }));
        Assert.Equal("{, number}", Format("{, number}"));
    }

    [Fact]
    public void Supported_cultures_normalize_browser_tags()
    {
        Assert.Equal("en", SupportedCultures.Normalize("en-GB"));
        Assert.Equal("es", SupportedCultures.Normalize("es-MX"));
        Assert.Equal("pt-BR", SupportedCultures.Normalize("pt-PT"));
        Assert.Equal("zh-Hans", SupportedCultures.Normalize("zh-CN"));
        Assert.Equal("zh-Hans", SupportedCultures.Normalize("zh"));
        Assert.Null(SupportedCultures.Normalize("zh-TW"));
        Assert.Null(SupportedCultures.Normalize("xx"));
        Assert.Null(SupportedCultures.Normalize(""));
        Assert.Equal("ar", SupportedCultures.Normalize("AR-eg"));
    }

    [Fact]
    public void English_resources_are_embedded_and_resolve()
    {
        var resources = new LocalizationResources();
        var english = resources.GetTable("en");
        Assert.NotEmpty(english);
        Assert.Equal("Save", english["Common.Save"]);
        // Per-culture completeness is enforced by tools/localization/check-resources.mjs --strict.
    }

    [Fact]
    public void Translations_keep_the_english_placeholders()
    {
        var resources = new LocalizationResources();
        var english = resources.GetTable("en");
        foreach (var culture in SupportedCultures.All.Select(c => c.Code).Where(c => c != "en"))
        {
            var table = resources.GetTable(culture);
            foreach (var (key, value) in table)
            {
                if (!english.TryGetValue(key, out var source))
                {
                    continue; // Orphaned keys are reported by the coverage script, not here.
                }

                Assert.True(
                    Placeholders(source).SetEquals(Placeholders(value)),
                    $"{culture}/{key}: placeholders differ. en='{source}' {culture}='{value}'");
            }
        }
    }

    private static HashSet<string> Placeholders(string pattern)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        var start = -1;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                if (depth == 0) start = i + 1;
                depth++;
            }
            else if (pattern[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    var body = pattern[start..i];
                    var comma = body.IndexOf(',');
                    names.Add((comma < 0 ? body : body[..comma]).Trim());
                    start = -1;
                }
            }
        }

        return names;
    }

    private enum Status { Active, Paused }
}
