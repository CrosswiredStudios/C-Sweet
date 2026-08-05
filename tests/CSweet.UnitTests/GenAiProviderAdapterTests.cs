using System.Net;
using System.Text;
using CSweet.Infrastructure.GenAi;
using CSweet.Domain.Setup;

namespace CSweet.UnitTests;

public sealed class GenAiProviderAdapterTests
{
    [Fact]
    public async Task ComfyUiTest_AcceptsObjectInfoPayload()
    {
        var adapter = CreateAdapter((_, _) => Task.FromResult(JsonResponse(
            """{"KSampler":{"input":{"required":{}}}}""")));

        var result = await adapter.TestAsync(Profile(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"ok\"}")]
    [InlineData("not-json")]
    public async Task ComfyUiTest_RejectsNonComfyUiPayload(string payload)
    {
        var adapter = CreateAdapter((_, _) => Task.FromResult(JsonResponse(payload)));

        var result = await adapter.TestAsync(Profile(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("provider_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task ComfyUiTest_ReportsTimedOutEndpointAsUnreachable()
    {
        var adapter = CreateAdapter(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var result = await adapter.TestAsync(Profile(), null, timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("provider_unreachable", result.ErrorCode);
    }

    private static ComfyUiLocalGenAiProviderAdapter CreateAdapter(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHttpClientFactory(new HttpClient(new StubHandler(send))));

    private static GenAiProviderProfile Profile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "ComfyUI Local",
        ProviderType = GenAiProviderType.ComfyUiLocal,
        BaseUrl = "http://localhost:8188",
        IsEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
