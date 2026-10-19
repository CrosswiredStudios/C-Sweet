using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.Projects;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class ProjectsWorkspaceTests
{
    [Theory]
    [InlineData("teams", "people")]
    [InlineData("governance", "decisions")]
    [InlineData("evidence", "delivery")]
    [InlineData("invalid", "overview")]
    public void ExistingSectionLinksRemainSupported(string oldTab, string current) => Assert.Equal(current, ProjectPresentation.Tab(oldTab));

    [Fact]
    public async Task PortfolioShowsAuthorizedNamesRealBlockersAndReadiness()
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId });
        Assert.Contains("Neon Bash", html);
        Assert.Contains("Matt", html);
        Assert.Contains("2 blocked", html);
        Assert.Contains("Not ready", html);
        Assert.DoesNotContain("v@context", html);
        Assert.DoesNotContain("Manager ID", html);
        Assert.DoesNotContain("Projects views", html);
        Assert.DoesNotContain("Personal boards", html);
        Assert.Contains("Read full brief", html);
        Assert.Contains($"/projects/{api.ProjectId}?tab=delivery", html);
        Assert.Contains($"/projects/{api.ProjectId}?tab=work", html);
        Assert.Contains($"/organizations/{api.OrganizationId}/projects?view=boards", html);
        Assert.Contains($"/employees/{api.ManagerId}", html);
        if (Environment.GetEnvironmentVariable("CSWEET_PROJECTS_PREVIEW") is string path) await File.WriteAllTextAsync(path, html);
        Assert.All(api.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData("overview", "Project brief")]
    [InlineData("work", "No project boards yet")]
    [InlineData("efficiency", "Work efficiency")]
    [InlineData("people", "Supervision")]
    [InlineData("documents", "Reviews")]
    [InlineData("communications", "No communications yet")]
    [InlineData("decisions", "Authority")]
    [InlineData("delivery", "Evaluations")]
    [InlineData("audit", "Audit timeline")]
    public async Task ProjectSectionsRenderTheirOwnContent(string tab, string expected)
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = tab });
        Assert.Contains(expected, html);
        Assert.Contains("Neon Bash", html);
        Assert.DoesNotContain("New project", html);
        Assert.Contains("aurora-page project-detail-workspace", html);
        Assert.DoesNotContain("Refresh delivery status", html);
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path, $"detail-{tab}.html"), html);
        Assert.All(api.Methods, method => Assert.Contains(method, tab == "delivery" ? new[] { HttpMethod.Get, HttpMethod.Post } : new[] { HttpMethod.Get }));
    }

    [Theory]
    [InlineData("boards", "All boards")]
    [InlineData("personal", "Personal boards")]
    public async Task DirectoriesDoNotRequireProjectInspectionAccess(string view, string expected)
    {
        var api = new Api();
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["View"] = view });
        Assert.Contains(expected, html);
        Assert.DoesNotContain(api.Paths, path => path.Contains("/inspection"));
        if (view == "personal") Assert.DoesNotContain(api.Paths, path => path.Contains("/work/boards"));
    }

    [Fact]
    public void MetadataAcceptsBothWireAndStoredPropertyCasing()
    {
        var resource = Resource() with { Metadata = JsonSerializer.SerializeToElement(new { outcome = "Ship a prototype" }) };
        Assert.Equal("Ship a prototype", ProjectPresentation.Metadata(resource, "Outcome"));
        Assert.Equal("Not set", ProjectPresentation.Metadata(resource, "TargetDate"));
    }

    private static ProjectInspectionResource Resource() => new(Guid.NewGuid(), "Workstream", "software-prototype.v1", "Neon Bash", "Active", 1, null, null, null, null, null, DateTimeOffset.UtcNow, "/projects", JsonSerializer.SerializeToElement(new { Outcome = "Ship a prototype", LifecycleStage = "Development" }));

    [Fact]
    public void FailedReadsRemainVisibleInProjectActivity()
    {
        var entry = new ProjectAuditEntry(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "artifact.read", "Work", "Failed", null, null, "Completed artifact.read.", "Agent", null, null, null, null, null, null, null, "hash");
        Assert.False(ProjectPresentation.IsRoutineRead(entry));
        Assert.True(ProjectPresentation.IsRoutineRead(entry with { Outcome = "Succeeded" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectEfficiencyNeverFallsBackToCompanyTotals(bool hasProjectUsage)
    {
        var api = new Api { HasProjectUsage = hasProjectUsage };
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = "efficiency" });
        Assert.DoesNotContain("999,999", html);
        Assert.DoesNotContain("Business overhead:", html);
        Assert.Contains(hasProjectUsage ? "How these numbers are measured" : "No usage recorded for this project yet", html);
        if (hasProjectUsage && Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path, "detail-efficiency-populated.html"), html);
    }

    [Fact]
    public async Task OverviewSummarizesLongBriefAndDoesNotShowRoutineReads()
    {
        var api = new Api { Populated = true };
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId });
        Assert.Contains("Read full brief", html);
        Assert.Contains("Creative direction approved", html);
        Assert.DoesNotContain("Completed work.item.read.", html);
        Assert.Contains($"/projects/{api.ProjectId}?tab=work", html);
        Assert.Contains("Inspect delivery", html);
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path, "detail-overview-populated.html"), html);
    }

    [Theory]
    [InlineData("people")]
    [InlineData("documents")]
    [InlineData("communications")]
    [InlineData("decisions")]
    [InlineData("audit")]
    public async Task ResourceTabsKeepUsefulLinksAndHideMissingRecordFields(string tab)
    {
        var api = new Api { Populated = true };
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = tab });
        Assert.DoesNotContain("Not provided", html);
        Assert.DoesNotContain("Completed work.item.read.", html);
        if (tab == "audit") Assert.Contains("Show routine reads", html);
        else { Assert.Contains("Needs attention first", html); Assert.Contains("Record details", html); Assert.Contains("href=\"/linked-record\"", html); }
        if (tab is "people" or "documents" or "decisions") Assert.Contains("aria-pressed=\"true\"", html);
        if (tab == "documents") Assert.True(html.IndexOf("Technical feasibility", StringComparison.Ordinal) < html.IndexOf("Creative brief", StringComparison.Ordinal));
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path, $"detail-{tab}-populated.html"), html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryRendersExactCandidateAndPartialPromotionWithoutStandaloneMergeColumns(bool partial)
    {
        await using var fixture = await HierarchicalDeliveryServiceTests.Fixture.Create(false, twoRepositories: true);
        await fixture.Activate(await fixture.Configure()); await fixture.CompleteTasks(); await fixture.Service.PulseAsync();
        if (partial)
        {
            for (var pass = 0; pass < 12; pass++)
            {
                var current = await fixture.Read();
                var review = current.Executions.FirstOrDefault(x => x.Status == "WaitingForHuman");
                if (review is not null) await fixture.Approve(review);
                else if (current.Executions.FirstOrDefault(x => x.Status == "WaitingForApproval") is { } acceptance)
                {
                    if (acceptance.Scope == "Release") fixture.Host.FailRepository = acceptance.Candidate!.Repositories.Last().RepositoryId;
                    await fixture.Accept(acceptance);
                }
                await fixture.Service.PulseAsync();
                if ((await fixture.Read()).Executions.Any(x => x.Status == "PartiallyPromoted")) break;
            }
        }
        var plan = await fixture.Read();
        var api = new Api { DeliveryPlans = [plan] };
        var html = await Render(api, new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = "delivery" });
        Assert.Contains("Releases", html); Assert.Contains("Reviewed/tested candidate", html);
        Assert.Contains(plan.Executions.Single(x => x.Scope == "Story").Candidate!.Repositories.First().CandidateCommitSha, html);
        if (partial)
        {
            Assert.Contains("PartiallyPromoted", html);
            Assert.Contains("Provider failed; retry unfinished repository", html);
            Assert.Contains(plan.Executions.Single(x => x.Scope == "Release").Promotions.Single(x => x.Status == "Completed").MergeCommitSha!, html);
        }
        else Assert.Contains("Awaiting validation", html);
        Assert.Contains("scope revision", html);
        Assert.DoesNotContain(">Merge<", html);
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path, $"detail-delivery-{partial}.html"), html);
    }

    [Fact]
    public async Task DeliverySeparatesCurrentBlockersFromHistoryAndKeepsEvidenceClosed()
    {
        var api = new Api();
        const string reason = "Compute validation failed or its result is unknown: outcome-unknown";
        var blocked = DeliveryExecution("Blocked", "build-readiness") with { BlockedReason = reason };
        var completed = DeliveryExecution("Completed", "manager-review") with { Scope = "Epic", ItemId = Guid.NewGuid() };
        var plan = DeliveryPlan(api, "Paused", [blocked, completed]) with
        {
            EpicItemIds = [completed.ItemId!.Value],
            Findings = [new(Guid.NewGuid(), blocked.Id, "candidate", reason, "Open", [], null),
                new(Guid.NewGuid(), blocked.Id, "old-candidate", "Old browser validation concern", "Superseded", [], null),
                new(Guid.NewGuid(), blocked.Id, "old-candidate", "Old build failure", "Resolved", [], "Fresh QA passed")]
        };
        api.DeliveryPlans = [plan];
        var html = await RenderDelivery(api);
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, "delivery-simplified.html"), html);
        var current = html[..html.IndexOf("class=\"release-history\"", StringComparison.Ordinal)];
        Assert.Contains(">Blocked</dd>", current);
        Assert.Contains("Gabriel Reyes", current);
        Assert.Contains("1 of 2 completed", current);
        Assert.Contains("then resume delivery", current);
        Assert.Matches("Current blockers <span[^>]*>1</span>", current);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(current, reason).Cast<System.Text.RegularExpressions.Match>());
        Assert.DoesNotContain("Old browser validation concern", current);
        Assert.DoesNotContain("Old build failure", current);
        Assert.Contains("Old browser validation concern", html);
        Assert.Contains("Fresh QA passed", html);
        Assert.Contains("Resolve blockers and recover", html);
        Assert.Contains("Recover unfinished operations", html);
        Assert.Contains("Resume delivery", html);
        Assert.Contains("Technical details and review evidence", html);
        Assert.Contains("Reviewed/tested candidate", html);
        Assert.DoesNotMatch("<details[^>]*\\sopen(?:[=\\s>])", html);
        Assert.Contains("Builds, previews, and delivery evidence", html);
    }

    [Theory]
    [InlineData("Active", "WaitingForChildren", "Waiting for scoped work", "Complete and accept the required work")]
    [InlineData("Active", "WaitingForEvidence", "Waiting for validation", "Wait for build or test results")]
    [InlineData("Active", "Running", "Checks in progress", "complete qa and regression testing")]
    [InlineData("Active", "WaitingForHuman", "Awaiting review", "Review the exact candidate")]
    [InlineData("Active", "WaitingForApproval", "Awaiting acceptance", "Accept the exact candidate")]
    [InlineData("Active", "PartiallyPromoted", "Partially integrated", "recover only the unfinished integrations")]
    [InlineData("Paused", "WaitingForChildren", "Paused", "resume delivery when appropriate")]
    [InlineData("Draft", "WaitingForChildren", "Scope awaiting activation", "review scope and staffing")]
    [InlineData("Completed", "Completed", "Delivered", "Delivery is complete")]
    [InlineData("Cancelled", "Cancelled", "Cancelled", "No further delivery is scheduled")]
    public async Task DeliveryReadinessAndNextActionsReflectRecordedState(string planStatus, string executionStatus, string readiness, string nextAction)
    {
        var api = new Api();
        api.DeliveryPlans = [DeliveryPlan(api, planStatus, [DeliveryExecution(executionStatus, "quality")])];
        var html = await RenderDelivery(api);
        Assert.Contains($">{readiness}</dd>", html);
        Assert.Contains(nextAction, html);
        Assert.DoesNotContain(">Ready</dd>", html);
        if (planStatus is "Completed" or "Cancelled") Assert.DoesNotContain("class=\"release-execution\"", html);
    }

    [Fact]
    public async Task DeliveryShowsAssignedReviewerAndOnlyOffersReviewToThatPerson()
    {
        var api = new Api();
        var reviewer = Guid.NewGuid();
        api.DeliveryPeople = [new(reviewer, "Independent QA", "Human", null, false)];
        var execution = DeliveryExecution("WaitingForHuman", "quality") with
        {
            Stages = [new(Guid.NewGuid(), "quality", "Human", 1, "WaitingForHuman", "OrganizationUser", reviewer,
                null, null, 1, null, null, null, null, DateTimeOffset.UtcNow)]
        };
        api.DeliveryPlans = [DeliveryPlan(api, "Active", [execution])];
        var managerHtml = await RenderDelivery(api);
        Assert.Contains("Owner: Independent QA", managerHtml);
        Assert.DoesNotContain(">Review exact candidate</button>", managerHtml);
        api.CurrentUserId = reviewer;
        var reviewerHtml = await RenderDelivery(api);
        Assert.Contains(">Review exact candidate</button>", reviewerHtml);
        Assert.DoesNotContain(">Pause delivery</button>", reviewerHtml);
    }

    private static WorkDeliveryExecutionResponse DeliveryExecution(string status, string stage) => new(Guid.NewGuid(), "Release", null,
        Guid.NewGuid(), status, stage, 1, new("candidate-digest", 1, [], []), null, [], []);

    private static WorkDeliveryPlanResponse DeliveryPlan(Api api, string status, IReadOnlyList<WorkDeliveryExecutionResponse> executions) =>
        new(Guid.NewGuid(), api.ProjectId, "Scaffold & Procedural Asset Pipeline", api.ManagerId, status, 1, 1,
            [], [], executions.Select(e=>new WorkDeliveryScopeSnapshot(e.Scope,e.ItemId,e.BoardId,1,[],new(["Requirement"],["Works"]),[])).ToArray(), executions, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Task<string> RenderDelivery(Api api) => Render(api,
        new() { ["OrganizationId"] = api.OrganizationId, ["WorkstreamId"] = api.ProjectId, ["Tab"] = "delivery" });

    private static async Task<string> Render(Api api, Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Projects>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class Api : HttpMessageHandler
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid ManagerId { get; } = Guid.NewGuid();
        public bool Populated { get; init; }
        public bool HasProjectUsage { get; init; }
        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public IReadOnlyList<WorkDeliveryPlanResponse> DeliveryPlans { get; set; } = [];
        public IReadOnlyList<ProjectSetupPerson> DeliveryPeople { get; set; } = [];
        public Guid? CurrentUserId { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path); Methods.Add(request.Method);
            object result;
            if (path.EndsWith($"/{ProjectId}/inspection")) result = Inspection();
            else if (path.EndsWith("/workstreams/inspection")) result = new ProjectPortfolioResponse(DateTimeOffset.UtcNow, 1, 1,
                [new(ProjectId, "Neon Bash", "Ship a prototype", "Active", "Development", "software-prototype.v1", 1, ManagerId, null, null, null, 1, DateTimeOffset.UtcNow, 1, 1, 5, 0, 0, null) { AccountableManagerName = "Matt", BlockedItems = 2, ReleaseReady = false }]);
            else if (path.EndsWith("/work/delivery/read")) result = DeliveryPlans;
            else if (path.EndsWith("/project-setup/options")) result = new ProjectSetupOptions(CurrentUserId ?? ManagerId, false,
                [new(ManagerId, "Gabriel Reyes", "Human", null, true), .. DeliveryPeople], [], []);
            else if (path.EndsWith("/analytics/efficiency"))
            {
                var total = new EfficiencyUsage(10, 999999, 0, 0, 0, 10, 10, 0, 0, 0, 1000);
                var project = total with { InputTokens = 15000, OutputTokens = 5000, FullyReportedCalls = 10, ActiveAgentTimeMs = 90000 };
                result = new WorkEfficiencyResponse(OrganizationId, DateTimeOffset.UtcNow, null, null, total, total, total,
                    HasProjectUsage ? [new(ProjectId, null, ProjectId, "Project", "Neon Bash", "Active", project, project, new(null, null, null, null, null, 0, 0, false))] : [], [], false, "Only reported provider usage is counted.");
            }
            else if (path.EndsWith("/work/boards")) result = new WorkBoardDirectoryResponse([], false);
            else if (path.EndsWith("/work/personal-todos")) result = new CSweet.WorkManagement.Contracts.PersonalTodoDirectory([]);
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
        }
        private ProjectInspectionResponse Inspection()
        {
            if (!Populated) return new(Resource(), new(0, 0, 0, 0, 0, 0, 0, false), [], [], [], [], [], [], [], DateTimeOffset.UtcNow);
            ProjectInspectionResource Item(string type, string title, string status) => Resource() with { ResourceType = type, Title = title, Status = status, TypeKey = null, Revision = null, DeepLink = "/linked-record" };
            ProjectAuditEntry Entry(string summary) => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "work.completed", "Work", "Succeeded", "Project", ProjectId, summary, "Agent", ManagerId, "Gabriel Reyes", null, null, null, null, null, "hash");
            return new(Resource() with { Metadata = JsonSerializer.SerializeToElement(new { Outcome = string.Concat(Enumerable.Repeat("Deliver a polished browser game with responsive controls, readable visual effects, and an accessible first-time player experience. ", 5)), LifecycleStage = "Concept" }) },
                new(20, 2, 10, 1, 2, 0, 0, false),
                [Item("Team", "Video Game Team", "Active"), Item("Supervision", "Production leadership", "Active")], [],
                [Item("Document", "Creative brief", "Approved"), Item("DocumentReview", "Technical feasibility", "Pending")],
                [Item("Conversation", "Creative direction review", "Active")],
                [Item("Decision", "Choose launch platforms", "Pending"), Item("Gate", "Prototype acceptance", "Pending")], [],
                [Entry("Completed work.item.read."), Entry("Creative direction approved")], DateTimeOffset.UtcNow);
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/projects");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
