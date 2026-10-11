using System.Net;
using System.Net.Http.Json;
using CSweet.Agent.SDK;
using CSweet.UI.Components.Hiring;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class AgentHireDialogRenderingTests
{
    [Fact]
    public async Task OpeningFromAnotherComponentRendersBeforeReadsCompleteAndShowsFailures()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new HttpClient(new Handler(entered, release)) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<AgentHireOperationState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AgentHireDialog>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AgentHireDialog.OrganizationId)] = Guid.NewGuid()
            })));
        var dialog = Assert.IsType<MudDialog>(activator.Dialog);
        Assert.False(dialog.GetState(component => component.Visible));
        var agent = new AvailableAgent("chief", "chief", AgentCatalogSource.LocalDirectory, [],
            AgentAvailabilityState.AvailableToInstall, null, "Evelyn Brooks", "Chief", "C-Sweet", "Management",
            [], [], [], null, null, null, 0, null, null, 1, "FirstParty");
        var opening = renderer.Dispatcher.InvokeAsync(() => activator.Review!.OpenAsync(agent));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await renderer.Dispatcher.InvokeAsync(() => Assert.True(dialog.GetState(component => component.Visible)));
        }
        finally
        {
            release.TrySetResult();
        }
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        // The final render must also propagate errors into the dialog content,
        // even though OpenAsync was invoked without a component event callback.
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var content = await renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(FragmentHost.ChildContent)] = dialog.DialogContent }));
            Assert.Contains("Preview unavailable", content.ToHtmlString());
        });
    }

    public sealed class FragmentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public AgentHireDialog? Review { get; private set; }
        public MudDialog? Dialog { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is AgentHireDialog review) Review = review;
            if (component is MudDialog dialog) Dialog = dialog;
            return component;
        }
    }

    private sealed class Handler(TaskCompletionSource entered, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/users"))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            if (request.Method == HttpMethod.Post)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = "Preview unavailable" }) };
            var json = request.RequestUri.AbsolutePath.Contains("/teams") ? "{\"teams\":[]}" : "[]";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
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
