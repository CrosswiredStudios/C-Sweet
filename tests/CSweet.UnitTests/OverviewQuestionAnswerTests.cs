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

namespace CSweet.UnitTests;

public sealed class OverviewQuestionAnswerTests
{
    [Fact]
    public async Task InstalledAvatarAndMenuLinkBelongToTheRequestingAgent()
    {
        await using var fixture = new Fixture();
        var output = await fixture.RenderAsync();
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains("https://example.com/requesting-agent.jpg", html);
        Assert.Contains("Custom agent name", html);
        Assert.Contains("Creative Director", html);
        Assert.Contains("Which direction?", html);
        await fixture.OpenMenuAsync();
        var other = Assert.Single(fixture.Activator.Items, x => x.Href is not null);
        Assert.Equal($"/organizations/{fixture.Api.Organization}/communications/{fixture.Api.Question.ConversationId}", other.Href);
        Assert.Empty(fixture.Api.Answers);
        Assert.Empty(fixture.Navigation.Destinations);
        Assert.Equal(2, fixture.Activator.Items.Count(x => x.Href is null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChoosingAnOptionAnswersInlineAndRetriesUseTheSameKey(bool failFirst, bool staleRead)
    {
        await using var fixture = new Fixture();
        fixture.Api.FailFirst = failFirst;
        fixture.Api.ReturnStaleQuestions = staleRead;
        var output = await fixture.RenderAsync();
        await fixture.OpenMenuAsync();
        var option = fixture.Activator.Items.First(x => x.Href is null);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => option.OnClick.InvokeAsync(new MouseEventArgs()));
        if (failFirst)
        {
            var failed = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
            Assert.Contains("Try again", failed);
            Assert.Contains("Which direction?", failed);
            await fixture.Renderer.Dispatcher.InvokeAsync(() => option.OnClick.InvokeAsync(new MouseEventArgs()));
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
        await fixture.Renderer.Dispatcher.InvokeAsync(() => option.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(answerCount, fixture.Api.Answers.Count);
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
            var services = new ServiceCollection().AddLogging(); services.AddMudServices();
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
                builder.AddAttribute(3, "SummaryOnly", true); builder.CloseComponent();
            };
            return await Renderer.RenderComponentAsync<EmployeePortraitScope>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = Api.Organization, ["ChildContent"] = content }));
        });
        public Task OpenMenuAsync() => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            // HtmlRenderer does not run the browser lifecycle that mounts popovers.
            // Render the actual menu content to exercise its bound callbacks and links.
            await Renderer.RenderComponentAsync<CascadingValue<MudMenu>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = Activator.Menu, ["IsFixed"] = true, ["ChildContent"] = Activator.Menu!.ChildContent }));
        });
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public MudMenu? Menu { get; private set; }
        public List<MudMenuItem> Items { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is MudMenu menu) Menu = menu;
            if (component is MudMenuItem item) Items.Add(item);
            return component;
        }
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        private readonly Guid _employee = Guid.NewGuid(), _installation = Guid.NewGuid();
        public PendingAgentQuestionResponse Question { get; }
        public List<AnswerExecutiveDecisionRequest> Answers { get; } = [];
        public List<string> AnswerUris { get; } = [];
        public bool FailFirst { get; set; }
        public bool ReturnStaleQuestions { get; set; }
        private bool _answered;
        public ApiHandler() => Question = new(Guid.NewGuid(), _employee, "Custom agent name",
            new(Guid.NewGuid(), "Which direction?", "Pending", [new("a", "Proceed", "Keep moving", true), new("b", "Wait", null, false)], "a", null, null, DateTimeOffset.UtcNow, null));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
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
