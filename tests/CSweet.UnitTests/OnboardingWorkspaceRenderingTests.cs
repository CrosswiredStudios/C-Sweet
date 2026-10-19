using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CSweet.UI.Layout;
using CSweet.UI.Services;
using CSweet.UI.Setup;
using CSweet.Contracts.Setup;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using MudBlazor;
using SetupPage = CSweet.UI.Pages.Setup;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class OnboardingWorkspaceRenderingTests
{
    [Theory]
    [InlineData("llm-provider", "editor", "Base URL")]
    [InlineData("llm-provider", "error", "Test connection failed")]
    [InlineData("llm-provider", "delete", "Keep provider")]
    [InlineData("genai-provider", "editor", "Test connection")]
    [InlineData("genai-provider", "operations", "Approved operations")]
    [InlineData("genai-provider", "delete", "approved operations")]
    public async Task ProviderFormsRetainConnectionAndManagementControls(string step, string scenario, string expected)
    {
        var html = await Render(new SetupApi(step), "aurora", providerScenario: scenario);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        if (Environment.GetEnvironmentVariable("CSWEET_SETUP_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"setup-{step}-{scenario}.html"), html);
    }
    [Theory]
    [InlineData("custom", "Custom allocation")]
    [InlineData("created", "Continue to Windows approval")]
    [InlineData("missing-handoff", "The secure request cannot be reopened")]
    [InlineData("stalled", "Try Windows setup again")]
    [InlineData("redeemed", "Installing Office")]
    [InlineData("recoveryrequired", "Reassign this Office")]
    [InlineData("blocked-reuse", "Reassignment is blocked")]
    [InlineData("removal-failed", "Retry removal")]
    [InlineData("removalinprogress", "Installing Office")]
    [InlineData("failed", "Start again")]
    [InlineData("expired", "Start again")]
    [InlineData("revoked", "Start again")]
    [InlineData("connected", "Finishing connection")]
    [InlineData("ready-unhealthy", "fresh authenticated health check")]
    [InlineData("ready", "Your Office is ready")]
    [InlineData("remote", "Generate a new code")]
    [InlineData("remote-approval", "Approve host")]
    [InlineData("linux", "sudo /usr/sbin/csweet-configure-office")]
    [InlineData("unavailable", "Assisted local setup is unavailable")]
    [InlineData("load-error", "Try again")]
    public async Task AssistantPreservesOfficeRecoveryAndReadinessControls(string scenario, string expected)
    {
        var api = new SetupApi("agent-execution");
        var html = WebUtility.HtmlDecode(await Render(api, "aurora", officeScenario: scenario));
        Assert.Contains(expected, html);
        if (scenario is "blocked-reuse" or "removal-failed") Assert.DoesNotContain("Reassign this Office", html);
        if (scenario == "recoveryrequired") Assert.Contains("Remove Office", html);
        if (scenario == "remote-approval") Assert.Contains("Not my host", html);
        if (scenario == "ready-unhealthy") Assert.DoesNotContain("Setup complete</span>", html);
        if (scenario is "failed" or "expired" or "revoked")
        {
            Assert.Contains("Diagnostic details", html);
            Assert.Contains("Setup reference:", html);
            Assert.DoesNotContain("Estimating time remaining", html);
            Assert.DoesNotContain("% complete", html);
        }
        if (scenario == "failed")
        {
            Assert.Contains("developer-bootstrap-failed", html);
            Assert.Contains("dotnet-sdk-present", html);
        }
        Assert.DoesNotContain(api.Paths, path => path.EndsWith("/recovery") || path.EndsWith("/launch"));
        if (Environment.GetEnvironmentVariable("CSWEET_SETUP_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"setup-office-{scenario}.html"), html);
    }

    [Theory]
    [InlineData("welcome", "aurora", "Start setup")]
    [InlineData("welcome", "gold", "Start setup")]
    [InlineData("agent-execution", "aurora", "Where should your agents work?")]
    [InlineData("agent-execution", "gold", "Where should your agents work?")]
    [InlineData("llm-provider", "aurora", "Connect your model providers.")]
    [InlineData("llm-provider", "gold", "Connect your model providers.")]
    [InlineData("personal-assistant", "aurora", "Make your assistant yours.")]
    [InlineData("personal-assistant", "gold", "Make your assistant yours.")]
    [InlineData("genai-provider", "aurora", "Finish without media")]
    [InlineData("genai-provider", "gold", "Finish without media")]
    public async Task FiveStepFlowRendersInBothThemes(string step, string theme, string expected)
    {
        var api = new SetupApi(step);
        var html = await Render(api, theme);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        Assert.Contains($"data-appearance=\"{theme}\"", html);
        Assert.Contains("onboarding-workspace", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "class=\"setup-progress-node\"").Count);
        Assert.DoesNotContain(api.Paths, path => path.Contains("communications") || path.Contains("email-delivery"));
        if (step == "llm-provider")
        {
            Assert.DoesNotContain("assistant-model-settings", html);
            Assert.DoesNotContain("/api/personal-assistant/settings", api.Paths);
        }
        if (step == "personal-assistant")
        {
            Assert.Contains("Assistant name", html);
            Assert.Contains("256000", html);
            Assert.Contains("128000", html);
            Assert.Contains("Save and continue", html);
        }
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

    private static async Task<string> Render(SetupApi api, string theme, bool finish = false, string? officeScenario = null, string? providerScenario = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        var activator = new CapturingActivator();
        services.AddSingleton<IComponentActivator>(activator);
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
            if (officeScenario is not null)
            {
                SeedOffice(activator.Office!, officeScenario);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Office, null);
                await Task.Yield();
            }
            if (finish)
            {
                await (Task)typeof(SetupPage).GetMethod("FinishSetupAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
                await Task.Yield();
            }
            if (providerScenario is not null)
            {
                var component = activator.Provider!;
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                if (providerScenario == "operations")
                    component.GetType().GetField("_editingProviderId", flags)!.SetValue(component, Guid.NewGuid());
                if (providerScenario == "error")
                {
                    component.GetType().GetField("_catalogMessage", flags)!.SetValue(component, "Test connection failed. Check the endpoint and credentials.");
                    component.GetType().GetField("_catalogSucceeded", flags)!.SetValue(component, false);
                }
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(component, null);
                await Task.Yield();
                // HtmlRenderer does not run OnAfterRender (MudDialog's portal activation).
                // Render the actual dialog fragment to review its conditional form content.
                var dialog = activator.Dialogs[providerScenario == "delete" ? 1 : 0];
                var preview = await renderer.RenderComponentAsync<DialogPreview>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(DialogPreview.Content)] = dialog.DialogContent }));
                return rendered.ToHtmlString() + preview.ToHtmlString();
            }
            return rendered.ToHtmlString();
        });
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public AgentHostOnboardingStep? Office { get; private set; }
        public ComponentBase? Provider { get; private set; }
        public List<MudDialog> Dialogs { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var instance = (IComponent)Activator.CreateInstance(componentType)!;
            if (instance is AgentHostOnboardingStep office) Office = office;
            if (instance is LlmProviderSetupStep or GenAiProviderSetupStep) Provider = (ComponentBase)instance;
            if (instance is MudDialog dialog) Dialogs.Add(dialog);
            return instance;
        }
    }

    private sealed class DialogPreview : ComponentBase
    {
        [Parameter] public RenderFragment? Content { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "mud-dialog-container mud-dialog-center");
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "class", "mud-dialog mud-dialog-width-md");
            builder.OpenElement(4, "div");
            builder.AddAttribute(5, "class", "mud-dialog-content");
            builder.AddContent(6, Content);
            builder.CloseElement(); builder.CloseElement(); builder.CloseElement();
        }
    }

    private static void SeedOffice(AgentHostOnboardingStep office, string scenario)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object? value) => office.GetType().GetField(name, flags)!.SetValue(office, value);
        var status = (ExecutionCapacityOnboardingResponse)office.GetType().GetField("_status", flags)!.GetValue(office)!;
        if (scenario == "custom")
        {
            office.GetType().GetMethod("SelectCapacityPreset", flags)!.Invoke(office, ["custom"]);
            return;
        }
        if (scenario == "load-error") { Set("_error", "Connection interrupted. Please try again."); return; }
        if (scenario == "unavailable") { Set("_status", status with { LocalCapacity = null }); return; }
        if (scenario is "remote" or "remote-approval" or "linux")
        {
            Set("_installationTarget", scenario == "linux" ? "local" : "remote");
            Set("_remotePlatform", scenario == "linux" ? "linux" : "windows");
            Set("_status", status with {
                LocalOperatingSystem = scenario == "linux" ? "linux" : "windows",
                Packages = new("https://example.test/office-release.json", null, null, null, "https://example.test/gateway"),
                ActiveEnrollment = scenario == "remote" ? new(Guid.NewGuid(), Guid.NewGuid(), "available", DateTimeOffset.UtcNow.AddMinutes(10)) : null,
                Nodes = scenario == "remote-approval" ? [new(Guid.NewGuid(), Guid.NewGuid(), "Office", "Workstation", "windows", "x64", "1.0", "1", "pendingapproval", "TEST-FINGERPRINT", null, 4, 8192, 32768, 1, null, [], new Dictionary<string,string>())] : []
            });
            return;
        }
        var state = scenario switch {
            "missing-handoff" or "stalled" => "created",
            "blocked-reuse" or "removal-failed" => "recoveryrequired",
            "ready-unhealthy" => "ready",
            _ => scenario
        };
        var session = new LocalOfficeSetupSessionResponse(Guid.NewGuid(), state, "install", "Installing Office",
            "Preparing the machine and verifying its connection.", 35, DateTimeOffset.UtcNow.AddMinutes(10), 4, 8192, 32768, 1, null,
            scenario == "missing-handoff" ? null : "csweet-office://test",
            ErrorCode: scenario == "removal-failed" ? "office_removal_failed" : scenario == "failed" ? "developer-bootstrap-failed" : null,
            ErrorMessage: scenario == "failed" ? "The guest reported a failed isolation check: dotnet-sdk-present" : null,
            LaunchMethod: "server", RecoveryCanReconnect: scenario == "recoveryrequired");
        Set("_localSession", session);
        Set("_status", status with { IsReady = scenario == "ready" });
        if (scenario == "stalled")
        {
            Set("_localApprovalRequested", true);
            Set("_localLaunchAttempts", 1);
            Set("_localLaunchAttemptedAt", DateTimeOffset.UtcNow.AddMinutes(-2));
        }
    }

    [Fact]
    public async Task StartAgainIgnoresRediscoveryOfDismissedFailureButAcceptsNewSession()
    {
        var component = TestLocalization.Inject(new AgentHostOnboardingStep());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(AgentHostOnboardingStep).GetField("_localSession", flags)!;
        var adopt = typeof(AgentHostOnboardingStep).GetMethod("AdoptLocalSession", flags)!;
        var reset = typeof(AgentHostOnboardingStep).GetMethod("ResetLocalSetupAsync", flags)!;
        var failed = new LocalOfficeSetupSessionResponse(Guid.NewGuid(), "failed", "install", "Failed",
            "Office setup failed.", 0, DateTimeOffset.UtcNow, 4, 8192, 32768, 1, null, null);
        adopt.Invoke(component, [failed]);
        await (Task)reset.Invoke(component, [])!;
        adopt.Invoke(component, [failed]);
        Assert.Null(field.GetValue(component));
        var next = failed with { Id = Guid.NewGuid(), State = "created" };
        adopt.Invoke(component, [next]);
        Assert.Equal(next, field.GetValue(component));
    }

    private sealed class SetupApi(string currentStep) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool FailFinish { get; init; }
        private static readonly string[] Keys = ["welcome", "agent-execution", "llm-provider", "personal-assistant", "genai-provider"];
        private object Status(bool complete = false) => new {
            isFirstRunComplete = complete,
            steps = Keys.Select((key, i) => new { key, displayName = key, isRequired = i < 4, isComplete = i < Array.IndexOf(Keys, currentStep) })
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            object? body = path switch {
                "/api/llm-provider-profiles" when currentStep == "personal-assistant" => new[] { new CSweet.Contracts.Llm.LlmProviderProfileResponse(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Local vLLM", CSweet.Domain.Setup.LlmProviderType.Vllm, "http://localhost:8000", null, "qwen3.8-27b", null, 256000, 128000, true, true, true, false, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) },
                "/api/personal-assistant/settings" => new CSweet.Contracts.PersonalAssistant.PersonalAssistantSettings(null, null, null, 0, false, null, [], new Dictionary<string, System.Text.Json.JsonElement>()),
                "/api/setup/status" => Status(),
                "/api/setup/complete" => new { succeeded = !FailFinish, message = "Please try again.", status = Status(!FailFinish) },
                "/api/setup/steps/genai-provider/complete" => new { succeeded = true, status = Status() },
                _ => null
            };
            string? json = path switch {
                "/api/llm-provider-profiles" or "/api/genai-provider-profiles" => "[]",
                _ when path.Contains("00000000-0000-0000-0000-000000000001") => """{"models":[],"succeeded":true}""",
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
