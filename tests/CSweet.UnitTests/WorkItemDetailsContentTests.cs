using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.WorkBoards;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class WorkItemDetailsContentTests
{
    [Fact]
    public async Task OverviewRetainsPlanningAndUsesOnlyTheSelectedTicketsExactRevisionLinks()
    {
        await using var f = new Fixture();
        await f.Render();
        var html = await f.Html();
        Assert.Contains("Pipeline &amp; architecture validation", html);
        Assert.Contains("prisma-feasibility", html);
        Assert.Contains("Confirm renderer support", html);
        Assert.Contains("60 fps &lt;script&gt;criterion&lt;/script&gt;", html);
        Assert.Contains("Keep keyboard support", html);
        Assert.Contains($"revision={f.Api.Revision:D}", html);
        Assert.DoesNotContain("private-sha256", html);
        Assert.DoesNotContain("Other ticket evidence", html);
        Assert.DoesNotContain(f.Api.Requests, path => path.EndsWith("/collaboration"));
        Assert.Contains("Follow", html);
        Capture(html, "editorial-ticket-overview.html");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ClosedOrUnprivilegedPaneDoesNotRequestSprintExecution(bool open, bool canRead)
    {
        await using var f = new Fixture();
        await f.Render(open, canRead);
        Assert.DoesNotContain(f.Api.Requests, path => path.EndsWith("/execution"));
        Assert.DoesNotContain($"revision={f.Api.Revision:D}", await f.Html());
    }

    [Fact]
    public async Task DiscussionAndActivityLoadOnDemandThroughTheirExistingAuthorizedEndpoint()
    {
        await using var f = new Fixture();
        await f.Render();
        await f.SelectTab(1);
        var discussion = await f.Html();
        Assert.Contains("Please verify the integrated GPU path.", discussion);
        Assert.Contains("Add a comment", discussion);
        Capture(discussion, "editorial-ticket-discussion.html");
        await f.SelectTab(2);
        var activity = await f.Html();
        Assert.Contains("Moved the ticket", activity);
        Assert.DoesNotContain("sensitive-activity-json", activity);
        Assert.Equal(2, f.Api.Requests.Count(path => path.EndsWith("/collaboration")));
        Capture(activity, "editorial-ticket-activity.html");
        await f.SelectTab(0);
        await f.SelectTab(1);
        Assert.Equal(2, f.Api.Requests.Count(path => path.EndsWith("/collaboration")));
    }

    [Fact]
    public async Task ReferenceAndActivityFailuresShowRecoverableMessagesInsteadOfRawErrors()
    {
        await using var f = new Fixture();
        f.Api.DenyReads = true;
        await f.Render();
        Assert.Contains("References are unavailable or your access has changed.", await f.Html());
        await f.SelectTab(2);
        Assert.Contains("Activity is unavailable or your access has changed.", await f.Html());
        Assert.DoesNotContain("server-internal-detail", await f.Html());
    }

    private static void Capture(string html, string filename)
    {
        if (Environment.GetEnvironmentVariable("CSWEET_DETAIL_PREVIEWS") is { } path)
            File.WriteAllText(Path.Combine(path, filename), html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly Activator _activator = new();
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _output;
        public Api Api { get; } = new();

        public Fixture()
        {
            var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
            services.AddSingleton<IComponentActivator>(_activator);
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<NavigationManager, TestNavigation>();
            services.AddSingleton(new HttpClient(Api) { BaseAddress = new Uri("http://localhost/") });
            _services = services.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task Render(bool open = true, bool canRead = true) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var board = Api.Board with { Board = Api.Board.Board with { AllowedActions = canRead ? [WorkOrchestrationActions.Read] : [] } };
            _output = await _renderer.RenderComponentAsync<WorkItemDetailsContent>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Board"] = board, ["Item"] = Api.Item, ["Open"] = open,
                ["DeliveryContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<MudSelect<Guid?>>(0);
                    builder.AddAttribute(1, "Label", "Sprint");
                    builder.AddAttribute(2, "Value", Api.Item.SprintId);
                    builder.AddAttribute(3, "Variant", Variant.Outlined);
                    builder.AddAttribute(4, "Margin", Margin.Dense);
                    builder.AddAttribute(5, "ChildContent", (RenderFragment)(options =>
                    {
                        options.OpenComponent<MudSelectItem<Guid?>>(0);
                        options.AddAttribute(1, "Value", Api.Item.SprintId);
                        options.AddAttribute(2, "ChildContent", (RenderFragment)(text => text.AddContent(0, "Production Sprint 1 · Active")));
                        options.CloseComponent();
                    }));
                    builder.CloseComponent();
                }),
                ["FooterContent"] = (RenderFragment)(builder =>
                {
                    foreach (var label in new[] { "Set estimate", "More actions", "Move or assign" })
                    {
                        builder.OpenComponent<MudButton>(0);
                        builder.AddAttribute(1, "Variant", Variant.Outlined);
                        builder.AddAttribute(2, "ChildContent", (RenderFragment)(text => text.AddContent(0, label)));
                        builder.CloseComponent();
                    }
                })
            }));
        });
        public Task SelectTab(int index) => _renderer.Dispatcher.InvokeAsync(() => _activator.Tabs!.ActivePanelIndexChanged.InvokeAsync(index));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_output.ToHtmlString);
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }

    private sealed class Activator : IComponentActivator
    {
        public MudTabs? Tabs { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MudTabs tabs) Tabs = tabs;
            return component;
        }
    }

    private sealed class Api : HttpMessageHandler
    {
        public Guid Revision { get; } = Guid.NewGuid();
        private readonly Guid _artifact = Guid.NewGuid();
        public WorkBoardItemResponse Item { get; }
        public WorkBoardDetailResponse Board { get; }
        public List<string> Requests { get; } = [];
        public bool DenyReads { get; set; }
        public Api()
        {
            var board = Guid.NewGuid(); var column = Guid.NewGuid(); var sprint = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            Item = new(Guid.NewGuid(), board, column, null, sprint, "Task", "[prisma-feasibility] Pipeline & architecture validation",
                "Confirm Phaser compatibility, rendering pipeline support, the 60 fps performance budget, and repository structure. Produce an implementation decision document for engineering.", "Running", "High", 6, 1, 2, null, now, now)
            { Identifier = "PRISM-8", AssignedDisplayName = "Victor Lin", TypeKey = "video-game.research-spike.v1",
              Planning = new Wire.WorkItemPlanningSpecification(["Confirm renderer support"],
                  ["60 fps <script>criterion</script>", "Document the selected Phaser version and its rendering capabilities.", "Define the hardware budget, measurement method, and ordered degradation path.", "Document repository structure and review criteria.", "Provide implementation guidance for scenes, physics, audio, and particles."], ["Keep keyboard support"]) };
            Board = new(new(board, Guid.NewGuid(), null, "Prism Break", "", false, false, false, null, 1, 1, 1, now, now, []),
                [new(column, "In Progress", "InProgress", 0, "None", null)], [Item]);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (path.Contains("/tracking/")) return Task.FromResult(Json(new Wire.WorkTrackingSnapshot(Item.Id, false, false, null, [])));
            if (DenyReads) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("server-internal-detail") });
            if (path.EndsWith("/collaboration"))
            {
                var author = Guid.NewGuid();
                WorkItemCommentResponse comment = new(Guid.NewGuid(), Item.Id, "OrganizationUser", author, "Victor Lin", "Please verify the integrated GPU path.", 1, DateTimeOffset.UtcNow, null);
                WorkItemActivityResponse activity = new(Guid.NewGuid(), Board.Board.Id, Item.Id, "item.moved", "work.item.move", "OrganizationUser", author, "Victor Lin", "sensitive-activity-json", DateTimeOffset.UtcNow);
                return Task.FromResult(Json(new WorkItemCollaborationResponse([comment], [activity], true, author)));
            }
            var evidence = new Wire.WorkExecutionEvidence("artifact-revision", "Technical architecture", JsonSerializer.Serialize(new { artifactId = _artifact, revisionId = Revision, sha256 = "private-sha256" }));
            var stage = new Wire.WorkStageExecutionResponse(Guid.NewGuid(), "review", "Agent", 1, "Completed", "Agent", null, null, null, 1, null, null, null, null, DateTimeOffset.UtcNow)
            { LatestOutcome = new(Guid.NewGuid(), Guid.NewGuid(), "Completed", "accepted", "Reviewed", JsonSerializer.SerializeToElement(new { }), [evidence], []) };
            var selected = new Wire.WorkItemExecutionResponse(Guid.NewGuid(), Item.Id, Item.Identifier!, "specialist-execution", 1, "Running", null, [stage], DateTimeOffset.UtcNow);
            var other = selected with { WorkItemId = Guid.NewGuid(), Stages = [stage with { LatestOutcome = stage.LatestOutcome with { Evidence = [evidence with { Name = "Other ticket evidence" }] } }] };
            return Task.FromResult(Json(new Wire.WorkSprintExecutionResponse(Guid.NewGuid(), Board.Board.Id, Item.SprintId!.Value, Guid.NewGuid(), Guid.NewGuid(), "Active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [selected, other])));
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
