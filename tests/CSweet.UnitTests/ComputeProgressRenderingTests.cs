using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Compute;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class ComputeProgressRenderingTests
{
    [Fact]
    public async Task DownloadShowsPercentageTotalSpeedAndScopedEta()
    {
        var html = await Render(new("Running", "Downloading", DateTimeOffset.UtcNow, 2_000_000_000, null,
            4_000_000_000, 2_000_000, 1000, PreparationStep: 1));
        Assert.Contains("50%", html); Assert.Contains("2.00 GB of 4.00 GB", html);
        Assert.Contains("2.0 MB/s", html); Assert.Contains("About 17 minutes remaining", html);
        Assert.Contains("Time remaining is for the download", html);
        Assert.Contains("aria-label=\"Linux image download progress\"", html);
    }

    [Fact]
    public async Task StallsDoNotKeepAStaleCountdownAndImageBuildShowsItsOwnDuration()
    {
        var stalled = await Render(new("Running", "Downloading", DateTimeOffset.UtcNow, 2_000_000_000, null,
            4_000_000_000, DownloadWaitingForProgress: true, PreparationStep: 1));
        Assert.Contains("Waiting for download progress", stalled);
        Assert.DoesNotContain("minutes remaining", stalled);
        var building = await Render(new("Running", "Installing Ubuntu", DateTimeOffset.UtcNow, null, null, PreparationStep: 2));
        Assert.Contains("Step 2 of 3", building); Assert.Contains("Expected duration: 10–30 minutes", building);
        Assert.DoesNotContain("Linux image download progress", building);
    }

    private static async Task<string> Render(ComputeSetupStatus status)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton(new HttpClient(new Handler(status)) { BaseAddress = new("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CSweet.UI.Pages.Compute>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CSweet.UI.Pages.Compute.OrganizationId)] = Guid.NewGuid() }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
    private sealed class Handler(ComputeSetupStatus status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ComputeDashboard(DateTimeOffset.UtcNow, status, [], false)) });
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
