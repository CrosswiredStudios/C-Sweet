using System.Globalization;
using CSweet.Contracts.Localization;
using CSweet.UI.Services;
using Microsoft.JSInterop;

namespace CSweet.UI.Localization;

/// <summary>
/// The active UI culture and time zone for this app instance. Resolved once at startup from, in order,
/// the signed-in user's saved preference, the choice stored in this browser, then the device language;
/// falls back to English. Changing the language updates <see cref="CultureInfo"/> defaults, re-points the
/// document's <c>lang</c>/<c>dir</c>, and raises <see cref="Changed"/> so the root re-renders the tree.
/// </summary>
public sealed class AppCulture : IAsyncDisposable
{
    public const string DefaultCode = SupportedCultures.Default;
    private const string ModulePath = "./_content/CSweet.UI/js/localization.js";

    private readonly IJSRuntime? _js;
    private readonly AuthSessionStore _session;
    private IJSObjectReference? _module;
    private Task? _initialization;
    private string? _deviceTimeZoneId;
    private string? _deviceLanguage;

    public AppCulture(IJSRuntime? js, AuthSessionStore session)
    {
        _js = js;
        _session = session;
        _session.Changed += OnSessionChanged;
        Culture = CultureInfo.GetCultureInfo(DefaultCode);
    }

    /// <summary>Supported culture code in effect, e.g. "en", "pt-BR".</summary>
    public string CurrentCode { get; private set; } = DefaultCode;

    public CultureInfo Culture { get; private set; }

    public SupportedCulture Current => SupportedCultures.Find(CurrentCode);

    public bool IsRightToLeft => Current.RightToLeft;

    /// <summary>Explicit user choice, or null when following the device language.</summary>
    public string? PreferredCode { get; private set; }

    /// <summary>IANA id chosen by the user, or null to follow the device.</summary>
    public string? PreferredTimeZoneId { get; private set; }

    /// <summary>The zone used when presenting times.</summary>
    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Local;

    /// <summary>The device's IANA time zone as reported by the browser, or null when unavailable.</summary>
    public string? DeviceTimeZoneId => NormalizeTimeZone(_deviceTimeZoneId);

    /// <summary>The device language as reported by the browser, after mapping to a supported culture.</summary>
    public string DeviceCode => SupportedCultures.Normalize(_deviceLanguage) ?? DefaultCode;

    public bool IsReady { get; private set; }

    public bool PersistenceUnavailable { get; private set; }

    public event Action? Changed;

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        string? stored = null;
        string? storedTimeZone = null;
        try
        {
            if (_js is null)
            {
                throw new InvalidOperationException("JavaScript interop is unavailable.");
            }

            _module = await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            var snapshot = await _module.InvokeAsync<DeviceSnapshot>("read");
            stored = snapshot.StoredLanguage;
            storedTimeZone = snapshot.StoredTimeZone;
            _deviceLanguage = snapshot.DeviceLanguage;
            _deviceTimeZoneId = snapshot.DeviceTimeZone;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            PersistenceUnavailable = true;
        }

        var session = _session.Current;
        PreferredCode = SupportedCultures.Normalize(session?.PreferredLanguage) ?? SupportedCultures.Normalize(stored);
        PreferredTimeZoneId = NormalizeTimeZone(session?.TimeZoneId) ?? NormalizeTimeZone(storedTimeZone);
        ApplyTimeZone();
        await ApplyLanguageAsync(PreferredCode ?? DeviceCode, raise: false);
        IsReady = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Sets the language. Null means "follow the device". Persists locally and, when signed in, to the account.
    /// </summary>
    public async Task SetLanguageAsync(string? code, IAuthenticationApiClient? account = null)
    {
        var normalized = SupportedCultures.Normalize(code);
        if (code is not null && normalized is null)
        {
            return;
        }

        PreferredCode = normalized;
        await StoreAsync("language", normalized);
        if (account is not null && _session.Current?.IsAuthenticated == true)
        {
            try
            {
                await account.UpdatePreferencesAsync(new CSweet.Contracts.Auth.UpdateUserPreferencesRequest(normalized, PreferredTimeZoneId));
            }
            catch (HttpRequestException)
            {
                // The local choice still applies; the account preference syncs on the next successful save.
            }
        }

        await ApplyLanguageAsync(normalized ?? DeviceCode, raise: true);
    }

    public async Task SetTimeZoneAsync(string? timeZoneId, IAuthenticationApiClient? account = null)
    {
        var normalized = NormalizeTimeZone(timeZoneId);
        if (timeZoneId is not null && normalized is null)
        {
            return;
        }

        PreferredTimeZoneId = normalized;
        await StoreAsync("timezone", normalized);
        if (account is not null && _session.Current?.IsAuthenticated == true)
        {
            try
            {
                await account.UpdatePreferencesAsync(new CSweet.Contracts.Auth.UpdateUserPreferencesRequest(PreferredCode, normalized));
            }
            catch (HttpRequestException)
            {
            }
        }

        ApplyTimeZone();
        Changed?.Invoke();
    }

    private async Task ApplyLanguageAsync(string code, bool raise)
    {
        var culture = ResolveCulture(code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CurrentCode = code;
        Culture = culture;

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("applyDocumentCulture", code, IsRightToLeft ? "rtl" : "ltr");
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
            {
            }
        }

        if (raise)
        {
            Changed?.Invoke();
        }
    }

    private static CultureInfo ResolveCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private void ApplyTimeZone()
    {
        TimeZone = ResolveTimeZone(PreferredTimeZoneId) ?? ResolveTimeZone(_deviceTimeZoneId) ?? TimeZoneInfo.Local;
    }

    private static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    private static string? NormalizeTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        id = id.Trim();
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out _) ? id : null;
    }

    private async Task StoreAsync(string key, string? value)
    {
        if (_module is null)
        {
            PersistenceUnavailable = true;
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("store", key, value);
            PersistenceUnavailable = false;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            PersistenceUnavailable = true;
        }
    }

    // After sign-in the account preference wins over whatever this browser had stored.
    private void OnSessionChanged()
    {
        if (!IsReady || _session.Current is not { IsAuthenticated: true } session)
        {
            return;
        }

        _ = ReconcileAsync(session.PreferredLanguage, session.TimeZoneId);
    }

    private async Task ReconcileAsync(string? preferredLanguage, string? timeZoneId)
    {
        var changed = false;
        var language = SupportedCultures.Normalize(preferredLanguage);
        if (language is not null && !string.Equals(language, PreferredCode, StringComparison.OrdinalIgnoreCase))
        {
            PreferredCode = language;
            await StoreAsync("language", language);
            await ApplyLanguageAsync(language, raise: false);
            changed = true;
        }

        var zone = NormalizeTimeZone(timeZoneId);
        if (zone is not null && !string.Equals(zone, PreferredTimeZoneId, StringComparison.Ordinal))
        {
            PreferredTimeZoneId = zone;
            await StoreAsync("timezone", zone);
            ApplyTimeZone();
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.Changed -= OnSessionChanged;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private sealed record DeviceSnapshot(string? StoredLanguage, string? StoredTimeZone, string? DeviceLanguage, string? DeviceTimeZone);
}
