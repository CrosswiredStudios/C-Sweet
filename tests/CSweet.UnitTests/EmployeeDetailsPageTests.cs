using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Analytics;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class EmployeeDetailsPageTests
{
    [Fact]
    public async Task PersonalBoardTabParameterSelectsTheBoardTab()
    {
        var html = await RenderAsync("personal-board");

        Assert.Contains("mud-tab-panel-active", html);
        Assert.Contains("class=\"personal-board\"", html);
        Assert.DoesNotContain("Direct reports", html);
    }

    [Fact]
    public async Task MissingOrUnknownTabParameterFallsBackToOverview()
    {
        var missing = await RenderAsync(null);
        Assert.Contains("Direct reports", missing);
        Assert.DoesNotContain("class=\"personal-board\"", missing);

        var unknown = await RenderAsync("does-not-exist");
        Assert.Contains("Direct reports", unknown);
        Assert.DoesNotContain("class=\"personal-board\"", unknown);
    }

    private static async Task<string> RenderAsync(string? tab)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(new EmployeeApi()) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<IAgentApiClient, AgentApiClient>();
        services.AddSingleton<IComponentActivator>(new PageActivator(tab));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<EmployeeDetails>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EmployeeDetails.OrganizationId)] = EmployeeApi.OrganizationId,
                [nameof(EmployeeDetails.EmployeeId)] = EmployeeApi.EmployeeId
            }));
            return component.ToHtmlString();
        });
    }

    // [SupplyParameterFromQuery] values arrive through framework-internal cascading suppliers that
    // static renderers do not register, so the query state is seeded at construction the same way
    // the router would seed it before the page's first render.
    private sealed class PageActivator(string? tab) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType) =>
            componentType == typeof(EmployeeDetails)
                ? new EmployeeDetails { Tab = tab }
                : (IComponent)Activator.CreateInstance(componentType)!;
    }

    private sealed class EmployeeApi : HttpMessageHandler
    {
        public static readonly Guid OrganizationId = Guid.NewGuid();
        public static readonly Guid EmployeeId = Guid.NewGuid();
        public static readonly Guid BoardId = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object result;
            if (path.EndsWith("/details"))
                result = new EmployeeDetailsResponse(
                    new OrganizationUserResponse(EmployeeId, OrganizationId, null, null, null, "Ada Sterling", null, 0, 1, DateTimeOffset.UtcNow),
                    null, null, null, [], [],
                    new EmployeeDetailsPermissions(true, true, true, true, false, false));
            else if (path.EndsWith("/users"))
                result = Array.Empty<OrganizationUserResponse>();
            else if (path.EndsWith("/roles"))
                result = Array.Empty<RoleResponse>();
            else if (path.EndsWith("/personal-board"))
                result = new Wire.PersonalTodoBoard(BoardId, EmployeeId, "Ada Sterling", null, null, 1, []);
            else if (path.EndsWith("/assignments"))
                result = new EmployeeAssignedWorkResponse(EmployeeId, []);
            else if (path.EndsWith("/usage"))
                result = new InferenceAnalyticsResponse("24h", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new InferenceAnalyticsTotalsResponse(0, 0, 0, 0), []);
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) });
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
