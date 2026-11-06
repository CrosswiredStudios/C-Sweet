using CSweet.UI.Services;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed class AppearanceStateTests
{
    [Fact]
    public void EveryThemeKeepsSecondaryTextReadableOnEverySurface()
    {
        static double Luminance(string color)
        {
            double Channel(int start)
            {
                var value = Convert.ToInt32(color.Substring(start, 2), 16) / 255d;
                return value <= .03928 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
        }
        foreach (var theme in AppearanceThemes.All)
        foreach (var surface in new[] { "paper", "surface", "elevated" })
        foreach (var role in new[] { "ink", "muted" })
        {
            var foreground = Luminance(theme.Tokens[role]);
            var background = Luminance(theme.Tokens[surface]);
            var ratio = (Math.Max(foreground, background) + .05) / (Math.Min(foreground, background) + .05);
            Assert.True(ratio >= 4.5, $"{theme.Id}: {role} text on {surface} is {ratio:F2}:1");
        }
    }

    [Fact]
    public async Task PreferenceSurvivesNewSessionAndIgnoresInvalidValues()
    {
        var storage = new Storage();
        var state = new AppearanceState(storage);
        var changes = 0;
        state.Changed += () => changes++;
        await state.SetThemeAsync("night");
        Assert.True(changes > 0);
        var next = new AppearanceState(storage);
        await next.InitializeAsync();
        Assert.Equal("night-glass", next.Theme);
        await next.SetThemeAsync("invalid");
        Assert.Equal("night-glass", next.Theme);
        storage.Value = "invalid";
        var fallback = new AppearanceState(storage);
        await fallback.InitializeAsync();
        Assert.Equal("night-glass", fallback.Theme);
    }

    [Fact]
    public async Task StorageFailureStillAppliesThemeForTheSession()
    {
        var state = new AppearanceState(new Storage { Fail = true });
        await state.InitializeAsync();
        await state.SetThemeAsync("night");
        Assert.Equal("night-glass", state.Theme);
        Assert.True(state.PersistenceUnavailable);
    }

    [Fact]
    public async Task DelayedStorageReadDoesNotOverrideNewSelection()
    {
        var read = new TaskCompletionSource<string?>();
        var storage = new Storage { PendingRead = read.Task };
        var state = new AppearanceState(storage);
        var loading = state.InitializeAsync();
        await state.SetThemeAsync("csweet-daylight");
        read.SetResult("aurora");
        await loading;
        Assert.Equal("csweet-daylight", state.Theme);
    }

    [Theory]
    [InlineData(null, "night-glass")]
    [InlineData("invalid", "night-glass")]
    [InlineData("gold", "signature-gold")]
    [InlineData("aurora", "csweet-daylight")]
    [InlineData("day", "csweet-daylight")]
    [InlineData("night", "night-glass")]
    [InlineData("retired-off-white", "off-white")]
    [InlineData("retired-gold", "signature-gold")]
    public async Task StoredPreferenceMigratesAndAppliesToDocument(string? stored, string expected)
    {
        var storage = new Storage { Value = stored };
        var state = new AppearanceState(storage);
        await state.InitializeAsync();
        Assert.Equal(expected, state.Theme);
        Assert.Equal(expected, storage.AppliedTheme);
    }

    [Fact]
    public void CompleteCatalogHasBothSchemesAndNeutralNightGlass()
    {
        Assert.Equal(42, AppearanceThemes.All.Count(t => t.Family != "classic"));
        Assert.Equal(43, AppearanceThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.All(AppearanceThemes.All, theme => Assert.Equal(26, theme.Tokens.Count));
        Assert.Contains(AppearanceThemes.All, t => !t.IsDark);
        Assert.Contains(AppearanceThemes.All, t => t.IsDark);
        var night = AppearanceThemes.Default;
        Assert.Equal("#050505", night.Tokens["paper"]);
        Assert.Equal("#141414", night.Tokens["surface"]);
        Assert.Equal("#1b1b1b", night.Tokens["elevated"]);
        var surface = night.CreateMudTheme().PaletteDark.Surface;
        Assert.Equal(20, (int)surface.R);
        Assert.Equal(20, (int)surface.G);
        Assert.Equal(20, (int)surface.B);
    }

    [Fact]
    public async Task EveryPresetCanBeAppliedAndRestored()
    {
        foreach (var preset in AppearanceThemes.All)
        {
            var storage = new Storage();
            var state = new AppearanceState(storage);
            await state.SetThemeAsync(preset.Id);
            var restored = new AppearanceState(storage);
            await restored.InitializeAsync();
            Assert.Equal(preset.Id, restored.Theme);
            Assert.Equal(preset.Id, storage.AppliedTheme);
            Assert.Equal(preset.IsDark, restored.IsDark);
        }
    }

    private sealed class Storage : IJSRuntime
    {
        public string? Value { get; set; }
        public string? AppliedTheme { get; private set; }
        public bool Fail { get; init; }
        public Task<string?>? PendingRead { get; init; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "csweetAppearance.apply")
            {
                AppliedTheme = (string)args![0]!;
                return default!;
            }
            if (Fail) throw new JSException("Storage unavailable");
            Assert.Equal("csweet.appearance", args![0]);
            if (identifier == "localStorage.getItem") return (T)(object)(PendingRead is null ? Value! : (await PendingRead)!);
            Assert.Equal("localStorage.setItem", identifier);
            Value = (string)args[1]!;
            return default!;
        }
    }
}
