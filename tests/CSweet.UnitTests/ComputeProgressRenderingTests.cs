using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Compute;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using CSweet.UI.Localization;

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
        var building = await Render(new("Running", "Installing Ubuntu", DateTimeOffset.UtcNow, null, null, PreparationStep: 2) { Health = "Running" });
        Assert.Contains("Step 3 of 6", building); Assert.Contains("Expected duration: 10–30 minutes", building);
        Assert.DoesNotContain("Linux image download progress", building);
    }

    [Fact]
    public async Task QuietHealthyInstallerIsNotShownAsDisconnectedOrFailed()
    {
        var html = await Render(new("Running", "Installing Ubuntu", DateTimeOffset.UtcNow.AddMinutes(-8), null, null, InstallerRunning: true)
        {
            Health = "Running", Milestone = 3, Phase = "build-guest", PhaseStartedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            InstallerCheckedAt = DateTimeOffset.UtcNow
        });
        Assert.Contains("Process running", html);
        Assert.Contains("quiet steps do not by themselves indicate failure", html);
        Assert.DoesNotContain("Status unknown", html);
        Assert.DoesNotContain("Retry preparation", html);
    }

    [Fact]
    public async Task UnknownStatusOffersCheckWithoutRetryAndFailureKeepsActivity()
    {
        var unknown = await Render(new("Running", "Installing Ubuntu", DateTimeOffset.UtcNow.AddMinutes(-8), null, null));
        Assert.Contains("Status unknown", unknown);
        Assert.Contains("Check status", unknown);
        Assert.DoesNotContain("Retry preparation", unknown);
        var failed = await Render(new("Failed", "Verifying image", DateTimeOffset.UtcNow, null, "image_export_failed")
        {
            Health = "Stopped", CanRetry = true, Milestone = 4,
            Activity = [new("build-guest", "Installing Ubuntu", DateTimeOffset.UtcNow.AddMinutes(-5))]
        }, canManage: true);
        Assert.Contains("Retry preparation", failed);
        Assert.Contains("Preparation activity", failed);
        Assert.Contains("image_export_failed", failed);
        Assert.DoesNotContain("Windows</h3>", failed);
    }

    [Fact]
    public async Task ConfigurationFailureExplainsTheRepairAndReusableImage()
    {
        var html = await Render(new("Failed", "Installing the service", DateTimeOffset.UtcNow, null, "setup_configuration_invalid")
        {
            Health = "Stopped", CanRetry = true, Milestone = 5
        }, canManage: true);
        Assert.Contains("could not read its setup configuration", html);
        Assert.Contains("Update C-Sweet and retry preparation", html);
        Assert.Contains("completed Linux image can be reused", html);
        Assert.Contains("Retry preparation", html);
        Assert.DoesNotContain("resolve any host setup issue", html);
    }

    [Fact]
    public async Task OlderGenericFailureStillExplainsWhichStepFailed()
    {
        var html = await Render(new("Failed", "Installing the service", DateTimeOffset.UtcNow, null, "local_setup_failed")
        {
            Health = "Stopped", CanRetry = true, Milestone = 5
        }, canManage: true);
        Assert.Contains("Linux image is ready, but compute service setup failed", html);
        Assert.Contains("reuse the completed image", html);
    }

    [Fact]
    public async Task OnlyOwnerGetsSetupAndCreationActions()
    {
        Assert.DoesNotContain("Create instance", await Render(new("Ready", "Ready", null, null, null)));
        Assert.Contains("Create instance", await Render(new("Ready", "Ready", null, null, null), canManage: true));
        Assert.Contains("Set up Linux", await Render(null, canManage: true));
        Assert.Contains("A business owner can set up", await Render(null));
    }

    [Fact]
    public async Task AlreadyCompletedPreparationStartsCollapsed()
    {
        var html = await Render(new("Ready", "Ready", null, null, null));
        Assert.Contains("View preparation", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.DoesNotContain("Hide preparation", html);
    }

    [Fact]
    public async Task CompletionCollapsesAfterTwoSecondsAndNewAttemptExpandsAgain()
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        CSweet.UI.Components.ComputePreparation? instance = null;
        var attemptId = Guid.NewGuid();
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(CapturePreparation.Capture)] = (Action<CSweet.UI.Components.ComputePreparation>)(value => instance = value),
            [nameof(CSweet.UI.Components.ComputePreparation.Setup)] = new ComputeSetupStatus("Running", "Installing", DateTimeOffset.UtcNow, null, null)
                { Health = "Running", AttemptId = attemptId, Milestone = 3 }
        });
        var component = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CapturePreparation>(parameters));
        await renderer.Dispatcher.InvokeAsync(() => instance!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(CSweet.UI.Components.ComputePreparation.Setup)] = new ComputeSetupStatus("Ready", "Ready", null, null, null)
                { Health = "Ready", AttemptId = attemptId }
        })));
        Assert.Contains("Hide preparation", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));
        await Task.Delay(2300);
        Assert.Contains("View preparation", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));
        await renderer.Dispatcher.InvokeAsync(() => instance!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(CSweet.UI.Components.ComputePreparation.Setup)] = new ComputeSetupStatus("Running", "Installing", DateTimeOffset.UtcNow, null, null)
                { Health = "Running", AttemptId = Guid.NewGuid(), Milestone = 3 }
        })));
        Assert.Contains("Hide preparation", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));
    }

    public sealed class CapturePreparation : CSweet.UI.Components.ComputePreparation
    {
        [Parameter] public Action<CSweet.UI.Components.ComputePreparation>? Capture { get; set; }
        protected override void OnInitialized() { Capture?.Invoke(this); base.OnInitialized(); }
    }

    private static async Task<string> Render(ComputeSetupStatus? status, bool canManage = false)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton(new HttpClient(new Handler(status, canManage)) { BaseAddress = new("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CSweet.UI.Pages.Compute>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CSweet.UI.Pages.Compute.OrganizationId)] = Guid.NewGuid() }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
    private sealed class Handler(ComputeSetupStatus? status, bool canManage) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ComputeDashboard(DateTimeOffset.UtcNow, status, [], false, canManage)) });
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
