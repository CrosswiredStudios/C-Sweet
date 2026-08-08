using System.Net;
using System.Text;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class AgentApiClientTests
{
    [Fact]
    public async Task GetConfigurationAsync_RuntimeStarting_DoesNotTreatReadinessAsSchema()
    {
        var installationId = Guid.NewGuid();
        var client = CreateClient(
            HttpStatusCode.Accepted,
            $$"""
              {
                "installationId":"{{installationId}}",
                "runtimeInstanceId":null,
                "stage":"StartingWorkload",
                "runtimeStatus":"Starting",
                "reason":"The isolated workload is booting.",
                "queuedAt":null,
                "startedAt":null,
                "mcpSessionEstablishedAt":null,
                "isReady":false,
                "isTerminal":false
              }
              """);

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.GetConfigurationAsync(installationId.ToString()));

        Assert.Equal(HttpStatusCode.Accepted, exception.StatusCode);
        Assert.Contains("still starting", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isolated workload is booting", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NullReferenceException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_RuntimeStarting_ReportsCurrentStage()
    {
        var installationId = Guid.NewGuid();
        var client = CreateClient(
            HttpStatusCode.Accepted,
            $$"""
              {
                "installationId":"{{installationId}}",
                "runtimeInstanceId":null,
                "stage":"WaitingForMcpSession",
                "runtimeStatus":"WaitingForMcpSession",
                "reason":null,
                "queuedAt":null,
                "startedAt":null,
                "mcpSessionEstablishedAt":null,
                "isReady":false,
                "isTerminal":false
              }
              """);

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.UpdateConfigurationAsync(
                installationId.ToString(),
                new CSweet.Contracts.Agents.UpdateAgentConfigurationRequest(
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        Assert.Equal(HttpStatusCode.Accepted, exception.StatusCode);
        Assert.Contains("WaitingForMcpSession", exception.Message, StringComparison.Ordinal);
    }

    private static AgentApiClient CreateClient(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return new AgentApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://csweet.test/")
        });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
