using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class OverviewHiringApprovalTests
{
    [Fact]
    public async Task ShowsStableRequesterAvatarRoleTeamAndItemReviewLinkEvenWithDuplicateNames()
    {
        await using var fixture = new Fixture();
        var output = await fixture.RenderAsync();
        var html = await fixture.HtmlAsync(output);
        Assert.Contains("https://example.com/requesting-agent.jpg", html);
        Assert.DoesNotContain("https://example.com/other-agent.jpg", html);
        Assert.Contains("Creative Director", html);
        Assert.Contains("Video Game Team", html);
        Assert.Contains("Video Game Team hiring plan", html);
        Assert.Contains("request-kind", html);
        Assert.Contains(">Hiring</span>", html);
        Assert.Contains("5 requested hires", html);
        Assert.Contains($"/organizations/{fixture.Api.Organization:D}/approvals?approvalId={fixture.Api.Approval.Id:D}", html);
        Assert.DoesNotContain("accepted video game vision", html);
        Assert.DoesNotContain("Review hire", html);
        Assert.Empty(fixture.Api.Decisions);
        CaptureMarkup(html);
    }

    [Fact]
    public void CountsAdditionalSeatsRatherThanDesiredTeamSize()
    {
        var request = ApiHandler.Request(Guid.NewGuid(), Guid.NewGuid());
        var hires = HiringApprovalPresentation.RequestedHires(request);
        Assert.Equal(5, hires.Sum(x => x.Headcount));
        Assert.Equal(new[] { "Technical Director", "Game Engineer" }, hires.Select(x => x.Role.Title));
        Assert.Equal(new[] { 2, 3 }, hires.Select(x => x.Headcount));
        Assert.Empty(HiringApprovalPresentation.RequestedHires(request with { Deltas = [] }));
    }

    [Fact]
    public async Task LargeHireDetailsArePagedWithReviewOnlyOnTheParentCard()
    {
        await using var fixture = new Fixture();
        var roles = Enumerable.Range(1, 14).Select(i => ApiHandler.Role($"Role {i}", i)).ToList();
        fixture.Api.Approval = fixture.Api.Approval with { ResourceChange = fixture.Api.Approval.ResourceChange! with
        { Roles = roles, Deltas = roles.Select(x => new ResourceChangeRoleDelta("Add", x, null)).ToList() } };
        var output = await fixture.RenderAsync();
        var open = Assert.Single(fixture.Activator.Buttons, b => b.Class == "requested-hires");
        await fixture.ClickAsync(open);
        var menu = fixture.Activator.Menu!;
        Assert.True(menu.GetState(x => x.Open));
        Assert.False(menu.Modal);
        Assert.False(menu.LockScroll);
        Assert.Equal(Origin.BottomLeft, menu.AnchorOrigin);
        Assert.Equal(Origin.TopLeft, menu.TransformOrigin);
        Assert.Equal("hiring-plan-popover", menu.PopoverClass);
        var details = await fixture.RenderDetailsAsync();
        var first = await fixture.HtmlAsync(details);
        Assert.Contains("Roles 1", first); Assert.Contains("of 14", first);
        Assert.Contains("Role 6", first); Assert.DoesNotContain(">Role 7<", first);
        var next = fixture.Activator.IconButtons.Last(b => b.Icon == Icons.Material.Outlined.ChevronRight);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => next.OnClick.InvokeAsync(new MouseEventArgs()));
        details = await fixture.RenderDetailsAsync();
        var second = await fixture.HtmlAsync(details);
        Assert.Contains("Role 7", second); Assert.DoesNotContain(">Role 6<", second);
        Assert.DoesNotContain("Review in Approvals", second);
        Assert.Contains($"/organizations/{fixture.Api.Organization:D}/approvals?approvalId={fixture.Api.Approval.Id:D}", await fixture.HtmlAsync(output));
        Assert.Empty(fixture.Api.Decisions);
        CaptureMarkup(first, "details-render.html");
        var close = fixture.Activator.IconButtons.Last(b => b.Icon == Icons.Material.Outlined.Close);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => close.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.False(menu.GetState(x => x.Open));
        Assert.Contains("aria-expanded=\"false\"", await fixture.HtmlAsync(output));
        await fixture.ClickAsync(open);
        details = await fixture.RenderDetailsAsync();
        Assert.Contains("Role 6", await fixture.HtmlAsync(details));
        Assert.DoesNotContain(">Role 7<", await fixture.HtmlAsync(details));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DecisionsUseExistingEndpointsRetrySafelyAndDismissStaleQueueItems(bool approve, bool failFirst)
    {
        await using var fixture = new Fixture();
        fixture.Api.FailFirst = failFirst;
        var output = await fixture.RenderAsync();
        var open = fixture.Activator.Buttons.Single(b => b.Href is null &&
            (approve ? b.Variant == Variant.Filled : b.Color == Color.Error));
        await fixture.ClickAsync(open);
        var submit = open;
        if (approve)
        {
            Assert.Null(fixture.Activator.Dialog);
            Assert.Single(fixture.Api.Decisions);
        }
        else
        {
            Assert.Empty(fixture.Api.Decisions);
            await fixture.RenderDialogAsync();
            var input = fixture.Activator.Feedback!;
            await fixture.Renderer.Dispatcher.InvokeAsync(() => input.ValueChanged.InvokeAsync("Please adjust the staffing."));
            submit = fixture.Activator.Buttons.Last(b => b.Variant == Variant.Filled);
            await fixture.ClickAsync(submit);
        }
        if (failFirst)
        {
            var failed = approve ? output : await fixture.RenderDialogAsync();
            Assert.Contains("The request changed", await fixture.HtmlAsync(failed));
            Assert.Equal(0, fixture.RefreshCount);
            await fixture.ClickAsync(submit);
            Assert.Equal(fixture.Api.Decisions[0].IdempotencyKey, fixture.Api.Decisions[1].IdempotencyKey);
        }
        Assert.Equal(1, fixture.RefreshCount);
        Assert.Contains("Nothing needs your attention", await fixture.HtmlAsync(output));
        Assert.All(fixture.Api.Decisions, decision =>
        {
            Assert.Equal(fixture.Api.Approval.Id, decision.RequestId);
            Assert.Equal(approve ? "Approve" : "Reject", decision.Decision);
            Assert.Equal(approve ? null : "Please adjust the staffing.", decision.Comment);
        });
        Assert.All(fixture.Api.DecisionUris, uri => Assert.EndsWith($"/hiring/resource-changes/{fixture.Api.Approval.Id:D}/decide", uri));
        var count = fixture.Api.Decisions.Count;
        await fixture.ClickAsync(submit);
        Assert.Equal(count, fixture.Api.Decisions.Count);
    }

    [Fact]
    public async Task UnauthorizedOrCompletedItemsCannotBeDecided()
    {
        await using var fixture = new Fixture();
        fixture.Api.Approval = fixture.Api.Approval with { CanDecide = false };
        var output = await fixture.RenderAsync();
        Assert.Contains("Nothing needs your attention", await fixture.HtmlAsync(output));
        Assert.DoesNotContain(fixture.Activator.Buttons, b => b.Variant == Variant.Filled);
        Assert.Empty(fixture.Api.Decisions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkflowApproveSubmitsDirectlyAndDenialUsesWorkflowEndpoint(bool approve)
    {
        await using var fixture = new Fixture();
        fixture.Api.Approval = fixture.Api.Approval with
        {
            Kind = ApprovalDashboardKinds.HiringWorkflow, ResourceChange = null,
            HiringWorkflow = new(fixture.Api.Approval.Id, "Producer", "candidate:producer", "Alex", "Catalog", "Alex",
                null, "Naomi Chen", "Pending", "Install the approved agent.", [], [], [], [], DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, null, null)
        };
        var output = await fixture.RenderAsync();
        if (approve)
        {
            await fixture.ClickAsync(fixture.Activator.Buttons.Single(b => b.Variant == Variant.Filled));
            Assert.Null(fixture.Activator.Dialog);
        }
        else
        {
            await fixture.ClickAsync(fixture.Activator.Buttons.First(b => b.Color == Color.Error));
            await fixture.RenderDialogAsync();
            await fixture.ClickAsync(fixture.Activator.Buttons.Last(b => b.Variant == Variant.Filled));
        }
        Assert.Single(fixture.Api.WorkflowDecisions);
        Assert.Equal(approve ? "Approve" : "Reject", fixture.Api.WorkflowDecisions[0].Decision);
        Assert.Contains("Nothing needs your attention", await fixture.HtmlAsync(output));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public ApiHandler Api { get; } = new();
        public CapturingActivator Activator { get; } = new();
        public HtmlRenderer Renderer { get; }
        public int RefreshCount { get; private set; }
        public Fixture()
        {
            var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetApiClients();
            services.AddSingleton<IComponentActivator>(Activator);
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<NavigationManager, TestNavigation>();
            services.AddSingleton(new HttpClient(Api) { BaseAddress = new Uri("http://localhost/") });
            _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderAsync() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<PendingAgentQuestions>(0);
                builder.AddAttribute(1, "OrganizationId", Api.Organization);
                builder.AddAttribute(2, "SummaryOnly", true);
                builder.AddAttribute(3, "Approvals", new[] { Api.Approval });
                builder.AddAttribute(4, "RefreshRequested", EventCallback.Factory.Create(this, () => RefreshCount++));
                builder.CloseComponent();
            };
            return await Renderer.RenderComponentAsync<EmployeePortraitScope>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = Api.Organization, ["ChildContent"] = content }));
        });
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderDialogAsync() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var dialog = Activator.Dialog!;
            RenderFragment content = builder => { builder.AddContent(0, dialog.DialogContent); builder.AddContent(1, dialog.DialogActions); };
            return await Renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = content }));
        });
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderDetailsAsync() => Renderer.Dispatcher.InvokeAsync(() =>
            Renderer.RenderComponentAsync<CascadingValue<MudMenu>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = Activator.Menu, ["IsFixed"] = true, ["ChildContent"] = Activator.Menu!.ChildContent })));
        public Task ClickAsync(MudButton button) => Renderer.Dispatcher.InvokeAsync(() => button.OnClick.InvokeAsync(new MouseEventArgs()));
        public Task<string> HtmlAsync(Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output) => Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private static void CaptureMarkup(string html, string filename = "card-render.html", [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "../../artifacts/hiring-approval-widget"));
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, filename), html);
    }
    private sealed class FragmentHost : ComponentBase
    {
        [Parameter] public RenderFragment? Content { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Content);
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public List<MudButton> Buttons { get; } = [];
        public List<MudIconButton> IconButtons { get; } = [];
        public MudDialog? Dialog { get; private set; }
        public MudMenu? Menu { get; private set; }
        public MudTextField<string>? Feedback { get; private set; }
        public HiringWorkflowApprovalCard? WorkflowCard { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is MudButton b) Buttons.Add(b);
            if (component is MudIconButton icon) IconButtons.Add(icon);
            if (component is MudDialog dialog) Dialog = dialog;
            if (component is MudMenu menu) Menu = menu;
            if (component is MudTextField<string> input) Feedback = input;
            if (component is HiringWorkflowApprovalCard card) WorkflowCard = card;
            return component;
        }
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        private readonly Guid _requester = Guid.NewGuid(), _installation = Guid.NewGuid(), _otherInstallation = Guid.NewGuid();
        public ApprovalDashboardItemResponse Approval { get; set; }
        public List<ResourceChangeDecisionRequest> Decisions { get; } = [];
        public List<DecideHiringWorkflowRequest> WorkflowDecisions { get; } = [];
        public List<string> DecisionUris { get; } = [];
        public bool FailFirst { get; set; }
        public ApiHandler()
        {
            var request = Request(Organization, _requester);
            Approval = new(request.Id, ApprovalDashboardKinds.ResourceChange, $"Team design: {request.ProductGoal}", request.Rationale,
                "Pending", "Naomi Chen", "Owner", DateTimeOffset.UtcNow, null, "/communications", true, request)
            { RequesterOrganizationUserId = _requester, RequestingTeam = "Video Game Team" };
        }
        public static ResourceChangeRole Role(string title, int priority, int count = 1) => new(title, "Video Game Team", title,
            $"Scope for {title}", count, priority, "Now", [], false, null, null);
        public static ResourceChangeRequestResponse Request(Guid org, Guid requester)
        {
            var producer = Role("Producer", 0);
            var technical = Role("Technical Director", 1, 4);
            var engineer = Role("Game Engineer", 2, 3);
            var reduced = Role("Game Designer", 3);
            var removed = Role("QA", 4);
            return new(Guid.NewGuid(), org, requester, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "Plan and deliver the accepted video game vision fddf7c7cdb073f03d6f38886a3a6cbe649f0b1ba270063ebb983e9a72e91f88c.",
                "Keep the Producer and add technical delivery capacity.", 1, [producer, technical, engineer, reduced],
                [new("Unchanged", producer, producer), new("Increase", technical, technical with { Headcount = 2 }), new("Add", engineer, null),
                 new("Modify", reduced, reduced with { Headcount = 2 }), new("Remove", removed, removed)], [], [], null,
                "Pending", "Delivered", null, DateTimeOffset.UtcNow, null) { TeamName = "Video Game Team" };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                DecisionUris.Add(path);
                if (path.Contains("/resource-changes/")) Decisions.Add((await request.Content!.ReadFromJsonAsync<ResourceChangeDecisionRequest>(token))!);
                else WorkflowDecisions.Add((await request.Content!.ReadFromJsonAsync<DecideHiringWorkflowRequest>(token))!);
                if (FailFirst && Decisions.Count == 1) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { message = "The request changed. Review it and try again." }) };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
            }
            object result = path.EndsWith("/users")
                ? new[] { new OrganizationUserResponse(_requester, Organization, null, null, null, "Naomi Chen", null, 1, 0, DateTimeOffset.UtcNow) { AgentInstallationId = _installation },
                    new OrganizationUserResponse(Guid.NewGuid(), Organization, null, null, null, "Naomi Chen", null, 1, 0, DateTimeOffset.UtcNow) { AgentInstallationId = _otherInstallation } }
                : path.EndsWith("/installations")
                    ? new[] { JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"id":"{{_installation}}","agentId":"creative-director","imageUrl":"https://example.com/requesting-agent.jpg","roleName":"Creative Director"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
                        JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"id":"{{_otherInstallation}}","agentId":"other","imageUrl":"https://example.com/other-agent.jpg","roleName":"Other role"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))! }
                    : Array.Empty<PendingAgentQuestionResponse>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
