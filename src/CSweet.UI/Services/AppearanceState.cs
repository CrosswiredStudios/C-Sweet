using Microsoft.JSInterop;

namespace CSweet.UI.Services;

/// <summary>Browser-local appearance, shared by every layout and page in this session.</summary>
public sealed class AppearanceState(IJSRuntime js)
{
    private const string StorageKey = "csweet.appearance";
    private Task? _initialization;
    private int _revision;
    public string Theme { get; private set; } = "aurora";
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
                Theme = stored == "gold" ? "gold" : "aurora";
                Changed?.Invoke();
            }
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            PersistenceUnavailable = true;
            Changed?.Invoke();
        }
    }

    public async Task SetThemeAsync(string theme)
    {
        if (theme is not ("aurora" or "gold")) return;
        ++_revision;
        Theme = theme;
        Changed?.Invoke();
        try
        {
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
