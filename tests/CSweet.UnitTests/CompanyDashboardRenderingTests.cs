using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
namespace CSweet.UnitTests;

public sealed class CompanyDashboardRenderingTests
{
    [Fact]
    public async Task FirstRunRendersFullRowsAndHiringActionsInSavedOrder()
    {
        var html = await Render(false);
        Assert.Contains("Hire a finance agent", html);
        Assert.Contains("Hire a legal agent", html);
        Assert.Contains("No approvals need your action", html);
        Assert.Contains("No projects yet", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "class=\"overview-widget").Count);
        Assert.Contains("Move Pending Decisions up", html);
        Assert.Contains("No decisions need your response", html);
        Assert.True(html.IndexOf("aria-label=\"Legal\"", StringComparison.Ordinal) < html.IndexOf("aria-label=\"CEO approvals\"", StringComparison.Ordinal));
        Assert.Contains("Move Legal down", html);
        Assert.Contains("Briefing settings", html);
    }
    [Fact]
    public async Task PopulatedReportsKeepUnknownAmountsUnknownAndEncodeLeadUpdates()
    {
        var html = await Render(true);
        Assert.DoesNotContain("Hire a finance agent", html);
        Assert.Contains("Not reported", html);
        Assert.Contains("No report for this month yet", html);
        Assert.Contains("Awaiting first update", html);
        Assert.Contains("Contact Legal Agent", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Finance Agent", html);
    }
    [Fact]
    public async Task FailedReportLoadDoesNotPretendAnAgentNeedsHiring()
    {
        var html = await Render(false, true);
        Assert.Contains("could not be loaded", html);
        Assert.DoesNotContain("Hire a finance agent", html);
        Assert.Contains("No approvals need your action", html);
    }
    private static async Task<string> Render(bool populated, bool failReports = false)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new Handler(populated, failReports)) { BaseAddress = new Uri("http://localhost") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CommandCenter>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CommandCenter.OrganizationId)] = Guid.NewGuid() }));
            return component.ToHtmlString();
        });
    }
    private sealed class Handler(bool populated, bool failReports) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/dashboard/layout")) result = new DashboardLayoutRequest(["legal", "approvals", "finance", "projects"]);
            else if (path.EndsWith("/dashboard"))
            {
                if (failReports) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = new CompanyDashboardResponse(
                    new(populated ? [new(Guid.NewGuid(), "Finance Agent")] : [], populated ? new(new(new(2025, 1, 5), "USD", 0, null, 100, null), Guid.NewGuid(), "Finance Agent", DateTimeOffset.UtcNow) : null),
                    new(populated ? [new(Guid.NewGuid(), "Legal Agent")] : [], null));
            }
            else if (path.EndsWith("/questions/pending")) result = Array.Empty<CSweet.Contracts.Communications.PendingAgentQuestionResponse>();
            else if (path.EndsWith("/approvals")) result = new ApprovalDashboardResponse(Guid.NewGuid(), 0, []);
            else if (path.EndsWith("/inspection"))
            {
                var id = Guid.NewGuid();
                result = new ProjectPortfolioResponse(DateTimeOffset.UtcNow, populated ? 1 : 0, populated ? 1 : 0, populated ?
                    [new(id, "Portal", "Launch", "Active", "Delivery", null, null, Guid.NewGuid(), null, null, null, 1, DateTimeOffset.UtcNow, 1, 1, 2, 0, 0, null)
                    { LatestLeadUpdate = new(new(id, "<script>untrusted</script>"), Guid.NewGuid(), "Lead", DateTimeOffset.UtcNow) }] : []);
            }
            else result = new { name = "Example company" };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
