using System.Net;
using CSweet.Contracts.Auth;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class LoginRedirectTests
{
    [Theory]
    [InlineData("login", true)]
    [InlineData("/login?reason=expired", true)]
    [InlineData("reset-password/abc", true)]
    [InlineData("recover-root", true)]
    [InlineData("", false)]
    [InlineData("projects", false)]
    [InlineData("loginx", false)]
    [InlineData("register", false)]
    public void RecognizesOnlyAccountRoutesAsPublic(string path, bool expected) =>
        Assert.Equal(expected, LoginRedirect.IsPublicAccountRoute(path));

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/projects/42?tab=board", "/projects/42?tab=board")]
    [InlineData("projects", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("/redirect?to=https://evil.example", "/")]
    [InlineData("/login?returnUrl=/x", "/")]
    [InlineData("/register", "/")]
    public void ReturnUrlStaysInsideTheApp(string? value, string expected) =>
        Assert.Equal(expected, LoginRedirect.SafeReturnUrl(value));

    [Fact]
    public void LoginUriCarriesReasonAndReturnRoute()
    {
        Assert.Equal("/login", LoginRedirect.LoginUri(""));
        Assert.Equal("/login?reason=unavailable", LoginRedirect.LoginUri("", LoginRedirect.ReasonUnavailable));
        Assert.Equal("/login?reason=expired&returnUrl=%2Fwork-boards%3Fid%3D7",
            LoginRedirect.LoginUri("work-boards?id=7", LoginRedirect.ReasonExpired));
        Assert.Equal("/login", LoginRedirect.LoginUri("login?returnUrl=/x"));
    }

    [Fact]
    public void ProtectedEndpoint401EndsTheSessionButAuthEndpointsDoNot()
    {
        using var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        using var protectedRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/businesses");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/login");

        Assert.True(AuthSessionStore.EndsSession(protectedRequest, unauthorized));
        Assert.False(AuthSessionStore.EndsSession(protectedRequest, forbidden));
        Assert.False(AuthSessionStore.EndsSession(loginRequest, unauthorized));
    }

    [Fact]
    public async Task HandlerClearsTheSessionAndNotifiesOn401()
    {
        var session = new AuthSessionStore();
        session.Set(new AuthStatusResponse(false, true, "ceo@example.com", true, false, "token"));
        var ended = 0;
        session.SessionEnded += () => ended++;
        using var handler = new CookieAndAntiforgeryHandler(session)
        {
            InnerHandler = new StatusHandler(HttpStatusCode.Unauthorized)
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var response = await http.GetAsync("api/businesses");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(session.Current);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void RouterFailsClosedWhenTheSessionCannotBeConfirmed()
    {
        var routes = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "CSweet.UI", "Routes.razor"));
        Assert.Contains("LoginRedirect.LoginUri(path, LoginRedirect.ReasonUnavailable)", routes, StringComparison.Ordinal);
        Assert.Contains("Session.SessionEnded += OnSessionEnded", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep rendering the current route if the API is unavailable", routes, StringComparison.Ordinal);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
