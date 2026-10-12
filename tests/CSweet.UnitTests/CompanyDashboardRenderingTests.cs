using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Compute;
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
    public async Task FirstRunMergesActionQueuesAndPreservesSavedWidgetOrder()
    {
        var html = await Render(false);
        Assert.Contains("Hire a finance agent", html);
        Assert.Contains("Hire a legal agent", html);
        Assert.Contains("Nothing needs your attention", html);
        Assert.Contains("No projects yet", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "class=\"overview-widget ").Count);
        Assert.Contains("Move Current Activity up", html);
        Assert.Contains("No active agent work", html);
        Assert.Contains("Move Needs your attention up", html);
        Assert.True(html.IndexOf("aria-label=\"Legal\"", StringComparison.Ordinal) < html.IndexOf("aria-label=\"Pending attention items\"", StringComparison.Ordinal));
        Assert.Contains("Move Legal down", html);
        Assert.Contains("Briefing settings", html);
        Assert.DoesNotContain("role=\"tab\"", html);
        Assert.DoesNotContain("role=\"tabpanel\"", html);
        Assert.DoesNotContain("Overview appearance", html);
        Assert.Contains("company-overview aurora-page", html);
        if (Environment.GetEnvironmentVariable("CSWEET_OVERVIEW_PREVIEW") is string path) await File.WriteAllTextAsync(path, html);
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
        Assert.Contains("Nothing needs your attention", html);
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
        var hiring = html[html.IndexOf("aria-label=\"Pending attention items\"", StringComparison.Ordinal)..];
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
        Assert.Contains("Other approval", hiring);
        Assert.Contains("5 pending", hiring);
        Assert.Contains("attention-request approval", hiring);
        Assert.Contains("attention-request hiring", hiring);
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
    public async Task HiringCandidateShowsPortraitPublisherCostAndRationaleWithinTheCandidatePanel()
    {
        var recommendation = Recommendation("Creative Director", 1) with
        {
            SuggestedBy = "Evelyn Brooks",
            SelectedCatalogAgentJson = """{"agentReference":"catalog:creative-director","name":"Naomi Chen","publisher":"C-Sweet","price":null,"imageUrl":"https://example.com/naomi.jpg"}""",
            SelectionRationale = "Matches the approved role. <script>unsafe</script>"
        };
        var html = await Render(false, recommendations: [recommendation]);
        Assert.Contains("https://example.com/naomi.jpg", html);
        Assert.Contains("Naomi Chen", html);
        Assert.Contains("Cost unavailable", html);
        Assert.Contains("Hire Naomi Chen", html);
        Assert.Contains("Browse Candidates", html);
        Assert.Contains("Role scope", html);
        Assert.Contains("candidate-rationale", html);
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("align-actions-right", html);
        Assert.DoesNotContain("Interviewing agents", html);
        Assert.DoesNotContain("candidate-preparation", html);
    }

    [Theory]
    [InlineData(null, null, "Interviewing agents")]
    [InlineData(" ", " ", "Interviewing agents")]
    [InlineData(null, "Choose the candidate for this approved role.", "Candidate review")]
    [InlineData(null, "No eligible candidate is available under your publisher preference.", "Candidate review")]
    [InlineData(null, "This approved role requires a human.", "Candidate review")]
    [InlineData("broken json", null, "Recommendation unavailable")]
    [InlineData("null", null, "Recommendation unavailable")]
    [InlineData("{}", null, "Recommendation unavailable")]
    public async Task UnselectedOrInvalidCandidatesShowSelectionProgressAndKeepContextualBrowsing(
        string? candidateJson, string? rationale, string expectedStatus)
    {
        var recommendation = Recommendation("Creative Director", 1) with
        { SelectedCatalogAgentJson = candidateJson, SelectionRationale = rationale };
        var html = await Render(false, recommendations: [recommendation]);
        Assert.Contains(expectedStatus, html);
        Assert.Contains("candidate-preparation", html);
        Assert.Contains("role=\"status\"", html);
        if (!string.IsNullOrWhiteSpace(rationale)) Assert.Contains(rationale, html);
        if (expectedStatus != "Interviewing agents") Assert.DoesNotContain("Interviewing agents", html);
        Assert.Contains("Browse Candidates", html);
        Assert.Contains($"recommendationId={recommendation.Id}", html);
        Assert.DoesNotContain("Recommended agent", html);
        Assert.DoesNotContain("Hire Naomi", html);
    }

    [Fact]
    public async Task MixedQueueLimitsLongListsAndOffersExpansion()
    {
        var approvals = Enumerable.Range(0, 6).Select(i => Approval($"Plan {i}", ApprovalDashboardKinds.ResourceChange)).ToArray();
        var recommendations = Enumerable.Range(0, 6).Select(i => Recommendation($"Suggestion {i}", i + 1)).ToArray();
        var html = await Render(false, approvals: approvals, recommendations: recommendations);
        Assert.Contains("Show all items (12)", html);
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

    [Fact]
    public async Task ComputePermissionsJoinTheQueueWithInstanceContextAndExcludeResolvedOrUnavailableInstances()
    {
        var pending = ComputeResource();
        var html = await Render(false, compute: new(DateTimeOffset.UtcNow, null,
            [pending, ComputeResource() with { LocalLinkAllowed = true },
                ComputeResource() with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                ComputeResource() with { State = "Destroyed" }, ComputeResource() with { State = "Destroying" },
                ComputeResource() with { DesiredState = "Destroyed" }], false, true));
        Assert.Contains("1 pending", html);
        Assert.Contains("Allow a local test link", html);
        Assert.Contains("Victor Lin", html);
        Assert.Contains("Prismatic Break", html);
        Assert.Contains($"/compute#compute-access-{pending.Id:D}", html);
        Assert.DoesNotContain("Nothing needs your attention", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "attention-request compute").Cast<System.Text.RegularExpressions.Match>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ComputePermissionQueueIsEmptyForNonOwnersAndAlreadyAllowedAccess(bool canManage, bool allowed)
    {
        var html = await Render(false, compute: new(DateTimeOffset.UtcNow, null,
            [ComputeResource() with { LocalLinkAllowed = allowed }], false, canManage));
        Assert.Contains("Nothing needs your attention", html);
        Assert.DoesNotContain("Allow a local test link", html);
    }

    [Fact]
    public async Task ComputeReadFailureDoesNotHideOtherRequestsOrClaimTheQueueIsEmpty()
    {
        var html = await Render(false, failCompute: true, recommendations: [Recommendation("Suggested developer", 1)]);
        Assert.Contains("Suggested developer", html);
        Assert.Contains("could not be loaded", html);
        Assert.DoesNotContain("Nothing needs your attention", html);
        html = await Render(false, failCompute: true);
        Assert.DoesNotContain("Nothing needs your attention", html);
    }

    [Fact]
    public async Task LongComputeQueuesOfferTheSharedExpansionControl()
    {
        var html = await Render(false, compute: new(DateTimeOffset.UtcNow, null,
            Enumerable.Range(0, 6).Select(_ => ComputeResource()).ToArray(), false, true));
        Assert.Contains("Show all items (6)", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "attention-request compute").Count);
    }

    private static ComputeResourceStatus ComputeResource() => new(Guid.NewGuid(), "Victor Lin", "Prismatic Break", "Ready",
        "Running", "linux", 2, 2048, 20480, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null);

    [Fact]
    public async Task TruncatedComputeReadsDoNotClaimThereAreNoPendingRequests()
    {
        var html = await Render(false, compute: new(DateTimeOffset.UtcNow, null, [], true, true));
        Assert.Contains("100 most recent compute instances", html);
        Assert.DoesNotContain("Nothing needs your attention", html);
    }

    private static async Task<string> Render(bool populated, bool failReports = false,
        IReadOnlyList<ApprovalDashboardItemResponse>? approvals = null, IReadOnlyList<HiringRecommendationResponse>? recommendations = null,
        bool failApprovals = false, bool failHiring = false, ComputeDashboard? compute = null, bool failCompute = false)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new Handler(populated, failReports, approvals, recommendations, failApprovals, failHiring, compute, failCompute)) { BaseAddress = new Uri("http://localhost") });
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
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
        bool failApprovals, bool failHiring, ComputeDashboard? compute, bool failCompute) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/dashboard/layout")) result = new DashboardLayoutRequest(["legal", "approvals", "finance", "projects"]);
            else if (path.EndsWith("/compute"))
            {
                if (failCompute) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = compute ?? new ComputeDashboard(DateTimeOffset.UtcNow, null, [], false);
            }
            else if (path.EndsWith("/dashboard/hiring"))
            {
                if (failHiring) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                result = new HiringBacklogResponse(recommendations ?? []);
            }
            else if (path.EndsWith("/dashboard/activity")) result = new CurrentActivityPage(DateTimeOffset.UtcNow, 0, [], null);
            else if (path.EndsWith("/users") || path.EndsWith("/installations")) result = Array.Empty<object>();
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
