using CSweet.UI.Mobile;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace CSweet.Maui;

/// <summary>System authentication browser (Custom Tabs on Android, ASWebAuthenticationSession on iOS) for C-Sweet sign-in.</summary>
internal sealed class MauiBrowserAuthenticator : IBrowserAuthenticator
{
    public async Task<BrowserResult> AuthenticateAsync(Uri start, Uri callback, CancellationToken ct)
    {
        try
        {
            var result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions { Url = start, CallbackUrl = callback, PrefersEphemeralWebBrowserSession = false }));
            return new BrowserResult(result.Properties);
        }
        catch (TaskCanceledException)
        {
            return new BrowserResult(null);
        }
    }
}

/// <summary>Android Keystore / iOS Keychain backed storage. A damaged store is cleared so the person can sign in again.</summary>
internal sealed class MauiSecureValueStore : ISecureValueStore
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception)
        {
            SecureStorage.Default.RemoveAll();
            return null;
        }
    }

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
