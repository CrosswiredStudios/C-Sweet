using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Security;
using CSweet.UI.Components.Employees;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class EmployeeTimelineRenderingTests
{
    [Fact]
    public async Task TimelineRendersReceivedDirectionExactEvidenceAndSharedAuditLink()
    {
        var html = await RenderAsync(false);
        Assert.Contains("Message received", html);
        Assert.Contains("Activity from the audit ledger", html);
        Assert.Contains("audit-inspector", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("/security?eventId=", html);
        Assert.Contains("timeline-mobile-detail", html);
        if (Environment.GetEnvironmentVariable("CSWEET_TIMELINE_PREVIEW") is string path)
            await File.WriteAllTextAsync(path, html);
    }
    [Fact]
    public async Task RevokedAccessRendersErrorWithoutOldEvidence()
    {
        var html = await RenderAsync(true);
        Assert.Contains("no longer have access", html);
        Assert.DoesNotContain("audit-inspector", html);
    }
    [Fact]
    public async Task ModelResponseRendersCombinedEvidenceAndRawSourcesWithoutChunkRows()
    {
        var html = await RenderAsync(false, true);
        Assert.Contains("combined response", html); Assert.Contains("Model response", html);
        Assert.Contains("&lt;script&gt;assembled response&lt;/script&gt;", html);
        Assert.Contains("Inspect individual chunks", html); Assert.DoesNotContain("Model Response Chunk", html);
        if (Environment.GetEnvironmentVariable("CSWEET_MODEL_RESPONSE_PREVIEW") is string path) await File.WriteAllTextAsync(path, html);
    }
    private static async Task<string> RenderAsync(bool forbidden, bool model = false)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton(new HttpClient(new Handler(forbidden, model)) { BaseAddress = new Uri("http://localhost") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var theme = await renderer.RenderComponentAsync<MudThemeProvider>();
            var component = await renderer.RenderComponentAsync<EmployeeTimeline>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(EmployeeTimeline.OrganizationId)] = Guid.NewGuid(), [nameof(EmployeeTimeline.EmployeeId)] = Guid.NewGuid(), [nameof(EmployeeTimeline.CanViewAuditLog)] = true }));
            return theme.ToHtmlString() + component.ToHtmlString();
        });
    }
    private sealed class Handler(bool forbidden, bool model) : HttpMessageHandler
    {
        private readonly Guid eventId = Guid.NewGuid();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (forbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var time = DateTimeOffset.UtcNow;
            object result = request.RequestUri!.AbsolutePath.EndsWith("/timeline")
                ? new SecurityEventPageResponse([
                    new(eventId, 7, time, "Communication", "Inbound", "Delivered", "communication.message.sent", "Human", "Alex Morgan", "Chief of Staff", null, "turn-7e92", "Verified", "ConversationMessage", Guid.NewGuid(), "Recipient"),
                    new(Guid.NewGuid(), 6, time.AddSeconds(-1), "AgentWork", "Internal", "Completed", "agent.work.completed", "Agent", "Chief of Staff", null, null, "turn-7e92", "Verified", "AgentWorkItem", Guid.NewGuid()),
                    new(Guid.NewGuid(), 5, time.AddSeconds(-3), "AgentCapability", "Internal", "Failed", "agent.capability.executed", "Agent", "Chief of Staff", "work.items.update", null, "turn-7e92", "Verified", "Capability", Guid.NewGuid()),
                    new(Guid.NewGuid(), 4, time.AddSeconds(-5), "Model", "Internal", "Completed", "model.call.completed", "Agent", "Chief of Staff", null, null, "turn-7e92", "Verified", "AgentRunLog", Guid.NewGuid()),
                    new(Guid.NewGuid(), 3, time.AddSeconds(-8), "AgentWork", "Internal", "Running", "agent.work.attempt.started", "Agent", "Chief of Staff", null, null, "turn-7e92", "Verified", "AgentWorkAttempt", Guid.NewGuid())], null)
                : new SecurityEventDetailResponse { Id = eventId, EventType = "communication.message.sent", Outcome = "Delivered", OccurredAt = time,
                    IntegrityStatus = "Verified", PayloadAvailability = "Available", PayloadContent = "<script>untrusted evidence</script>", ActorDisplayName = "Alex Morgan", CorrelationId = "turn-7e92" };
            if (model)
                result = request.RequestUri.AbsolutePath.EndsWith("/timeline")
                    ? new SecurityEventPageResponse([new(eventId, 7, time, "Model", "Internal", "Completed", "model.response", "Agent", "Daniel", null, null, "turn", "Verified", "AgentRunLog", Guid.NewGuid(), ModelResponseChunkCount: 2941)], null)
                    : new SecurityEventDetailResponse { Id = eventId, EventType = "model.call.completed", Outcome = "Completed", OccurredAt = time, IntegrityStatus = "Verified",
                        ModelResponse = new(2941, 2941, 2900, "<script>assembled response</script>", [], 10, 20, "Verified", false, [new(eventId, time, "model.response.chunk")]) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
