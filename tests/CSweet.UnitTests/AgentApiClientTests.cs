using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class AgentApiClientTests
{
    [Fact]
    public async Task RetryDefinitionBuildAsync_UsesTheDefinitionRetryRoute()
    {
        var definitionId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var response = new AgentDefinitionResponse(
            definitionId, Guid.NewGuid(), "com.example.agent", "Example", "1.0.0", "Example",
            new string('a', 40), "Building", false, "OnDemand", 3600, "Skip", 600, 512, 50,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new AgentBuildSummaryResponse(Guid.NewGuid(), "Queued", 2, DateTimeOffset.UtcNow,
                null, null, false, null));
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
        });
        var client = new AgentApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://csweet.test/") });

        var result = await client.RetryDefinitionBuildAsync(definitionId);

        Assert.Equal(HttpMethod.Post, captured?.Method);
        Assert.Equal($"/api/agents/definitions/{definitionId}/retry-build", captured?.RequestUri?.AbsolutePath);
        Assert.Equal(definitionId, result.Id);
        Assert.Equal("Queued", result.Build?.Status);
    }

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
