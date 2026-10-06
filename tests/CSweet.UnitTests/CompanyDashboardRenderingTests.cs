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
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(html, "class=\"overview-widget").Count);
        Assert.Contains("No hiring approvals need your action", html);
        Assert.Contains("No pending hiring suggestions", html);
        Assert.Contains("Move Current Activity up", html);
        Assert.Contains("No active agent work", html);
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
    [Fact]
    public async Task HiringGroupsOnlyActionableApprovalsAndShowsTheBacklogWithoutChatActions()
    {
        var plan = Approval("Plan approval", ApprovalDashboardKinds.ResourceChange);
        var hire = Approval("Hire approval", ApprovalDashboardKinds.HiringWorkflow);
        var suggestions = new[]
        {
            Recommendation("Lower priority", 50), Recommendation("<script>Developer</script>", 1) with
            { Headcount = 3, FulfilledHeadcount = 1, RemainingHeadcount = 2, RequestingTeam = "Product team", SuggestedBy = "Chief" },
            Recommendation("Withdrawn suggestion", 1) with { Status = "Cancelled" },
            Recommendation("Fulfilled suggestion", 1) with { RemainingHeadcount = 0 }
        };
        var html = await Render(false, approvals: [plan, hire, Approval("Other approval", ApprovalDashboardKinds.AgentAction),
            Approval("Not yours", ApprovalDashboardKinds.ResourceChange) with { CanDecide = false },
            Approval("Old plan", ApprovalDashboardKinds.ResourceChange) with { Status = "Approved" }], recommendations: suggestions);
        var hiring = html[html.IndexOf("aria-label=\"Hiring\"", StringComparison.Ordinal)..];
        Assert.Contains("Plan approval", hiring);
        Assert.Contains("Hire approval", hiring);
        Assert.Contains($"approvalId={plan.Id:D}", hiring);
        Assert.Contains("recommendationId=", hiring);
        Assert.Contains("2 of 3 positions remaining", hiring);
        Assert.Contains("Product team", hiring);
        Assert.Contains("Chief", hiring);
        Assert.Contains("&lt;script&gt;Developer&lt;/script&gt;", hiring);
        Assert.DoesNotContain("<script>", hiring);
        Assert.DoesNotContain("Not yours", html);
        Assert.DoesNotContain("Old plan", html);
        Assert.DoesNotContain("Withdrawn suggestion", html);
        Assert.DoesNotContain("Fulfilled suggestion", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, ">Plan approval<").Cast<System.Text.RegularExpressions.Match>());
        Assert.True(hiring.IndexOf("Developer", StringComparison.Ordinal) < hiring.IndexOf("Lower priority", StringComparison.Ordinal));
        var ceo = html[html.IndexOf("aria-label=\"CEO approvals\"", StringComparison.Ordinal)..html.IndexOf("aria-label=\"Finances\"", StringComparison.Ordinal)];
        Assert.Contains("Other approval", ceo);
        Assert.Contains("1 pending", ceo);
        Assert.DoesNotContain("Plan approval", ceo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiringGroupFailuresDoNotHideTheOtherGroup(bool failApprovals)
    {
        var html = await Render(false, approvals: [Approval("Review plan", ApprovalDashboardKinds.ResourceChange)],
            recommendations: [Recommendation("Suggested developer", 1)], failApprovals: failApprovals, failHiring: !failApprovals);
        Assert.Contains("could not be loaded", html);
        Assert.Contains(failApprovals ? "Suggested developer" : "Review plan", html);
        Assert.DoesNotContain(failApprovals ? "No hiring approvals" : "No pending hiring suggestions", html);
    }

    [Fact]
    public async Task HiringPreviewLimitsEachGroupAndOffersIndependentExpansion()
    {
        var approvals = Enumerable.Range(0, 6).Select(i => Approval($"Plan {i}", ApprovalDashboardKinds.ResourceChange)).ToArray();
        var recommendations = Enumerable.Range(0, 6).Select(i => Recommendation($"Suggestion {i}", i + 1)).ToArray();
        var html = await Render(false, approvals: approvals, recommendations: recommendations);
        Assert.Contains("Show all approvals (6)", html);
        Assert.Contains("Show all suggestions (6)", html);
        Assert.DoesNotContain(">Plan 5<", html);
        Assert.DoesNotContain(">Suggestion 5<", html);
    }

    private static ApprovalDashboardItemResponse Approval(string title, string kind) =>
        new(Guid.NewGuid(), kind, title, "Review the proposed team", "Pending", "Producer", "Manager",
            DateTimeOffset.UtcNow, null, "/approvals", true) { RequestingTeam = "Product team" };
    private static HiringRecommendationResponse Recommendation(string title, int priority)
    {
        var id = Guid.NewGuid();
        return new(id, null, title, "Build the product", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Priority = priority, RemainingHeadcount = 1, HiringUrl = $"/marketplace?role=developer&recommendationId={id:D}" };
    }

    private static async Task<string> Render(bool populated, bool failReports = false,
        IReadOnlyList<ApprovalDashboardItemResponse>? approvals = null, IReadOnlyList<HiringRecommendationResponse>? recommendations = null,
        bool failApprovals = false, bool failHiring = false)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new Handler(populated, failReports, approvals, recommendations, failApprovals, failHiring)) { BaseAddress = new Uri("http://localhost") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CommandCenter>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CommandCenter.OrganizationId)] = Guid.NewGuid() }));
            return component.ToHtmlString();
        });
    }
    private sealed class Handler(bool populated, bool failReports,
        IReadOnlyList<ApprovalDashboardItemResponse>? approvals, IReadOnlyList<HiringRecommendationResponse>? recommendations,
        bool failApprovals, bool failHiring) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/dashboard/layout")) result = new DashboardLayoutRequest(["legal", "approvals", "finance", "projects"]);
            else if (path.EndsWith("/dashboard/hiring"))
            {
                if (failHiring) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = new HiringBacklogResponse(recommendations ?? []);
            }
            else if (path.EndsWith("/dashboard/activity")) result = new CurrentActivityPage(DateTimeOffset.UtcNow, 0, [], null);
            else if (path.EndsWith("/dashboard"))
            {
                if (failReports) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = new CompanyDashboardResponse(
                    new(populated ? [new(Guid.NewGuid(), "Finance Agent")] : [], populated ? new(new(new(2025, 1, 5), "USD", 0, null, 100, null), Guid.NewGuid(), "Finance Agent", DateTimeOffset.UtcNow) : null),
                    new(populated ? [new(Guid.NewGuid(), "Legal Agent")] : [], null));
            }
            else if (path.EndsWith("/questions/pending")) result = Array.Empty<CSweet.Contracts.Communications.PendingAgentQuestionResponse>();
            else if (path.EndsWith("/approvals"))
            {
                if (failApprovals) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = new ApprovalDashboardResponse(Guid.NewGuid(), approvals?.Count ?? 0, approvals ?? []);
            }
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
