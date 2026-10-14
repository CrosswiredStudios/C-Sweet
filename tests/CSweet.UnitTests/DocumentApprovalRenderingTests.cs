using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class DocumentApprovalRenderingTests
{
    [Fact]
    public async Task OverviewChipOpensTheExactSubmittedRevisionWithoutNavigation()
    {
        await using var f = new Fixture();
        DocumentApprovalSelection? selection = null;
        var output = await f.RenderAsync<PendingAgentQuestions>(new()
        {
            ["OrganizationId"] = f.Api.Organization, ["SummaryOnly"] = true,
            ["Approvals"] = new[] { f.Api.Item },
            ["DocumentOpened"] = EventCallback.Factory.Create<DocumentApprovalSelection>(f, value => selection = value)
        });
        var html = await f.Html(output);
        Assert.Contains("Open document", html);
        Assert.Contains("Submitted revision 1", html);
        Assert.Contains("Approve revision 1", html);
        Assert.DoesNotContain("Review approval", html);
        CaptureMarkup(html, "overview-card.html");
        var chip = f.Buttons.Single(x => x.Class == "document-approval-open");
        Assert.Null(chip.Href);
        await f.Click(chip);
        Assert.Equal(new(f.Api.Artifact, f.Api.Submitted, true), selection);
        Assert.Empty(f.Api.Decisions);
    }

    [Fact]
    public async Task ApprovalsDeepLinkOpensTheExistingPaneWithSubmittedContent()
    {
        await using var f = new Fixture();
        f.Configure = component =>
        {
            if (component is Approvals)
                typeof(Approvals).GetProperty(nameof(Approvals.SelectedApprovalId))!.SetValue(component, f.Api.Item.Id);
        };
        var output = await f.RenderAsync<Approvals>(new()
        { ["OrganizationId"] = f.Api.Organization });
        var html = await f.Html(output);
        Assert.Contains("approval-document-host", html);
        Assert.Contains("artifact-workspace", html);
        Assert.Contains("SUBMITTED CONTENT", html);
        Assert.DoesNotContain("LATEST DRAFT CONTENT", html);
        Assert.Contains("The latest draft is revision 2", html);
        Assert.Contains("Decision for submitted revision 1", html);
        Assert.DoesNotContain("Save draft", html);
        CaptureMarkup(html, "approvals-pane.html");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaneKeepsPinnedContentAndSuppressesUnauthorizedOrSupersededDecisions(bool superseded)
    {
        await using var f = new Fixture();
        if (superseded)
            f.Api.Detail = f.Api.Detail with { Document = f.Api.Detail.Document with { SubmittedRevisionId = f.Api.Latest } };
        var output = await f.RenderPane(canDecide:superseded);
        var html = await f.Html(output);
        Assert.Contains("SUBMITTED CONTENT", html);
        Assert.DoesNotContain("LATEST DRAFT CONTENT", html);
        Assert.DoesNotContain("artifact-decision-bar", html);
        Assert.DoesNotContain(f.Buttons, b => b.Color == Color.Warning);
    }

    [Fact]
    public async Task MissingPinnedRevisionDoesNotFallBackToLatestDraft()
    {
        await using var f = new Fixture();
        var output = await f.RenderPane(revision:Guid.NewGuid());
        var html = await f.Html(output);
        Assert.Contains("linked revision is unavailable", html);
        Assert.DoesNotContain("LATEST DRAFT CONTENT", html);
        Assert.DoesNotContain("artifact-decision-bar", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardAndPaneApproveTheDisplayedRevisionAndRefreshTheirHost(bool pane)
    {
        await using var f = new Fixture();
        var output = pane ? await f.RenderPane() : await f.RenderCard();
        var approve = f.Buttons.Single(b => pane ? b.Color == Color.Primary && b.Variant == Variant.Filled : b.Color == Color.Success);
        await f.Click(approve);
        var decision = Assert.Single(f.Api.Decisions);
        Assert.Equal(f.Api.Submitted, decision.RevisionId);
        Assert.Equal("accept", decision.Decision);
        Assert.Equal(1, f.Refreshes);
        await f.Click(approve);
        Assert.Single(f.Api.Decisions);
        Assert.DoesNotContain("LATEST DRAFT CONTENT", await f.Html(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardAndPaneRequireRevisionFeedbackAndPreserveIt(bool pane)
    {
        await using var f = new Fixture();
        var output = pane ? await f.RenderPane() : await f.RenderCard();
        if (!pane)
            await f.Click(f.Buttons.Single(b => b.Color == Color.Primary && b.Variant == Variant.Outlined && b.Class != "document-approval-open"));
        var submit = pane ? f.Buttons.Single(b => b.Color == Color.Warning) : f.Buttons.Last(b => b.Variant == Variant.Filled && b.Color == Color.Primary);
        await f.Click(submit);
        Assert.Empty(f.Api.Decisions);
        Assert.Contains("Feedback is required", await f.Html(output));
        await f.Renderer.Dispatcher.InvokeAsync(() => f.Inputs.Last().ValueChanged.InvokeAsync("Clarify the first playable scope."));
        await f.Click(submit);
        var decision = Assert.Single(f.Api.Decisions);
        Assert.Equal(f.Api.Submitted, decision.RevisionId);
        Assert.Equal("request-revision", decision.Decision);
        Assert.Equal("Clarify the first playable scope.", decision.Comment);
        Assert.Equal(1, f.Refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissedSubmissionChangeCannotApproveANewerRevision(bool pane)
    {
        await using var f = new Fixture();
        var output = pane ? await f.RenderPane() : await f.RenderCard();
        var approve = f.Buttons.Single(b => pane ? b.Color == Color.Primary && b.Variant == Variant.Filled : b.Color == Color.Success);
        f.Api.Detail = f.Api.Detail with { Document = f.Api.Detail.Document with { SubmittedRevisionId = f.Api.Latest } };
        await f.Click(approve);
        Assert.Empty(f.Api.Decisions);
        Assert.Contains("no longer awaiting", await f.Html(output));
        Assert.Equal(1, f.Refreshes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UnavailableDocumentHasAnActionableErrorAndNoDecisions(HttpStatusCode error)
    {
        await using var f = new Fixture();
        f.Api.ReadError = error;
        var output = await f.RenderPane();
        var html = await f.Html(output);
        Assert.Contains(error == HttpStatusCode.InternalServerError ? "could not be loaded" : "unavailable or has not been shared", html);
        Assert.DoesNotContain("artifact-decision-bar", html);
        Assert.Empty(f.Api.Decisions);
    }

    [Fact]
    public async Task HistoryRetainsReviewedRevisionAuthorAndFeedback()
    {
        await using var f = new Fixture();
        f.Api.Item = f.Api.Item with { Status = "RevisionRequested", CanDecide = false,
            DecisionComment = "Clarify scope <script>unsafe</script>", ActualDecisionMaker = "CEO", DecidedAt = DateTimeOffset.UtcNow };
        var output = await f.RenderCard();
        var html = await f.Html(output);
        Assert.Contains("Reviewed revision 1", html);
        Assert.Contains("Revision requested", html);
        Assert.Contains("Decided by CEO", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("Send revision request", html);
        Assert.DoesNotContain(f.Buttons, b => b.Color == Color.Success);
    }

    private static void CaptureMarkup(string html, string name)
    {
        if (Environment.GetEnvironmentVariable("CSWEET_DOCUMENT_APPROVAL_PREVIEW") is not { } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CapturingActivator _activator = new();
        public ApiHandler Api { get; } = new();
        public HtmlRenderer Renderer { get; }
        public List<MudButton> Buttons => _activator.Buttons;
        public List<MudTextField<string>> Inputs => _activator.Inputs;
        public int Refreshes { get; private set; }
        public Action<IComponent>? Configure { set => _activator.Configure = value; }
        public Fixture()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddMudServices(); services.AddCSweetApiClients();
            services.AddSingleton<IComponentActivator>(_activator);
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<NavigationManager, TestNavigation>();
            services.AddSingleton(new HttpClient(Api) { BaseAddress = new Uri("http://localhost/") });
            _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<HtmlRootComponent> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            Renderer.Dispatcher.InvokeAsync(() => Renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters)));
        public Task<HtmlRootComponent> RenderCard() => RenderAsync<DocumentApprovalCard>(new()
        { ["OrganizationId"] = Api.Organization, ["Approval"] = Api.Item,
          ["RefreshRequested"] = EventCallback.Factory.Create(this, () => Refreshes++) });
        public Task<HtmlRootComponent> RenderPane(bool canDecide = true, Guid? revision = null) => RenderAsync<ArtifactWorkspace>(new()
        { ["OrganizationId"] = Api.Organization, ["ArtifactId"] = Api.Artifact, ["RevisionId"] = revision ?? Api.Submitted,
          ["ReviewOnly"] = true, ["CanDecide"] = canDecide,
          ["Changed"] = EventCallback.Factory.Create<ArtifactDocumentDetail>(this, _ => Refreshes++) });
        public Task Click(MudButton button) => Renderer.Dispatcher.InvokeAsync(() => button.OnClick.InvokeAsync(new MouseEventArgs()));
        public Task<string> Html(HtmlRootComponent output) => Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public List<MudButton> Buttons { get; } = [];
        public List<MudTextField<string>> Inputs { get; } = [];
        public Action<IComponent>? Configure { get; set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            Configure?.Invoke(component);
            if (component is MudButton button) Buttons.Add(button);
            if (component is MudTextField<string> input) Inputs.Add(input);
            return component;
        }
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        public Guid Artifact { get; } = Guid.NewGuid();
        public Guid Submitted { get; } = Guid.NewGuid();
        public Guid Latest { get; } = Guid.NewGuid();
        public ArtifactDocumentDetail Detail { get; set; }
        public ApprovalDashboardItemResponse Item { get; set; }
        public HttpStatusCode? ReadError { get; set; }
        public List<DecideArtifactRevisionRequest> Decisions { get; } = [];
        public ApiHandler()
        {
            var now = DateTimeOffset.UtcNow;
            var submitted = new ArtifactRevisionResponse(Submitted, Artifact, 1, null, "SUBMITTED CONTENT", "submitted-hash", "Submitted", "Naomi Chen", now, now, null);
            var latest = new ArtifactRevisionResponse(Latest, Artifact, 2, Submitted, "LATEST DRAFT CONTENT", "latest-hash", "Draft", "Naomi Chen", now, null, null);
            var summary = new ArtifactDocumentSummary(Artifact, Organization, "Game design document", "game-design", "InReview", null, null,
                Latest, Submitted, null, 2, "Naomi Chen", false, null, now, now, null);
            Detail = new(summary, latest, null, [latest, submitted], [], []);
            Item = new(Artifact, ApprovalDashboardKinds.Artifact, "Artifact: Game design document", "Version 2 Document.",
                "Pending", "Naomi Chen", "CEO", now, null, $"/organizations/{Organization}/documents?artifact={Artifact}", true)
                { Artifact = new(Artifact, Submitted) };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal($"/api/organizations/{Organization:D}/documents/{Artifact:D}/decisions", path);
                var decision = (await request.Content!.ReadFromJsonAsync<DecideArtifactRevisionRequest>(token))!;
                Decisions.Add(decision);
                var accepted = decision.Decision == "accept";
                Detail = Detail with { Document = Detail.Document with { SubmittedRevisionId = null,
                    Status = accepted ? "Approved" : "ChangesRequested", AcceptedRevisionId = accepted ? Submitted : null },
                    Revisions = Detail.Revisions.Select(x => x.Id == Submitted ? x with { Status = accepted ? "Accepted" : "Rejected" } : x).ToList() };
                return Json(Detail);
            }
            if (path == $"/api/organizations/{Organization:D}/documents/{Artifact:D}")
                return ReadError is { } error ? new(error) : Json(Detail);
            if (path.EndsWith("/approvals")) return Json(new ApprovalDashboardResponse(Guid.NewGuid(), 1, [Item]));
            return Json(Array.Empty<object>());
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
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Document review must stay on the current page.");
    }
}
