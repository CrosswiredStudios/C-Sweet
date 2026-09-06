using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.SourceControl;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class SourceControlSettingsStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHubFailureDoesNotHideInternalStorage(bool malformed)
    {
        using var http = Client(path => path.Contains("platform-setup")
            ? malformed ? new(HttpStatusCode.OK) { Content = new StringContent("invalid json") } : new(HttpStatusCode.ServiceUnavailable)
            : Storage());
        var result = await SourceControlSettingsState.LoadAsync(http);
        Assert.True(result.Storage!.Ready); Assert.Null(result.StorageError); Assert.Null(result.Setup); Assert.NotNull(result.SetupError); Assert.False(result.AccessDenied);
    }
    [Fact]
    public async Task StorageFailureDoesNotHideOptionalGitHubSetup()
    {
        using var http = Client(path => path.Contains("platform-setup") ? Setup() : new(HttpStatusCode.ServiceUnavailable));
        var result = await SourceControlSettingsState.LoadAsync(http);
        Assert.NotNull(result.Setup); Assert.Null(result.SetupError); Assert.Null(result.Storage); Assert.NotNull(result.StorageError);
    }
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task DeniedAccessReturnsNoPartialAdministrativeData(HttpStatusCode status)
    {
        using var http = Client(path => path.Contains("platform-setup") ? new(status) : Storage());
        var result = await SourceControlSettingsState.LoadAsync(http);
        Assert.True(result.AccessDenied); Assert.Null(result.Storage); Assert.Null(result.Setup);
    }
    [Fact]
    public async Task CancellationIsNotReportedAsServiceFailure()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var http = Client(_ => Storage());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceControlSettingsState.LoadAsync(http, source.Token));
    }
    private static HttpResponseMessage Storage() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new InternalGitStorageStatus(true, "local-store", "temp", "filesystem", "lfs", "filesystem", "backups", null)) };
    private static HttpResponseMessage Setup() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new PlatformSourceControlSetupResponse(new(false, false, null), null)) };
    private static HttpClient Client(Func<string, HttpResponseMessage> respond) => new(new Handler(respond)) { BaseAddress = new("http://localhost/") };
    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request.RequestUri!.AbsolutePath)); }
    }
}
