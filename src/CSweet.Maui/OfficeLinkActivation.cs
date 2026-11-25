using CSweet.UI.Mobile;

namespace CSweet.Maui;

internal static class OfficeLinkActivation
{
    private const string Pending = "office-link.pending";
    private static readonly SemaphoreSlim Showing = new(1);

    internal static bool Accept(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.IdnHost != "account.c-sweet.com" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/office/link" ||
            uri.Fragment.Length != 0 || uri.Query.Length is 0 or > 1400) return false;
        Preferences.Default.Set(Pending, uri.AbsoluteUri);
        MainThread.BeginInvokeOnMainThread(Resume);
        return true;
    }

    internal static async void Resume()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        var services = page?.Handler?.MauiContext?.Services;
        if (services == null || page == null || !await Showing.WaitAsync(0)) return;
        var raw = Preferences.Default.Get(Pending, "");
        try
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var request)) return;
            var fields = System.Web.HttpUtility.ParseQueryString(request.Query);
            if (!long.TryParse(fields["until"], out var until) || until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await page.DisplayAlertAsync("Request expired", "Open C-Sweet Office on the computer and scan its new code.", "OK");
                Preferences.Default.Remove(Pending); return;
            }
            if (!await page.DisplayAlertAsync("Connect an Office", "Continue to your C-Sweet account to review the computer and matching code.", "Continue", "Cancel"))
            { Preferences.Default.Remove(Pending); return; }
            var account = services.GetRequiredService<MobileAccountSession>();
            var signedIn = await account.RestoreAsync();
            if (signedIn)
            {
                try { await account.GetAccessTokenAsync(); }
                catch (AccountSessionExpiredException) { signedIn = false; }
            }
            if (!signedIn && (await account.SignInAsync(services.GetRequiredService<IBrowserAuthenticator>(), false)).Status != SignInStatus.Succeeded)
                return;
            // This browser-only sibling route cannot be intercepted by this app's verified link.
            var opened = await Browser.Default.OpenAsync(new Uri("https://account.c-sweet.com/office/link/browser" + request.Query), BrowserLaunchMode.SystemPreferred);
            if (opened && Preferences.Default.Get(Pending, "") == raw) Preferences.Default.Remove(Pending);
        }
        catch (Exception) { await page.DisplayAlertAsync("Could not open the request", "Scan the code again or open its link in your phone’s browser.", "OK"); }
        finally { Showing.Release(); }
    }
}
