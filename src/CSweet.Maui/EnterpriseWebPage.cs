using CSweet.UI.Mobile;
using CSweet.UI.Localization;
using CSweet.UI.Services;

namespace CSweet.Maui;

/// <summary>
/// Opens an enterprise's web app (TS-13 thin-shell prototype). Android only for now: the app runs in a WebView at
/// <c>https://hq-{installation}.csweet.invalid/</c>, an origin per enterprise that never resolves, so its storage is
/// kept apart from other enterprises and nothing it loads can come from the network.
/// </summary>
internal sealed class EnterpriseAppHost(HeadquartersTunnels tunnels, IAppLocalizer localizer) : IEnterpriseAppHost
{
#if ANDROID
    public bool IsAvailable => true;
#else
    public bool IsAvailable => false;
#endif

    public async Task<bool> OpenAsync(Guid installationId, string displayName)
    {
        if (tunnels.Get(installationId) is not { } client) return false;
        var cache = Path.Combine(FileSystem.CacheDirectory, "enterprise-web");
        var back = localizer["Mobile.Enterprise.Back"].ToString();
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var navigation = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
            if (navigation is null) return;
            await navigation.PushModalAsync(new EnterpriseWebPage(installationId, displayName, new EnterpriseAssets(client, cache), back));
        });
        return true;
    }
}

internal sealed class EnterpriseWebPage : ContentPage
{
    public static string Origin(Guid installationId) => $"https://hq-{installationId:N}{ShellBridge.OriginSuffix}";

    public EnterpriseWebPage(Guid installationId, string displayName, EnterpriseAssets assets, string backLabel)
    {
        Title = displayName;
        var origin = Origin(installationId);
        var web = new WebView();
        var back = new Button { Text = backLabel, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(8, 4) };
        back.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        grid.Add(back, 0, 0);
        grid.Add(web, 0, 1);
        Content = grid;
        web.HandlerChanged += (_, _) =>
        {
#if ANDROID
            if (web.Handler?.PlatformView is Android.Webkit.WebView platform) EnterpriseWebView.Attach(platform, origin, assets);
#endif
        };
    }
}

#if ANDROID
internal static class EnterpriseWebView
{
    public static void Attach(Android.Webkit.WebView view, string origin, EnterpriseAssets assets)
    {
        var settings = view.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.MixedContentMode = Android.Webkit.MixedContentHandling.NeverAllow;
        settings.SetSupportMultipleWindows(false);
        settings.JavaScriptCanOpenWindowsAutomatically = false;

        view.SetWebViewClient(new EnterpriseWebViewClient(new Uri(origin).Host, assets));
        if (AndroidX.WebKit.WebViewFeature.IsFeatureSupported(AndroidX.WebKit.WebViewFeature.WebMessageListener))
        {
            // The bridge object is injected only into this enterprise's origin; the listener also checks main frame.
            AndroidX.WebKit.WebViewCompat.AddWebMessageListener(view, "csweetShell", new List<string> { origin }, new BridgeListener(origin, assets));
        }
        view.LoadUrl(origin + "/");
    }
}

/// <summary>Serves this enterprise's origin from Headquarters and blocks every other request.</summary>
internal sealed class EnterpriseWebViewClient(string host, EnterpriseAssets assets) : Android.Webkit.WebViewClient
{
    public override Android.Webkit.WebResourceResponse? ShouldInterceptRequest(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request)
    {
        var url = request?.Url;
        if (url is null || url.Scheme != "https" || !string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            Android.Util.Log.Info("CSweetShell", $"Blocked {request?.Method} {url}");
            return Response(EnterpriseAssets.Served.Empty(403));
        }
        if (request!.Method != "GET") return Response(EnterpriseAssets.Served.Empty(405));
        try
        {
            // Runs on a WebView worker thread, so waiting here does not block the UI.
            var query = url.EncodedQuery is { Length: > 0 } q ? "?" + q : null;
            return Response(assets.GetAsync(url.EncodedPath ?? "/", query, CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (Exception error)
        {
            Android.Util.Log.Warn("CSweetShell", $"Failed {url.EncodedPath}: {error.Message}");
            return Response(EnterpriseAssets.Served.Empty(502));
        }
    }

    public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request) =>
        request?.Url is not { } url || !string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase); // stay on this enterprise

    private static Android.Webkit.WebResourceResponse Response(EnterpriseAssets.Served served)
    {
        var (mime, charset) = Split(served.ContentType);
        var headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" };
        return new Android.Webkit.WebResourceResponse(mime, charset, served.Status, Reason(served.Status), headers, served.Body);
    }

    private static (string Mime, string? Charset) Split(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return ("application/octet-stream", null);
        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var charset = parts.Skip(1).FirstOrDefault(x => x.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"');
        if (charset is null && (parts[0].StartsWith("text/") || parts[0].EndsWith("json") || parts[0].EndsWith("javascript"))) charset = "utf-8";
        return (parts[0], charset);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Status"
    };
}

/// <summary>Answers the web app's API calls (shell-bridge.js) through the Headquarters session.</summary>
internal sealed class BridgeListener(string origin, EnterpriseAssets assets) : Java.Lang.Object, AndroidX.WebKit.WebViewCompat.IWebMessageListener
{
    private sealed record Request(int Id, string Method, string Path, string? ContentType, string? Body);

    public void OnPostMessage(Android.Webkit.WebView view, AndroidX.WebKit.WebMessageCompat message, Android.Net.Uri sourceOrigin,
        bool isMainFrame, AndroidX.WebKit.JavaScriptReplyProxy replyProxy)
    {
        if (!isMainFrame || !string.Equals(sourceOrigin.ToString()?.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)) return;
        Request? request;
        try { request = System.Text.Json.JsonSerializer.Deserialize<Request>(message.Data ?? "", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
        catch (System.Text.Json.JsonException) { return; }
        if (request is null) return;
        _ = Task.Run(async () =>
        {
            string reply;
            try
            {
                var response = await assets.ApiAsync(request.Method, request.Path, CancellationToken.None);
                reply = System.Text.Json.JsonSerializer.Serialize(new { id = request.Id, status = response.Status, contentType = response.ContentType, body = Convert.ToBase64String(response.Body) });
            }
            catch (Exception error)
            {
                Android.Util.Log.Warn("CSweetShell", $"Bridge {request.Method} {request.Path} failed: {error.Message}");
                reply = System.Text.Json.JsonSerializer.Serialize(new { id = request.Id, status = 503, contentType = (string?)null, body = (string?)null });
            }
            MainThread.BeginInvokeOnMainThread(() => replyProxy.PostMessage(reply));
        });
    }
}
#endif
