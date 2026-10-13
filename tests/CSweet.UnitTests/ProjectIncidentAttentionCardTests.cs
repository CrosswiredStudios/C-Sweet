using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class ProjectIncidentAttentionCardTests
{
    [Theory]
    [InlineData("Failed", true, "Retry")]
    [InlineData("Blocked", true, "Retry")]
    [InlineData("Active", false, "In progress")]
    [InlineData("Failed", false, null)]
    public async Task ShowsCurrentActionsAndDeepLink(string status, bool canRetry, string? action)
    {
        await using var f = new Fixture(status, canRetry);
        var html = await f.Render();
        Assert.Contains("Investigate", html);
        Assert.Contains($"collaboration={f.Incident.Collaboration!.SessionId}", html);
        Assert.Contains("Provider connection interrupted", html);
        if (action is not null) Assert.Contains(action, html);
        else Assert.DoesNotContain(">Retry<", html);
        if (status == "Active") Assert.True(f.Buttons.Single(x => x.Href is null).Disabled);
        Assert.Equal(0, f.Handler.Posts);
    }

    [Fact]
    public async Task RetryPreservesRevisionAndIdempotencyAndPreventsRepeatedSubmission()
    {
        await using var f = new Fixture("Failed", true);
        await f.Render();
        await f.ClickRetry(); await f.ClickRetry();
        Assert.Equal(1, f.Handler.Posts);
        var c = f.Incident.Collaboration!;
        Assert.Equal(c.Revision, f.Handler.Request!.ExpectedRevision);
        Assert.Equal($"ui-retry:{c.SessionId:N}:{c.Revision}", f.Handler.Request.IdempotencyKey);
        Assert.EndsWith($"/chats/{c.ConversationId}/coordination-sessions/{c.SessionId}/retry", f.Handler.Path);
        Assert.Contains("Retry queued", await f.Html());
        Assert.Contains("recovery is confirmed", await f.Html());
        Assert.Equal(1, f.Refreshes);
        Assert.True(f.Buttons.Single(x => x.Href is null).Disabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "collaboration changed")]
    [InlineData(HttpStatusCode.Forbidden, "no longer have permission")]
    [InlineData(HttpStatusCode.InternalServerError, "Could not retry")]
    public async Task FailedRetryKeepsInvestigationAndHidesRawServerErrors(HttpStatusCode code, string message)
    {
        await using var f = new Fixture("Failed", true);
        f.Handler.Status = code;
        await f.Render(); await f.ClickRetry();
        var html = await f.Html();
        Assert.Contains(message, html); Assert.Contains("Investigate", html);
        Assert.DoesNotContain("private stack trace", html); Assert.DoesNotContain("Retry queued", html);
        Assert.Equal(1, f.Refreshes);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root;
        private readonly CaptureComponents capture = new();
        public Handler Handler { get; } = new();
        public int Refreshes { get; private set; }
        public List<MudButton> Buttons => capture.Buttons;
        public ProjectIncidentAttentionResponse Incident { get; }
        public Fixture(string status, bool canRetry)
        {
            var org = Guid.NewGuid(); var session = Guid.NewGuid(); var chat = Guid.NewGuid();
            Incident = new(Guid.NewGuid(), Guid.NewGuid(), "Game", "Naomi", "Planning needs attention",
                "Provider connection interrupted", "Review the failed step", "/report", DateTimeOffset.UtcNow)
            { Collaboration = new(session, chat, 7, status, canRetry,
                $"/organizations/{org}/communications/{chat}?perspective={Guid.NewGuid()}&collaboration={session}") };
            var services = new ServiceCollection().AddLogging(); services.AddMudServices();
            services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
            services.AddSingleton<IComponentActivator>(capture);
            services.AddSingleton(new HttpClient(Handler) { BaseAddress = new Uri("http://localhost/") });
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = await renderer.RenderComponentAsync<ProjectIncidentAttentionCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["OrganizationId"] = Guid.NewGuid(), ["Incident"] = Incident,
                ["RefreshRequested"] = EventCallback.Factory.Create(this, () => Refreshes++)
            }));
            return root.ToHtmlString();
        });
        public Task ClickRetry() => renderer.Dispatcher.InvokeAsync(() => Buttons.Single(x => x.Href is null).OnClick.InvokeAsync(new MouseEventArgs()));
        public Task<string> Html() => renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await provider.DisposeAsync(); }
    }
    private sealed class CaptureComponents : IComponentActivator
    {
        public List<MudButton> Buttons { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is MudButton button) Buttons.Add(button);
            return component;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string? Path { get; private set; }
        public RetryAgentCoordinationRequest? Request { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Posts++;
            Path = request.RequestUri!.AbsolutePath;
            Request = await request.Content!.ReadFromJsonAsync<RetryAgentCoordinationRequest>(token);
            return new(Status) { Content = new StringContent("private stack trace") };
        }
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
