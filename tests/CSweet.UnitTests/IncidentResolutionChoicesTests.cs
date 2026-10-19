using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class IncidentResolutionChoicesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoiceAndCustomAnswerStayInlineAndKeepTheirRetryKey(bool custom)
    {
        await using var f = new Fixture();
        var root = await f.Render(); await f.OpenMenu();
        if (custom)
        {
            await f.Renderer.Dispatcher.InvokeAsync(() => f.Capture.Items.Last().OnClick.InvokeAsync(new MouseEventArgs()));
            Assert.Contains("Your blocker resolution", await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            await f.Renderer.Dispatcher.InvokeAsync(() =>
            {
                typeof(IncidentResolutionChoices).GetField("_direction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Capture.Choices, "Collect the audio contract details first.");
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Capture.Choices, null);
            });
        }
        async Task Submit() => await f.Renderer.Dispatcher.InvokeAsync(() => custom
            ? f.Capture.Buttons.Last().OnClick.InvokeAsync(new MouseEventArgs())
            : f.Capture.Items.First().OnClick.InvokeAsync(new MouseEventArgs()));
        f.Handler.Fail = true; await Submit();
        Assert.Contains("Try again", await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        f.Handler.Fail = false; await Submit(); await Submit();
        Assert.Equal(2, f.Handler.Requests.Count); Assert.Equal(f.Handler.Requests[0].IdempotencyKey, f.Handler.Requests[1].IdempotencyKey);
        Assert.Equal(custom ? null : "reconcile", f.Handler.Requests[1].OptionId);
        Assert.Equal(custom ? "Collect the audio contract details first." : null, f.Handler.Requests[1].SomethingElse);
        Assert.Equal(1, f.Answers);
        Assert.Contains("Fix requested", await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    [Fact]
    public async Task IncidentCardUsesReporterIdentityAndFullReportWithoutDuplicatingTheQuestionCount()
    {
        await using var f = new Fixture(); var employee = Guid.NewGuid();
        var report = new ProjectIncidentAttentionResponse(f.Decision.IncidentId!.Value, Guid.NewGuid(), "Prism Break", "Same employee name",
            "Scope reconciliation is blocked", "Old facts", "Next step", "/report", DateTimeOffset.UtcNow)
            { ReporterOrganizationUserId = employee, OwnerReport = "The authority decisions are recorded. Update the accepted brief and collect the missing logistics facts.",
                ResolutionDecision = f.Decision, ReportConversationId = f.Chat };
        var root = await f.Renderer.Dispatcher.InvokeAsync(() => f.Renderer.RenderComponentAsync<ProjectIncidentAttentionCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = f.Org, ["Incident"] = report })));
        var html = await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains(report.OwnerReport, html); Assert.DoesNotContain("Old facts", html);
        Assert.Equal(employee, Assert.Single(f.Capture.Avatars).EmployeeId);
        var questions = new[] { new PendingAgentQuestionResponse(f.Chat, employee, report.ReporterName, f.Decision) };
        Assert.Empty(OverviewAttentionItems.StandaloneQuestions(questions, [report]));
    }

    [Theory]
    [InlineData("Outdated")]
    [InlineData("Superseded")]
    public async Task OutdatedChoicesDoNotRenderAnActionMenu(string status)
    {
        await using var f = new Fixture(); f.Decision = f.Decision with { Status = status };
        var root = await f.Render(); var html = await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("blocker changed", html); Assert.Null(f.Capture.Menu); Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task ClosedResolutionRequestNeedsNoResponse()
    {
        await using var f = new Fixture(); f.Decision = f.Decision with { Status = "Cancelled" };
        var root = await f.Render(); var html = await f.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("No response is needed", html); Assert.Null(f.Capture.Menu); Assert.Empty(f.Handler.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        public Guid Org { get; } = Guid.NewGuid(); public Guid Chat { get; } = Guid.NewGuid();
        public ExecutiveDecisionCardResponse Decision { get; set; } = new(Guid.NewGuid(), "How should I help resolve this blocker?", "Pending",
            [new("reconcile", "Reconcile the accepted brief", "Coordinate its update", true), new("facts", "Collect facts", "Ask for missing logistics", false)],
            "reconcile", null, null, DateTimeOffset.UtcNow, null) { IncidentId = Guid.NewGuid(), IncidentReportRevision = 1 };
        public Capturing Capture { get; } = new(); public Handler Handler { get; } = new(); public HtmlRenderer Renderer { get; }
        public int Answers;
        public Fixture()
        {
            var collection = new ServiceCollection().AddLogging(); collection.AddMudServices(); collection.AddCSweetLocalization();
            collection.AddSingleton<IComponentActivator>(Capture); collection.AddSingleton<IJSRuntime, NoJs>(); collection.AddSingleton<NavigationManager, Navigation>();
            collection.AddSingleton(new HttpClient(Handler) { BaseAddress = new("http://localhost/") });
            services = collection.BuildServiceProvider(); Renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> Render() => Renderer.Dispatcher.InvokeAsync(() =>
            Renderer.RenderComponentAsync<IncidentResolutionChoices>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["OrganizationId"] = Org, ["ConversationId"] = Chat, ["Decision"] = Decision,
                ["Answered"] = EventCallback.Factory.Create(this, () => Answers++) })));
        public Task OpenMenu() => Renderer.Dispatcher.InvokeAsync(async () =>
            await Renderer.RenderComponentAsync<CascadingValue<MudMenu>>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["Value"] = Capture.Menu, ["IsFixed"] = true, ["ChildContent"] = Capture.Menu!.ChildContent })));
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await services.DisposeAsync(); }
    }
    private sealed class Capturing : IComponentActivator
    {
        public IncidentResolutionChoices? Choices; public MudMenu? Menu;
        public List<MudMenuItem> Items { get; } = []; public List<MudButton> Buttons { get; } = []; public List<PersonAvatarContent> Avatars { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var instance = (IComponent)Activator.CreateInstance(type)!;
            if (instance is IncidentResolutionChoices c) Choices = c;
            if (instance is MudMenu m) Menu = m;
            if (instance is MudMenuItem i) Items.Add(i);
            if (instance is MudButton b) Buttons.Add(b);
            if (instance is PersonAvatarContent a) Avatars.Add(a);
            return instance;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Fail; public List<AnswerExecutiveDecisionRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((await request.Content!.ReadFromJsonAsync<AnswerExecutiveDecisionRequest>(token))!);
            return new(Fail ? HttpStatusCode.Conflict : HttpStatusCode.OK) { Content = JsonContent.Create(new AnswerExecutiveDecisionResponse(!Fail, null,
                Fail ? "Try again" : "Submitted")) };
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Resolution answers must remain inline."); }
    private sealed class NoJs : IJSRuntime { public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!); public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!); }
}
