using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class CompanyDashboardRealtimeTests
{
    [Fact]
    public async Task EventsDuringLoadingAreCoalescedAndReconnectRecoversCurrentState()
    {
        var org = Guid.NewGuid();
        var handler = new Handler();
        await using var fixture = new Fixture(handler);
        handler.OnFirstHiringRead = () =>
        {
            fixture.Publish(org);
            fixture.Publish(org);
        };
        var output = await fixture.RenderAsync(org);
        Assert.Equal(2, handler.HiringReads);
        fixture.Publish(Guid.NewGuid());
        Assert.Equal(2, handler.HiringReads);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Publish(org));
        Assert.Equal(3, handler.HiringReads);
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            ((Action?)typeof(AppRealtimeState).GetField("Reconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Realtime))?.Invoke());
        Assert.Equal(4, handler.HiringReads);
        Assert.Contains("Suggestion 4", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Activator.Page!.Dispose());
        fixture.Publish(org);
        Assert.Equal(4, handler.HiringReads);
    }

    [Fact]
    public async Task OrganizationSwitchCancelsOldReadsAndDoesNotApplyTheirResults()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { FirstHiringRead = async () => { entered.SetResult(); await release.Task; } };
        await using var fixture = new Fixture(handler);
        var render = fixture.RenderAsync(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Activator.Page!.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CommandCenter.OrganizationId)] = second })));
        release.SetResult();
        var output = await render.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains($"Company {second:D}", html);
        Assert.Contains("Suggestion 2", html);
        Assert.DoesNotContain("Suggestion 1", html);
        Assert.True(handler.FirstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ExpansionControlsShowAllAndCollapseEachGroupIndependently()
    {
        var handler = new Handler { ItemCount = 6 };
        await using var fixture = new Fixture(handler);
        var output = await fixture.RenderAsync(Guid.NewGuid());
        Assert.DoesNotContain(">Plan 5<", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        var buttons = fixture.Activator.Buttons.Where(x => x.UserAttributes?.ContainsKey("aria-expanded") == true).ToArray();
        Assert.Equal(2, buttons.Length);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => buttons[0].OnClick.InvokeAsync(new MouseEventArgs()));
        var html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.Contains(">Plan 5<", html);
        Assert.DoesNotContain(">Suggestion 5<", html);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => buttons[1].OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Contains(">Suggestion 5<", await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => buttons[0].OnClick.InvokeAsync(new MouseEventArgs()));
        html = await fixture.Renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        Assert.DoesNotContain(">Plan 5<", html);
        Assert.Contains(">Suggestion 5<", html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public CapturingActivator Activator { get; } = new();
        public AppRealtimeState Realtime { get; }
        public HtmlRenderer Renderer { get; }
        public Fixture(Handler handler)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddMudServices();
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<NavigationManager, TestNavigation>();
            services.AddSingleton<IComponentActivator>(Activator);
            services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
            services.AddScoped<AppRealtimeState>();
            services.AddScoped<IAgentApiClient, AgentApiClient>();
            _services = services.BuildServiceProvider();
            Realtime = _services.GetRequiredService<AppRealtimeState>();
            Renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderAsync(Guid org) =>
            Renderer.Dispatcher.InvokeAsync(() => Renderer.RenderComponentAsync<CommandCenter>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CommandCenter.OrganizationId)] = org })));
        public void Publish(Guid org) => typeof(AppRealtimeState).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Realtime, [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, AppRealtimeEvents.HiringRecommendationsChanged,
                org, "hiring", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }))]);
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        public CommandCenter? Page { get; private set; }
        public List<MudButton> Buttons { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is CommandCenter page) Page = page;
            if (component is MudButton button) Buttons.Add(button);
            return component;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Action? OnFirstHiringRead { get; set; }
        public Func<Task>? FirstHiringRead { get; set; }
        public CancellationToken FirstToken { get; private set; }
        public int HiringReads { get; private set; }
        public int ItemCount { get; set; } = 1;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/dashboard/hiring"))
            {
                var read = ++HiringReads;
                if (read == 1)
                {
                    FirstToken = token;
                    OnFirstHiringRead?.Invoke();
                    if (FirstHiringRead is not null) await FirstHiringRead();
                }
                result = new HiringBacklogResponse(Enumerable.Range(0, ItemCount).Select(i =>
                    new HiringRecommendationResponse(Guid.NewGuid(), null, $"Suggestion {(ItemCount == 1 ? read : i)}", "Objective",
                        "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                    { RemainingHeadcount = 1, HiringUrl = "/marketplace" }).ToArray());
            }
            else if (path.EndsWith("/dashboard/layout")) result = new DashboardLayoutRequest(DashboardWidgets.DefaultOrder);
            else if (path.EndsWith("/dashboard")) result = new CompanyDashboardResponse(new([], null), new([], null));
            else if (path.EndsWith("/dashboard/activity")) result = new CurrentActivityPage(DateTimeOffset.UtcNow, 0, [], null);
            else if (path.EndsWith("/users") || path.EndsWith("/installations")) result = Array.Empty<object>();
            else if (path.EndsWith("/inspection")) result = new ProjectPortfolioResponse(DateTimeOffset.UtcNow, 0, 0, []);
            else if (path.EndsWith("/questions/pending")) result = Array.Empty<CSweet.Contracts.Communications.PendingAgentQuestionResponse>();
            else if (path.EndsWith("/approvals")) result = new ApprovalDashboardResponse(Guid.NewGuid(), ItemCount,
                Enumerable.Range(0, ItemCount).Select(i => new ApprovalDashboardItemResponse(Guid.NewGuid(), ApprovalDashboardKinds.ResourceChange,
                    $"Plan {i}", "Summary", "Pending", "Producer", "Owner", DateTimeOffset.UtcNow, null, "/approvals", true)).ToArray());
            else result = new { name = $"Company {path.Split('/').Last()}" };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
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
