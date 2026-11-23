using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Setup;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class HostedOfficeOverviewTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 2, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(1, 1, 2)]
    [InlineData(-1, 0, 0)]
    public async Task All_plans_show_both_sources_and_the_dashboard_action(int linked, int standard, int team)
    {
        var plan = linked < 0 ? null : new HostedOfficePlan(linked, standard, team, true);
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, Js>();
        services.AddSingleton<NavigationManager, Nav>();
        services.AddSingleton(new HttpClient(new Inventory(plan)) { BaseAddress = new Uri("https://tenant.test/") });
        services.AddScoped<ISetupApiClient, SetupApiClient>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<HostedOffices>()).ToHtmlString());
        Assert.Contains("Hosted Offices", html);
        Assert.Contains("Your devices", html);
        Assert.Contains("Add hosted Offices", html);
        Assert.Contains("Connect a device", html);
        Assert.Contains("Continue to dashboard", html);
        if (standard > 0) Assert.Contains($"{standard} purchased", html);
        if (linked < 0)
        {
            Assert.Contains("temporarily unavailable", html);
            Assert.DoesNotContain("No hosted Offices on this plan", html);
        }
    }

    private sealed class Inventory(HostedOfficePlan? plan) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = request.RequestUri!.AbsolutePath switch
            {
                "/api/setup/hosted-offices" => JsonContent.Create(new HostedOfficeOverview(plan, plan is null ? "unavailable" : null, "https://account.test/order", false)),
                "/api/setup/execution-capacity" => JsonContent.Create(new ExecutionCapacityOnboardingResponse("remote", false, 0, 0, false, "Linux", "amd64", "", null, [], [])),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
            };
            return Task.FromResult(response);
        }
    }
    private sealed class Nav : NavigationManager
    {
        public Nav() => Initialize("https://tenant.test/", "https://tenant.test/offices");
    }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
