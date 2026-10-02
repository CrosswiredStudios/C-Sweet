using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Services;

/// <summary>
/// Shared rules for sending a signed-out user to the login page. The UI fails closed: when the
/// session cannot be confirmed (signed out, expired, or the API cannot be reached) every
/// non-account route redirects to <c>/login</c>. The API remains the authority; this only keeps
/// the authenticated shell from rendering for someone who is not signed in.
/// </summary>
public static class LoginRedirect
{
    public const string ReasonUnavailable = "unavailable";
    public const string ReasonExpired = "expired";

    private static readonly string[] PublicAccountRoutes =
    [
        "login", "confirm-email", "forgot-password", "reset-password", "recover-root"
    ];

    /// <summary>True for account routes that must stay reachable without a session.</summary>
    public static bool IsPublicAccountRoute(string? relativePath)
    {
        var path = PathOnly(relativePath);
        return PublicAccountRoutes.Any(route =>
            path.Equals(route, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds <c>/login</c> with an optional reason and the route to return to after sign-in.</summary>
    public static string LoginUri(string? returnRelativePath, string? reason = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(reason)) query.Add("reason=" + Uri.EscapeDataString(reason));
        var returnUrl = SafeReturnUrl("/" + (returnRelativePath ?? string.Empty).TrimStart('/'));
        if (returnUrl != "/") query.Add("returnUrl=" + Uri.EscapeDataString(returnUrl));
        return query.Count == 0 ? "/login" : "/login?" + string.Join('&', query);
    }

    public static string LoginUri(NavigationManager navigation, string? reason = null)
    {
        string relative;
        try { relative = navigation.ToBaseRelativePath(navigation.Uri); }
        catch (InvalidOperationException) { relative = string.Empty; }
        return LoginUri(relative, reason);
    }

    /// <summary>
    /// Accepts only an app-local path. Absolute, protocol-relative, backslash and account routes
    /// collapse to <c>/</c>, so a crafted link cannot bounce a fresh sign-in to another site or back
    /// into the login flow.
    /// </summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        var value = returnUrl.Trim();
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\') || value.Contains("://", StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            return "/";
        var path = value.TrimStart('/');
        if (IsPublicAccountRoute(path) || PathOnly(path).StartsWith("register", StringComparison.OrdinalIgnoreCase)) return "/";
        return value;
    }

    private static string PathOnly(string? relativePath)
    {
        var path = (relativePath ?? string.Empty).Trim().TrimStart('/');
        var end = path.IndexOfAny(['?', '#']);
        return end < 0 ? path : path[..end];
    }
}
