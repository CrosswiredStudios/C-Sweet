using CSweet.Contracts.Auth;

namespace CSweet.UI.Services;

public sealed class AuthSessionStore
{
    public AuthStatusResponse? Current { get; private set; }
    public string? AntiforgeryToken => Current?.AntiforgeryToken;

    /// <summary>Raised when an API call reports that the session is no longer authenticated.</summary>
    public event Action? SessionEnded;

    public void Set(AuthStatusResponse status) => Current = status;
    public void Clear() => Current = null;

    /// <summary>
    /// Records a 401 from a protected API endpoint (expired cookie, server-side sign-out) and
    /// notifies the router so the user is sent to sign in instead of seeing a broken shell.
    /// </summary>
    public void ReportUnauthenticated()
    {
        Current = null;
        SessionEnded?.Invoke();
    }

    /// <summary>
    /// 401 means "not signed in" on every protected endpoint (the API maps cookie challenges to 401
    /// and access-denied to 403). Authentication endpoints answer their own 401s and are excluded.
    /// </summary>
    public static bool EndsSession(HttpRequestMessage request, HttpResponseMessage response) =>
        response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
        !(request.RequestUri?.AbsolutePath.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase) ?? false);
}
