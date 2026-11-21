using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Agents;
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

public sealed class OverviewQuestionAnswerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenEndedQuestionAnswersInlineAndKeepsItsKeyOnRetry(bool failFirst)
    {
        await using var fixture = new Fixture();
        fixture.Api.Question = fixture.Api.Question with { Decision = fixture.Api.Question.Decision with { Options = [], RecommendedOptionId = "" } };
        fixture.Api.FailFirst = failFirst;
        fixture.Api.ReturnStaleQuestions = true;
        var output = await fixture.RenderAsync();
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("<textarea", html);
        Assert.Contains("rows=\"4\"", html);
        Assert.Contains("decision-submit", html);
        Assert.DoesNotContain("Choose an answer", html);
        Assert.DoesNotContain("Something Else", html);
        var submit = Assert.Single(fixture.Activator.Buttons, x => x.Variant == Variant.Filled);
        Assert.True(submit.Disabled);
        var card = fixture.Activator.Card!;
        var direction = typeof(AgentQuestionCard).GetField("_direction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var render = typeof(ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => { direction.SetValue(card, "Explore a cozy world\nPC, no combat"); render.Invoke(card, null); });
        Assert.False(submit.Disabled);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => submit.OnClick.InvokeAsync(new MouseEventArgs()));
        if (failFirst)
        {
            Assert.Contains("Try again", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
            await fixture.Renderer.Dispatcher.InvokeAsync(() => submit.OnClick.InvokeAsync(new MouseEventArgs()));
            Assert.Equal(fixture.Api.Answers[0].IdempotencyKey, fixture.Api.Answers[1].IdempotencyKey);
        }
        Assert.Contains("Nothing needs your attention", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        Assert.All(fixture.Api.Answers, answer => { Assert.Null(answer.OptionId); Assert.Equal("Explore a cozy world\nPC, no combat", answer.SomethingElse); });
        Assert.Empty(fixture.Navigation.Destinations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverviewQuestionUsesMarkdownAndLinksToTheExactDecision(bool grouped)
    {
        await using var fixture = new Fixture();
        fixture.Api.Grouped = grouped;
        fixture.Api.Question = fixture.Api.Question with { Decision = fixture.Api.Question.Decision with {
            Prompt = "**Choose the direction**\n\n- Keep the approved scope\n- Plan independent QA\n\n<script>unsafe()</script>" } };
        var output = await fixture.RenderAsync();
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("decision-preview-text", html);
        Assert.Contains("<strong>Choose the direction</strong>", html);
        Assert.Contains("<li>Keep the approved scope</li>", html);
        Assert.Contains($"/communications/{fixture.Api.Question.ConversationId}?decision={fixture.Api.Question.Decision.Id}", html);
        Assert.Contains("Read the full question in Communications", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Empty(fixture.Api.Answers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewShowsTheRequestingAgentAndOneDropdownBeforeSelection(bool grouped)
    {
        await using var fixture = new Fixture();
        fixture.Api.Grouped = grouped;
        var output = await fixture.RenderAsync();
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("https://example.com/requesting-agent.jpg", html);
        Assert.Contains("Custom agent name", html);
        Assert.Contains("Creative Director", html);
        Assert.Contains("Which direction?", html);
        Assert.Contains("<select", html);
        Assert.Contains("Choose an answer", html);
        Assert.DoesNotContain("option-detail", html);
        Assert.DoesNotContain("Work can start today.", html);
        Assert.DoesNotContain("type=\"radio\"", html);
        Assert.Contains("Ask a follow-up", html);
        Assert.Contains("Give different direction", html);
        Assert.DoesNotContain("Something Else", html);
        Assert.Empty(fixture.Api.Answers);
        Assert.Empty(fixture.Navigation.Destinations);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task ChoosingAnOptionAnswersInlineAndRetriesUseTheSameKey(bool failFirst, bool staleRead, bool grouped)
    {
        await using var fixture = new Fixture();
        fixture.Api.Grouped = grouped;
        fixture.Api.FailFirst = failFirst;
        fixture.Api.ReturnStaleQuestions = staleRead;
        var output = await fixture.RenderAsync();
        var submit = Assert.Single(fixture.Activator.Buttons, button => button.Variant == Variant.Filled);
        Assert.True(submit.Disabled);
        var choices = fixture.Activator.Choices!;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => choices.SelectedChanged.InvokeAsync("a"));
        Assert.Empty(fixture.Api.Answers);
        Assert.False(submit.Disabled);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => submit.OnClick.InvokeAsync(new MouseEventArgs()));
        if (failFirst)
        {
            var failed = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
            Assert.Contains("Try again", failed);
            Assert.Contains("Which direction?", failed);
            await fixture.Renderer.Dispatcher.InvokeAsync(() => submit.OnClick.InvokeAsync(new MouseEventArgs()));
            Assert.Equal(fixture.Api.Answers[0].IdempotencyKey, fixture.Api.Answers[1].IdempotencyKey);
        }
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.DoesNotContain("Response sent", html);
        Assert.Contains("Nothing needs your attention", html);
        Assert.DoesNotContain("Which direction?", html);
        Assert.All(fixture.Api.Answers, answer => { Assert.Equal("a", answer.OptionId); Assert.Null(answer.SomethingElse); });
        Assert.All(fixture.Api.AnswerUris, uri => Assert.EndsWith($"/hub/chats/{fixture.Api.Question.ConversationId}/decisions/{fixture.Api.Question.Decision.Id}/respond", uri));
        Assert.Empty(fixture.Navigation.Destinations);
        var answerCount = fixture.Api.Answers.Count;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => submit.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(answerCount, fixture.Api.Answers.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AskingAboutImpactKeepsDecisionPendingWithoutSelectingAnOption(bool allowFreeText)
    {
        await using var fixture = new Fixture();
        fixture.Api.Question = fixture.Api.Question with { Decision = fixture.Api.Question.Decision with { AllowFreeText = allowFreeText } };
        var output = await fixture.RenderAsync();
        var followUp = Assert.Single(fixture.Activator.Help, help => help.Option is null);
        var toggle = Assert.Single(fixture.Activator.Buttons, button => button.Class == "ask-follow-up");
        await fixture.Renderer.Dispatcher.InvokeAsync(() => toggle.OnClick.InvokeAsync(new MouseEventArgs()));
        await fixture.RenderDialogAsync();
        var ask = Assert.Single(fixture.Activator.Buttons, b => b.Variant == Variant.Outlined);
        Assert.True(ask.Disabled);
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(DecisionExplanation).GetField("_question", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(followUp, "Does this approve spending?");
            typeof(ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(followUp, null);
        });
        await fixture.Renderer.Dispatcher.InvokeAsync(() => ask.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(new DecisionQuestionRequest("Does this approve spending?", null), Assert.Single(fixture.Api.Questions));
        Assert.Empty(fixture.Api.Answers);
        Assert.Null(fixture.Activator.Choices!.Selected);
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("It needs separate approval.", await fixture.RenderDialogAsync());
        Assert.Contains("Which direction?", html);
        Assert.DoesNotContain("Work can start today.", html);
    }

    [Fact]
    public async Task SwitchingOptionsShowsOnlyTheCurrentImpactAndReusesTheLegacyExplanation()
    {
        await using var fixture = new Fixture();
        var output = await fixture.RenderAsync();
        var choices = fixture.Activator.Choices!;
        async Task<string> Select(string id) => await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            await choices.SelectedChanged.InvokeAsync(id);
            return output.ToHtmlString();
        });

        var saved = await Select("a");
        Assert.Contains("Keep moving", saved);
        Assert.Contains("Work can start today.", saved);
        Assert.Empty(fixture.Api.Questions);

        var legacy = await Select("b");
        Assert.Contains("It needs separate approval.", legacy);
        Assert.DoesNotContain("Work can start today.", legacy);
        Assert.DoesNotContain("It needs separate approval.", await Select("a"));
        Assert.Contains("It needs separate approval.", await Select("b"));
        Assert.Equal("b", Assert.Single(fixture.Api.Questions).OptionId);
        Assert.Empty(fixture.Api.Answers);
    }

    [Fact]
    public async Task LateLegacyExplanationDoesNotReplaceTheNewSelection()
    {
        await using var fixture = new Fixture();
        var output = await fixture.RenderAsync();
        var choices = fixture.Activator.Choices!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.ExplanationDelay = release.Task;
        var loading = fixture.Renderer.Dispatcher.InvokeAsync(() => choices.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(DecisionChoices.Selected)] = "b" })));
        try
        {
            Assert.Contains("Getting explanation", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
            await fixture.Renderer.Dispatcher.InvokeAsync(() => choices.SelectedChanged.InvokeAsync("a"));
        }
        finally { release.TrySetResult(); }
        await loading;
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("Work can start today.", html);
        Assert.DoesNotContain("It needs separate approval.", html);
        Assert.Equal("b", Assert.Single(fixture.Api.Questions).OptionId);
        Assert.Empty(fixture.Api.Answers);
    }

    [Fact]
    public async Task FollowUpsRetainPreviousAnswersInTheDialogAndPreserveTheSelectedOption()
    {
        await using var fixture = new Fixture();
        var output = await fixture.RenderAsync();
        var choices = fixture.Activator.Choices!;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => choices.SelectedChanged.InvokeAsync("a"));
        var help = Assert.Single(fixture.Activator.Help);
        var toggle = Assert.Single(fixture.Activator.Buttons, b => b.Class == "ask-follow-up");
        await fixture.Renderer.Dispatcher.InvokeAsync(() => toggle.OnClick.InvokeAsync(new MouseEventArgs()));
        foreach (var question in new[] { "When can we start?", "Do we need approval?" })
        {
            await fixture.RenderDialogAsync();
            var ask = fixture.Activator.Buttons.Last(b => b.Variant == Variant.Outlined);
            await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
            {
                typeof(DecisionExplanation).GetField("_question", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(help, question);
                await ask.OnClick.InvokeAsync(new MouseEventArgs());
            });
        }
        var dialog = await fixture.RenderDialogAsync();
        Assert.Contains("When can we start?", dialog);
        Assert.Contains("Do we need approval?", dialog);
        Assert.Equal(2, dialog.Split("It needs separate approval.").Length - 1);
        Assert.Equal(2, fixture.Api.Questions.Count);
        Assert.All(fixture.Api.Questions, q => Assert.Equal("a", q.OptionId));
        Assert.DoesNotContain("It needs separate approval.", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        Assert.Equal("a", choices.Selected);
        Assert.Empty(fixture.Api.Answers);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public ApiHandler Api { get; } = new();
        public CapturingActivator Activator { get; } = new();
        public TestNavigation Navigation { get; } = new();
        public HtmlRenderer Renderer { get; }
        public Fixture()
        {
            var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
            services.AddSingleton<IComponentActivator>(Activator);
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<NavigationManager>(Navigation);
            services.AddSingleton(new HttpClient(Api) { BaseAddress = new Uri("http://localhost/") });
            services.AddSingleton<AppRealtimeState>();
            services.AddScoped<IAgentApiClient, AgentApiClient>();
            _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderAsync() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<MudPopoverProvider>(0); builder.CloseComponent();
                builder.OpenComponent<PendingAgentQuestions>(1);
                builder.AddAttribute(2, "OrganizationId", Api.Organization);
                builder.AddAttribute(3, "SummaryOnly", true);
                if (Api.Grouped) builder.AddAttribute(4, "Issues", new OverviewIssueDirectory([
                    new("question:" + Api.Question.Decision.Id, null, null, "Business work", "Review a project decision",
                        "Your direction is needed", Api.Question.AgentName, "Respond", true, false, [], [Api.Question], [], Api.Question.Decision.Prompt)]));
                builder.CloseComponent();
            };
            return await Renderer.RenderComponentAsync<EmployeePortraitScope>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = Api.Organization, ["ChildContent"] = content }));
        });
        // Static HTML rendering omits MudDialog's interactive portal. Render its real content and actions.
        public Task<string> RenderDialogAsync() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var dialog = Assert.IsType<MudDialog>(Activator.Dialog);
            RenderFragment body = builder => { builder.AddContent(0, dialog.DialogContent); builder.AddContent(1, dialog.DialogActions); };
            var content = await Renderer.RenderComponentAsync<MudPaper>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(MudPaper.ChildContent)] = body }));
            return content.ToHtmlString();
        });
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public MudDialog? Dialog { get; private set; }
        public DecisionChoices? Choices { get; private set; }
        public List<DecisionExplanation> Help { get; } = [];
        public AgentQuestionCard? Card { get; private set; }
        public List<MudButton> Buttons { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MudDialog dialog) Dialog = dialog;
            if (component is DecisionChoices choices) Choices = choices;
            if (component is DecisionExplanation followUp) Help.Add(followUp);
            if (component is AgentQuestionCard card) Card = card;
            if (component is MudButton button) Buttons.Add(button);
            return component;
        }
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        private readonly Guid _employee = Guid.NewGuid(), _installation = Guid.NewGuid();
        public PendingAgentQuestionResponse Question { get; set; }
        public List<AnswerExecutiveDecisionRequest> Answers { get; } = [];
        public List<DecisionQuestionRequest> Questions { get; } = [];
        public List<string> AnswerUris { get; } = [];
        public Task ExplanationDelay { get; set; } = Task.CompletedTask;
        public bool FailFirst { get; set; }
        public bool Grouped { get; set; }
        public bool ReturnStaleQuestions { get; set; }
        private bool _answered;
        public ApiHandler() => Question = new(Guid.NewGuid(), _employee, "Custom agent name",
            new(Guid.NewGuid(), "Which direction?", "Pending", [new("a", "Proceed", "Keep moving", true) { Impact = "Work can start today." }, new("b", "Wait", null, false)], "a", null, null, DateTimeOffset.UtcNow, null));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith($"/chats/{Question.ConversationId}/decisions/{Question.Decision.Id}/explain"))
            {
                Questions.Add((await request.Content!.ReadFromJsonAsync<DecisionQuestionRequest>(token))!);
                await ExplanationDelay.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DecisionExplanationResponse("It needs separate approval.")) };
            }
            if (request.Method == HttpMethod.Post)
            {
                Answers.Add((await request.Content!.ReadFromJsonAsync<AnswerExecutiveDecisionRequest>(token))!);
                AnswerUris.Add(path);
                if (FailFirst && Answers.Count == 1) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new AnswerExecutiveDecisionResponse(false, "unavailable", "Try again")) };
                _answered = true;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AnswerExecutiveDecisionResponse(true, null, "Answered")) };
            }
            object result = path.EndsWith("/users")
                ? new[] { new OrganizationUserResponse(_employee, Organization, null, null, null, "Custom agent name", null, 1, 0, DateTimeOffset.UtcNow) { AgentInstallationId = _installation } }
                : path.EndsWith("/installations")
                    ? new[] { JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"Id":"{{_installation}}","AgentId":"com.csweet.creative-director","ImageUrl":"https://example.com/requesting-agent.jpg","RoleName":"Creative Director"}""")! }
                    : _answered && !ReturnStaleQuestions ? Array.Empty<PendingAgentQuestionResponse>() : new[] { Question };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public List<string> Destinations { get; } = [];
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Destinations.Add(uri);
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
