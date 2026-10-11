using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CSweet.UI.Layout;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SetupPage = CSweet.UI.Pages.Setup;

namespace CSweet.UnitTests;

public sealed class OnboardingWorkspaceRenderingTests
{
    [Theory]
    [InlineData("welcome", "aurora", "Start setup")]
    [InlineData("welcome", "gold", "Start setup")]
    [InlineData("agent-execution", "aurora", "Where should your agents work?")]
    [InlineData("agent-execution", "gold", "Where should your agents work?")]
    [InlineData("llm-provider", "aurora", "Connect your model providers.")]
    [InlineData("llm-provider", "gold", "Connect your model providers.")]
    [InlineData("genai-provider", "aurora", "Finish without media")]
    [InlineData("genai-provider", "gold", "Finish without media")]
    public async Task FourStepFlowRendersInBothThemes(string step, string theme, string expected)
    {
        var api = new SetupApi(step);
        var html = await Render(api, theme);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        Assert.Contains($"data-appearance=\"{theme}\"", html);
        Assert.Contains("onboarding-workspace", html);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "class=\"setup-progress-node\"").Count);
        Assert.DoesNotContain(api.Paths, path => path.Contains("communications") || path.Contains("email-delivery"));
        if (Environment.GetEnvironmentVariable("CSWEET_SETUP_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"setup-{step}-{theme}.html"), html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishUsesFinalMediaStepAndSurfacesRetryableFailure(bool failure)
    {
        var api = new SetupApi("genai-provider") { FailFinish = failure };
        var html = await Render(api, "aurora", finish: true);
        Assert.Contains("/api/setup/steps/genai-provider/complete", api.Paths);
        Assert.Contains("/api/setup/complete", api.Paths);
        Assert.DoesNotContain(api.Paths, path => path.Contains("communications") || path.Contains("email-delivery"));
        Assert.Contains(failure ? "Please try again." : "Your workspace is ready.", WebUtility.HtmlDecode(html));
    }

    private static async Task<string> Render(SetupApi api, string theme, bool finish = false)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<AppearanceState>().SetThemeAsync(theme);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            SetupPage? page = null;
            RenderFragment body = builder => {
                builder.OpenComponent<SetupPage>(0);
                builder.AddComponentReferenceCapture(1, value => page = (SetupPage)value);
                builder.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<SetupLayout>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SetupLayout.Body)] = body }));
            if (finish)
            {
                await (Task)typeof(SetupPage).GetMethod("FinishSetupAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
                await Task.Yield();
            }
            return rendered.ToHtmlString();
        });
    }

    private sealed class SetupApi(string currentStep) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool FailFinish { get; init; }
        private static readonly string[] Keys = ["welcome", "agent-execution", "llm-provider", "genai-provider"];
        private object Status(bool complete = false) => new {
            isFirstRunComplete = complete,
            steps = Keys.Select((key, i) => new { key, displayName = key, isRequired = i < 3, isComplete = i < Array.IndexOf(Keys, currentStep) })
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            object? body = path switch {
                "/api/setup/status" => Status(),
                "/api/setup/complete" => new { succeeded = !FailFinish, message = "Please try again.", status = Status(!FailFinish) },
                "/api/setup/steps/genai-provider/complete" => new { succeeded = true, status = Status() },
                _ => null
            };
            string? json = path switch {
                "/api/llm-provider-profiles" or "/api/genai-provider-profiles" => "[]",
                "/api/llm-provider-profiles/discover-local" or "/api/genai-provider-profiles/discover-local" => """{"profiles":[],"results":[]}""",
                "/api/setup/execution-capacity" => """{"selectedMode":"local","isReady":false,"canInstallLocalNode":true,"localOperatingSystem":"windows","localArchitecture":"x64","summary":"Choose capacity","nodes":[],"checks":[],"localPrerequisites":[],"localCapacity":{"isSupported":true,"totalCpuCount":16,"totalMemoryMb":32768,"freeDiskMb":524288,"safeCpuCount":12,"safeMemoryMb":24576,"safeDiskMb":262144,"minimumCpuCount":2,"minimumMemoryMb":4096,"minimumDiskMb":32768,"presets":[{"key":"small","displayName":"Small","percentOfSafeCapacity":25,"cpuCount":3,"memoryMb":6144,"diskMb":65536},{"key":"performance","displayName":"Performance","percentOfSafeCapacity":75,"cpuCount":9,"memoryMb":18432,"diskMb":196608},{"key":"balanced","displayName":"Balanced","percentOfSafeCapacity":50,"cpuCount":6,"memoryMb":12288,"diskMb":131072}]}}""",
                _ => null
            };
            return Task.FromResult(new HttpResponseMessage(body is null && json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = body is not null ? JsonContent.Create(body) : new StringContent(json ?? "{}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/setup");
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).ToString(); }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
