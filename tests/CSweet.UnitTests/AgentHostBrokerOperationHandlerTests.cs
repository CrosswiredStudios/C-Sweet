using System.Net;
using System.Text;
using CSweet.AgentBroker;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentHostBrokerOperationHandlerTests
{
    [Fact]
    public async Task HandleAsync_BuffersBodyAndRemovesHopByHopResponseHeaders()
    {
        HttpRequestMessage? captured = null;
        var transport = new StubHttpMessageHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"result\":{}}", Encoding.UTF8, "application/json")
            };
            response.Headers.TransferEncodingChunked = true;
            response.Headers.ConnectionClose = false;
            response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            return response;
        });
        var client = new HttpClient(transport) { BaseAddress = new Uri("http://agenthost/") };
        var handler = new AgentHostBrokerOperationHandler(
            new StubHttpClientFactory(client),
            new AgentHostBrokerOptions { BaseUrl = "http://agenthost/" },
            NullLogger<AgentHostBrokerOperationHandler>.Instance);

        var result = await handler.HandleAsync(
            new BrokerOperationContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                "mcp.runtime",
                "POST",
                "/mcp",
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Transfer-Encoding"] = "chunked",
                    ["Connection"] = "keep-alive"
                },
                Encoding.UTF8.GetBytes("{}")),
            CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"result\":{}}", Encoding.UTF8.GetString(result.Body.Span));
        Assert.DoesNotContain(result.Headers.Keys, key =>
            key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(captured);
        Assert.False(captured.Headers.Contains("Transfer-Encoding"));
        Assert.False(captured.Headers.Contains("Connection"));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
