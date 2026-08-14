using System.Net;
using System.Text;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class SetupApiClientTests
{
    [Fact]
    public async Task ApproveExecutionNodeAsync_NonJsonServerError_ReportsControlPlaneFailure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("database failure", Encoding.UTF8, "text/plain")
        });
        var client = new SetupApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://csweet.test/")
        });

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.ApproveExecutionNodeAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("server logs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
