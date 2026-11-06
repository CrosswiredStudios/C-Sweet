using Microsoft.JSInterop;
using MudBlazor;

namespace CSweet.UI.Services;

/// <summary>Browser-local appearance, shared by every layout and page in this session.</summary>
public sealed class AppearanceState(IJSRuntime js)
{
    private const string StorageKey = "csweet.appearance";
    private Task? _initialization;
    private int _revision;
    public string Theme { get; private set; } = AppearanceThemes.DefaultId;
    public AppearanceTheme CurrentTheme => AppearanceThemes.Resolve(Theme);
    public bool IsDark => CurrentTheme.IsDark;
    public MudTheme MudTheme { get; private set; } = AppearanceThemes.Default.CreateMudTheme();
    public bool PersistenceUnavailable { get; private set; }
    public event Action? Changed;

    // Call after the first interactive render, when browser storage is available.
    public Task InitializeAsync() => _initialization ??= LoadAsync();

    private async Task LoadAsync()
    {
        var revision = _revision;
        try
        {
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (revision == _revision)
            {
                Select(AppearanceThemes.ResolveStored(stored));
                await js.InvokeVoidAsync("csweetAppearance.apply", Theme);
                Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            PersistenceUnavailable = true;
            Changed?.Invoke();
        }
    }

    private void Select(AppearanceTheme theme)
    {
        Theme = theme.Id;
        MudTheme = theme.CreateMudTheme();
    }

    public async Task SetThemeAsync(string theme)
    {
        if (AppearanceThemes.Find(theme) is not { } selected) return;
        ++_revision;
        Select(selected);
        theme = selected.Id;
        try
        {
            await js.InvokeVoidAsync("csweetAppearance.apply", theme);
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, theme);
            PersistenceUnavailable = false;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            PersistenceUnavailable = true;
        }
        Changed?.Invoke();
    }
}
