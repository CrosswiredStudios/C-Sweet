using System.Net;
using System.Text;
using CSweet.AgentBroker;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentHostBrokerOperationHandlerTests
{
    [Theory]
    [InlineData("GET", "info/refs?service=git-upload-pack")]
    [InlineData("GET", "info/refs?service=git-receive-pack")]
    [InlineData("POST", "git-upload-pack")]
    [InlineData("POST", "git-receive-pack")]
    public async Task GitPurposeForwardsBinaryBodiesAndProtocolContentTypes(string method, string suffix)
    {
        byte[] body = [0, 255, 1, 13, 10];
        var path = $"/git/workspaces/{Guid.NewGuid():D}/1/{suffix}";
        var transport = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(path, request.RequestUri!.PathAndQuery);
            Assert.Equal(method, request.Method.Method);
            Assert.Equal(body, request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            Assert.Equal("Bearer session-token", request.Headers.Authorization!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new("application/x-git-receive-pack-result");
            return response;
        });
        using var client = new HttpClient(transport) { BaseAddress = new("http://agenthost/") };
        var handler = new AgentHostBrokerOperationHandler(new StubHttpClientFactory(client), new(),
            NullLogger<AgentHostBrokerOperationHandler>.Instance);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            "git.runtime", method, path, new Dictionary<string, string> { ["Authorization"] = "Bearer session-token",
                ["Content-Type"] = "application/x-git-receive-pack-request" }, body), default);
        Assert.Equal(body, result.Body.ToArray());
        Assert.Equal("application/x-git-receive-pack-result", result.Headers["Content-Type"]);
    }

    [Theory]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "https://example.com/git/workspaces/anything")]
    [InlineData("POST", "/git/workspaces/../mcp")]
    [InlineData("POST", "/git/workspaces/%2e%2e/mcp")]
    [InlineData("GET", "/git/workspaces/00000000-0000-0000-0000-000000000000/1/info/refs?service=git-upload-pack&extra=x")]
    [InlineData("PUT", "/git/workspaces/00000000-0000-0000-0000-000000000000/1/git-receive-pack")]
    public async Task GitPurposeCannotForwardOutsideSmartHttpRoutes(string method, string path)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => throw new Exception("Must not forward")));
        var handler = new AgentHostBrokerOperationHandler(new StubHttpClientFactory(client), new(),
            NullLogger<AgentHostBrokerOperationHandler>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid().ToString("N"), "git.runtime", method, path, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty), default));
    }

    [Fact]
    public async Task McpPurposeCannotBeUsedToReachGitRoutes()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => throw new Exception("Must not forward")));
        var handler = new AgentHostBrokerOperationHandler(new StubHttpClientFactory(client), new(),
            NullLogger<AgentHostBrokerOperationHandler>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid().ToString("N"), "mcp.runtime", "GET", $"/git/workspaces/{Guid.NewGuid():D}/1/info/refs?service=git-upload-pack",
            new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty), default));
    }

    [Fact]
    public async Task WorkClaimSurvivesBeyondShortControlTimeout()
    {
        using var client = new HttpClient(new DelayedClaimHandler()) { BaseAddress = new Uri("http://agenthost/") };
        var handler = new AgentHostBrokerOperationHandler(new StubHttpClientFactory(client),
            new AgentHostBrokerOptions { ControlRequestTimeoutSeconds = 1, TimeoutSeconds = 5 },
            NullLogger<AgentHostBrokerOperationHandler>.Instance);
        var result = await handler.HandleAsync(new BrokerOperationContext(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid().ToString("N"), "mcp.runtime", "POST", "/mcp", new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("{\"method\":\"csweet/work/claim\",\"params\":{\"waitSeconds\":25}}")), CancellationToken.None);
        Assert.Equal(200, result.StatusCode);
    }

    private sealed class DelayedClaimHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":null}") };
        }
    }

    [Fact]
    public void Options_DefaultTimeoutCoversAgentHostLlmRequests()
    {
        var options = new AgentHostBrokerOptions();

        Assert.True(options.TimeoutSeconds > 120);
        Assert.True(options.ControlRequestTimeoutSeconds < 30);
    }

    [Theory]
    [InlineData("initialize", 10)]
    [InlineData("ping", 10)]
    [InlineData("csweet/work/claim", 180)]
    [InlineData("tools/call", 180)]
    public void ResolveTimeoutSeconds_LeavesReconnectHeadroomForControlRequests(
        string method,
        int expectedSeconds)
    {
        var client = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("http://agenthost/")
        };
        var handler = new AgentHostBrokerOperationHandler(
            new StubHttpClientFactory(client),
            new AgentHostBrokerOptions { BaseUrl = "http://agenthost/" },
            NullLogger<AgentHostBrokerOperationHandler>.Instance);

        var body = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\"}}");

        Assert.Equal(expectedSeconds, handler.ResolveTimeoutSeconds(body));
    }

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

    [Fact]
    public void DescribeErrorResponse_ReturnsOnlyTheBoundedSingleLineJsonRpcReason()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"mentions must be an array.\\nRetry after correcting the request.\"}}");

        var result = AgentHostBrokerOperationHandler.DescribeErrorResponse(body);

        Assert.Equal("mentions must be an array. Retry after correcting the request.", result);
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
