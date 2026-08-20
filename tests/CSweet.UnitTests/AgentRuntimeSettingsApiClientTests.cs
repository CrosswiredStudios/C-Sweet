using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Setup;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeSettingsApiClientTests
{
    [Fact]
    public async Task RecoverAsync_PostsTheManualRuntimeRecoveryRoute()
    {
        HttpRequestMessage? captured = null;
        var expected = new AgentRuntimeSettingsActionResponse(
            true,
            "Runtime reconciliation completed and advanced 2 runtimes.",
            null);
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(expected),
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new AgentRuntimeSettingsApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://csweet.test/")
        });

        var result = await client.RecoverAsync();

        Assert.Equal(HttpMethod.Post, captured?.Method);
        Assert.Equal("/api/agent-runtime/settings/recover", captured?.RequestUri?.AbsolutePath);
        Assert.True(result.Succeeded);
        Assert.Equal(expected.Message, result.Message);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
