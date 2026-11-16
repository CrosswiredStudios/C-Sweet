using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Localization;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class AttentionNotificationCardTests
{
    private static readonly Guid Agent = Guid.NewGuid(), Installation = Guid.NewGuid(), Organization = Guid.NewGuid();
    private static PendingAgentQuestionResponse Question(string prompt, bool freeText = false) => new(Guid.NewGuid(), Agent, "Evelyn Brooks",
        new(Guid.NewGuid(), prompt, "Pending", freeText ? [] : [new("review", "Review each hire", "Choose candidates before hiring", true)],
            freeText ? "" : "review", null, null, DateTimeOffset.UtcNow, null));
    private static OverviewIssueCard Group(params PendingAgentQuestionResponse[] questions) => new("case:scope", Guid.NewGuid(), Guid.NewGuid(),
        "Prism Break", "Confirm the next step", "The team needs your direction before continuing this work.", "Evelyn Brooks",
        "Your response returns to the accountable agent.", true, false, [], questions, [], "Complete supporting evidence stays available.") { OwnerOrganizationUserId = Agent };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionableQuestionRetainsMarkdownPortraitAndPromptBesideItsControls(bool grouped)
    {
        await using var fixture = new Fixture();
        var question = Question("**How should hiring approvals work?**\n\nChoose how involved you would like to be.");
        var item = grouped ? Group(question) : Group(question) with { IssueId = null };
        var html = await fixture.RenderAsync(item);
        Assert.Contains("<strong>How should hiring approvals work?</strong>", html);
        Assert.Contains("_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg", html);
        Assert.Contains("Chief of Staff", html);
        Assert.Contains("decision-preview-text", html);
        Assert.Contains("Choose an answer", html);
        Assert.DoesNotContain("rows=\"3\"", html);
        if (grouped) Assert.True(html.IndexOf("decision-preview-text", StringComparison.Ordinal) < html.IndexOf("<details class=\"issue-details\"", StringComparison.Ordinal));
        else Assert.DoesNotContain("Confirm the next step", html);
    }

    [Fact]
    public async Task GroupKeepsEachQuestionVisibleAndDoesNotRepeatAnIncidentDecision()
    {
        await using var fixture = new Fixture();
        var first = Question("How should hiring approvals work?");
        var second = Question("What would you like the game to feel like?", true);
        var incident = new ProjectIncidentAttentionResponse(Guid.NewGuid(), Guid.NewGuid(), "Prism Break", "Evelyn Brooks", "A direction is needed",
            "A retained question owns the next step.", "Choose a response", "/report", DateTimeOffset.UtcNow) {
            ReporterOrganizationUserId = Agent, ResolutionDecision = first.Decision, ReportConversationId = first.ConversationId };
        var item = Group(first, second, first) with { Incidents = [incident] };
        var html = await fixture.RenderAsync(item);
        var visible = html[..html.IndexOf("<details class=\"issue-details\"", StringComparison.Ordinal)];
        Assert.Contains(first.Decision.Prompt, visible); Assert.Contains(second.Decision.Prompt, visible);
        Assert.Equal(1, visible.Split("Choose an answer").Length - 1);
        Assert.Equal(1, visible.Split("Send response").Length - 1);
        Assert.Contains("rows=\"4\"", visible);
        Assert.DoesNotContain("class=\"incident-resolution\"", visible);
        Assert.Contains("embedded-request", visible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryUsesItsOwnPresentationAndStatusNeverAddsRetryAuthority(bool actionable)
    {
        await using var fixture = new Fixture();
        var work = new BlockedWorkAttentionResponse(Guid.NewGuid(), Agent, "Evelyn Brooks", "Manager", "Resume the retained request", "Current access needs review", 3, true, "/work");
        var item = Group() with { IssueId = null, Work = [work], RequiresUserAction = actionable };
        var html = await fixture.RenderAsync(item);
        Assert.Equal(actionable, html.Contains("Retry after review", StringComparison.Ordinal));
        Assert.Equal(actionable, html.Contains("Open affected work", StringComparison.Ordinal));
        Assert.Contains("_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg", html);
        if (!actionable) Assert.Contains("No action needed from you.", html);
    }

    [Fact]
    public async Task UnknownTypeHasAnInformationalFallbackWithoutRenderingArbitraryPayloadOrActions()
    {
        await using var fixture = new Fixture();
        var html = await fixture.RenderAsync(new { Kind = "Approve", Payload = "private-provider-payload" });
        Assert.Contains("Review this notification", html);
        Assert.DoesNotContain("private-provider-payload", html);
        Assert.DoesNotContain("Choose an answer", html);
    }

    [Fact]
    public async Task ANewQuestionInTheSameMountedNotificationDoesNotInheritThePreviousDraft()
    {
        await using var fixture = new Fixture();
        await fixture.RenderAsync(Question("What is the initial direction?", true));
        await fixture.ReplaceQuestionAsync(Question("What should happen next?", true));
    }

    [Fact]
    public async Task OptionalVisualPreviewUsesTheActualTypedComponents()
    {
        await using var fixture = new Fixture();
        var choice = Question("How involved would you like to be in hiring?");
        var direction = Question("What kind of game would you like to make?\n\nShare the player experience, target platform and any constraints.", true);
        var status = Group() with { Key = "team", Title = "The team is preparing your project", Summary = "Your approved direction is recorded. The producer is checking the current roster.", NextAction = "The producer owns the next step.", RequiresUserAction = false };
        var items = new object[] { Group(choice) with { IssueId = null }, direction, Group(Question("Should we proceed with the proposed approach?")), status };
        var rendered = new List<string>();
        foreach (var item in items) rendered.Add(await fixture.RenderAsync(item));
        Assert.All(rendered, html => Assert.Contains("attention-request", html));
        if (Environment.GetEnvironmentVariable("CSWEET_UI_PREVIEW_DIRECTORY") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "cards-fragment.html"), string.Join("\n", rendered));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly HtmlRenderer renderer;
        private readonly Capture components = new();
        private readonly EmployeePortraits portraits = EmployeePortraits.Build(
            [new(Agent, Organization, null, null, null, "Evelyn Brooks", null, 1, 0, DateTimeOffset.UtcNow) { AgentInstallationId = Installation }],
            [JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"Id":"{{Installation}}","AgentId":"external.chief","ImageUrl":"_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg","RoleName":"Chief of Staff"}""")!], []);
        public Fixture()
        {
            var collection = new ServiceCollection().AddLogging(); collection.AddMudServices(); collection.AddCSweetLocalization();
            collection.AddSingleton<IJSRuntime, NoJavaScript>(); collection.AddSingleton<NavigationManager, TestNavigation>();
            collection.AddSingleton(new HttpClient { BaseAddress = new("http://localhost/") });
            collection.AddSingleton<IComponentActivator>(components);
            services = collection.BuildServiceProvider(); renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> RenderAsync(object item) => renderer.Dispatcher.InvokeAsync(async () => {
            RenderFragment content = builder => { builder.OpenComponent<AttentionNotificationCard>(0); builder.AddAttribute(1, "OrganizationId", Organization);
                builder.AddAttribute(2, "Notification", item); builder.CloseComponent(); };
            return (await renderer.RenderComponentAsync<CascadingValue<EmployeePortraits>>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Value"] = portraits, ["ChildContent"] = content }))).ToHtmlString();
        });
        public async Task ReplaceQuestionAsync(PendingAgentQuestionResponse next)
        {
            var prior = components.Question!;
            await renderer.Dispatcher.InvokeAsync(async () => {
                typeof(AgentQuestionCard).GetField("_direction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(prior, "Previous question's private draft");
                await components.Router!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Notification"] = next }));
            });
            Assert.NotSame(prior, components.Question);
            Assert.Equal(next.Decision.Id, components.Question!.Question.Decision.Id);
        }
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator
    {
        public AttentionNotificationCard? Router;
        public AgentQuestionCard? Question;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is AttentionNotificationCard router) Router ??= router;
            if (component is AgentQuestionCard question) Question = question;
            return component;
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
