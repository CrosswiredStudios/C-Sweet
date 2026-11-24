using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CSweet.Contracts.Plugins;
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangeFromSummaryReopensOriginalViewOnlyAfterServerCancellation(bool accepted)
    {
        var api = new SetupApi("agent-execution");
        var html = WebUtility.HtmlDecode(await Render(api, "aurora", officeScenario: "created", officeAction: async office =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var status = (ExecutionCapacityOnboardingResponse)typeof(AgentHostOnboardingStep).GetField("_status", flags)!.GetValue(office)!;
            api.CancelResult = new(accepted, accepted ? null : "setup_cannot_cancel", "Office setup changed. Try again.", null, status);
            await (Task)typeof(AgentHostOnboardingStep).GetMethod("ChangeLocalSetupAsync", flags)!.Invoke(office, null)!;
        }));
        Assert.Single(api.Paths, path => path.EndsWith("/cancel"));
        if (accepted)
        {
            Assert.Contains("Install C-Sweet Office here", html);
            Assert.Contains("Create an Office", html);
            Assert.DoesNotContain("Continue to Windows approval", html);
            Assert.DoesNotContain("host-target-summary", html);
        }
        else
        {
            Assert.Contains("Office setup changed. Try again.", html);
            Assert.Contains("Continue to Windows approval", html);
        }
    }

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
    [InlineData("storage", "Office installation drive")]
    [InlineData("storage-full", "Choose an Office installation drive")]
    [InlineData("custom", "Custom allocation")]
    [InlineData("created", "Continue to Windows approval")]
    [InlineData("missing-handoff", "The secure request cannot be reopened")]
    [InlineData("stalled", "Try Windows setup again")]
    [InlineData("redeemed", "Installing Office")]
    [InlineData("time-range", "About 15s\u201345s remaining")]
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
    [InlineData("macos", "sudo /usr/local/sbin/csweet-configure-office")]
    [InlineData("linux", "sudo /usr/sbin/csweet-configure-office")]
    [InlineData("unavailable", "Assisted local setup is unavailable")]
    [InlineData("load-error", "Try again")]
    public async Task AssistantPreservesOfficeRecoveryAndReadinessControls(string scenario, string expected)
    {
        var api = new SetupApi("agent-execution");
        var html = WebUtility.HtmlDecode(await Render(api, "aurora", officeScenario: scenario));
        Assert.Contains(expected, html);
        if (scenario == "redeemed") Assert.Contains(">Estimating time remaining\u2026</span>", html);
        if (scenario is "created" or "missing-handoff") Assert.Contains("Change drive or capacity", html);
        if (scenario is "stalled" or "redeemed" or "connected" or "ready") Assert.DoesNotContain("Change drive or capacity", html);
        if (scenario is "blocked-reuse" or "removal-failed") Assert.DoesNotContain("Reassign this Office", html);
        if (scenario == "recoveryrequired") Assert.Contains("Remove Office", html);
        if (scenario == "macos")
        {
            Assert.Contains("--gateway-sha256 " + new string('a', 64), html);
            Assert.DoesNotContain("Continue to Windows approval", html);
        }
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
    [InlineData("genai-provider", "aurora", "Step 5 of 6")]
    [InlineData("genai-provider", "gold", "Step 5 of 6")]
    [InlineData("connection-service", "aurora", "Would you like to set up connections?")]
    [InlineData("connection-service", "gold", "Would you like to set up connections?")]
    public async Task SixStepFlowRendersInBothThemes(string step, string theme, string expected)
    {
        var api = new SetupApi(step);
        var html = await Render(api, theme);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        Assert.Contains($"data-appearance=\"{theme}\"", html);
        Assert.Contains("onboarding-workspace", html);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(html, "class=\"setup-progress-node\"").Count);
        if (step == "connection-service")
        {
            Assert.Contains("Step 6 of 6", html);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "class=\"connection-option\"").Count);
            Assert.Contains("Use C-Sweet hosting", html);
            Assert.Contains(">Free</span>", html);
            Assert.Contains("Host my own", html);
            Assert.Contains("Decide later", html);
            Assert.DoesNotContain("Service address", html);
        }
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
    public async Task FinishSavesConnectionChoiceAndSurfacesRetryableFailure(bool failure)
    {
        var api = new SetupApi("connection-service") { ConnectionRevision = 0, FailFinish = failure };
        var html = await Render(api, "aurora", finish: true);
        Assert.Equal(new[] { "/api/plugins/connection-service/selection", "/api/setup/steps/connection-service/complete", "/api/setup/complete" }, api.Paths.TakeLast(3));
        Assert.Equal(new("Skip", null, 0), api.SavedChoice);
        Assert.DoesNotContain(api.Paths, path => path.EndsWith("/probe") || path.EndsWith("/phone-setup"));
        Assert.DoesNotContain(api.Paths, path => path.Contains("communications") || path.Contains("email-delivery"));
        Assert.Contains(failure ? "Please try again." : "Your workspace is ready.", WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData("https://connections.example.com", true)]
    [InlineData("http://connections.example.com", false)]
    public async Task CheckAvailabilityUsesTheEditedSelfHostedAddress(string address, bool valid)
    {
        var api = new SetupApi("connection-service");
        var html = await Render(api, "aurora", connectionAction: async component =>
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = typeof(ConnectionServiceSetupStep);
            await OpenConnectionScreen(component, "Existing");
            type.GetMethod("ChangeOrigin", flags)!.Invoke(component, [address]);
            await (Task)type.GetMethod("CheckAsync", flags)!.Invoke(component, [])!;
        });
        Assert.Contains("Service address", html);
        if (valid)
        {
            Assert.Equal(new("SelfHosted", address, 3), api.SavedChoice);
            Assert.Contains("/api/plugins/connection-service/probe", api.Paths);
            Assert.Contains("available for account sign-in", html);
        }
        else
        {
            Assert.Null(api.SavedChoice);
            Assert.DoesNotContain("/api/plugins/connection-service/probe", api.Paths);
            Assert.Contains("Enter an HTTPS address", html);
        }
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Remote")]
    public async Task NewServiceCanDownloadInstallationBeforeSaving(string location)
    {
        var api = new SetupApi("connection-service");
        var html = await Render(api, "gold", connectionAction: async component =>
        {
            await OpenConnectionScreen(component, "Location");
            await OpenConnectionScreen(component, location);
            typeof(ConnectionServiceSetupStep).GetMethod("ChangeOrigin", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(component, ["https://connections.example.com"]);
        });
        Assert.Contains("download=\"compose.yaml\"", html);
        Assert.Contains("docker compose up -d --build", html);
        Assert.Contains(location == "Remote" ? "server administrator" : "Headquarters machine", html);
        Assert.Null(api.SavedChoice);
        var encoded = System.Text.RegularExpressions.Regex.Match(html, "data:text/plain;charset=utf-8;base64,([^\"]+)").Groups[1].Value;
        var compose = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("ConnectionService__PublicOrigin: 'https://connections.example.com'", compose);
        Assert.Contains("AllowedLocalBrowserReturnUris__0: 'https://localhost:7125/settings/plugins/managed-return'", compose);
        Assert.Contains("--migrate-store", compose);
        Assert.Contains("phone.env", compose);
    }

    [Theory]
    [InlineData(3, "NotPaired")]
    [InlineData(0, "Paired")]
    public async Task DecidingLaterPreservesPreviousSelectionOrPairing(long revision, string pairingState)
    {
        var api = new SetupApi("connection-service") { ConnectionRevision = revision, PairingState = pairingState };
        var html = await Render(api, "aurora", finish: true);
        Assert.Null(api.SavedChoice);
        Assert.Contains("Your workspace is ready.", WebUtility.HtmlDecode(html));
    }

    private static Task OpenConnectionScreen(ConnectionServiceSetupStep component, string name)
    {
        var open = typeof(ConnectionServiceSetupStep).GetMethod("OpenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)open.Invoke(component, [Enum.Parse(open.GetParameters()[0].ParameterType, name)])!;
    }

    private static async Task<string> Render(SetupApi api, string theme, bool finish = false, string? officeScenario = null, string? providerScenario = null,
        Func<AgentHostOnboardingStep, Task>? officeAction = null, Func<ConnectionServiceSetupStep, Task>? connectionAction = null)
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
                if (officeAction is not null) await officeAction(activator.Office!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Office, null);
                await Task.Yield();
            }
            if (finish)
            {
                await OpenConnectionScreen(activator.Connections!, "Later");
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
                await Task.Yield();
            }
            if (connectionAction is not null)
            {
                await connectionAction(activator.Connections!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Connections, null);
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
        public ConnectionServiceSetupStep? Connections { get; private set; }
        public List<MudDialog> Dialogs { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var instance = (IComponent)Activator.CreateInstance(componentType)!;
            if (instance is AgentHostOnboardingStep office) Office = office;
            if (instance is ConnectionServiceSetupStep connections) Connections = connections;
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
        if (scenario is "storage" or "storage-full")
        {
            var limits = CSweet.Application.Setup.LocalOfficeCapacityCalculator.Calculate(8, 16L << 30, 500L << 30, true);
            var volume = new LocalOfficeVolumeResponse("data", @"D:\", "Data", "NTFS", 1024L << 30, 500L << 30,
                false, true, null, @"D:\CSweet\Office\program", @"D:\CSweet\Office\data", limits);
            Set("_storageVolumeId", scenario == "storage" ? "data" : null);
            Set("_status", status with { LocalCapacity = limits with { Volumes = [volume], SelectedVolumeId = null } });
            return;
        }
        if (scenario == "custom")
        {
            office.GetType().GetMethod("SelectCapacityPreset", flags)!.Invoke(office, ["custom"]);
            return;
        }
        if (scenario == "load-error") { Set("_error", "Connection interrupted. Please try again."); return; }
        if (scenario == "unavailable") { Set("_status", status with { LocalCapacity = null }); return; }
        if (scenario is "remote" or "remote-approval" or "linux" or "macos")
        {
            Set("_installationTarget", scenario is "linux" or "macos" ? "local" : "remote");
            Set("_remotePlatform", "windows");
            Set("_status", status with {
                LocalOperatingSystem = scenario is "linux" or "macos" ? scenario : "windows",
                Packages = new("https://example.test/office-release.json", null, null, null, "https://example.test/gateway",
                    ControlPlaneCertificateSha256: scenario == "macos" ? new string('a', 64) : null),
                ActiveEnrollment = scenario == "remote" ? new(Guid.NewGuid(), Guid.NewGuid(), "available", DateTimeOffset.UtcNow.AddMinutes(10)) : null,
                Nodes = scenario == "remote-approval" ? [new(Guid.NewGuid(), Guid.NewGuid(), "Office", "Workstation", "windows", "x64", "1.0", "1", "pendingapproval", "TEST-FINGERPRINT", null, 4, 8192, 32768, 1, null, [], new Dictionary<string,string>())] : []
            });
            return;
        }
        var state = scenario switch {
            "missing-handoff" or "stalled" => "created",
            "time-range" => "redeemed",
            "blocked-reuse" or "removal-failed" => "recoveryrequired",
            "ready-unhealthy" => "ready",
            _ => scenario
        };
        var session = new LocalOfficeSetupSessionResponse(Guid.NewGuid(), state, "install", "Installing Office",
            "Preparing the machine and verifying its connection.", 35, DateTimeOffset.UtcNow.AddMinutes(10), 4, 8192, 32768, 1, null,
            scenario == "missing-handoff" ? null : "csweet-office://test",
            ErrorCode: scenario == "removal-failed" ? "office_removal_failed" : scenario == "failed" ? "developer-bootstrap-failed" : null,
            ErrorMessage: scenario == "failed" ? "The guest reported a failed isolation check: dotnet-sdk-present" : null,
            EstimatedRemainingMinimumSeconds: scenario == "time-range" ? 15 : null,
            EstimatedRemainingMaximumSeconds: scenario == "time-range" ? 45 : null,
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
        public LocalOfficeSetupActionResponse? CancelResult { get; set; }
        public bool FailFinish { get; init; }
        public string ConnectionMode { get; init; } = "Official";
        public long ConnectionRevision { get; init; } = 3;
        public string PairingState { get; init; } = "NotPaired";
        public SaveConnectionServiceChoice? SavedChoice { get; private set; }
        private static readonly string[] Keys = ["welcome", "agent-execution", "llm-provider", "personal-assistant", "genai-provider", "connection-service"];
        private object Status(bool complete = false) => new {
            isFirstRunComplete = complete,
            steps = Keys.Select((key, i) => new { key, displayName = key, isRequired = i < 4, isComplete = i < Array.IndexOf(Keys, currentStep) })
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/plugins/connection-service/selection" && request.Method == HttpMethod.Put)
                SavedChoice = await request.Content!.ReadFromJsonAsync<SaveConnectionServiceChoice>(cancellationToken);
            object? body = path switch {
                _ when path.EndsWith("/cancel") => CancelResult,
                "/api/llm-provider-profiles" when currentStep == "personal-assistant" => new[] { new CSweet.Contracts.Llm.LlmProviderProfileResponse(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Local vLLM", CSweet.Domain.Setup.LlmProviderType.Vllm, "http://localhost:8000", null, "qwen3.8-27b", null, 256000, 128000, true, true, true, false, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) },
                "/api/personal-assistant/settings" => new CSweet.Contracts.PersonalAssistant.PersonalAssistantSettings(null, null, null, 0, false, null, [], new Dictionary<string, System.Text.Json.JsonElement>()),
                "/api/setup/status" => Status(),
                "/api/setup/complete" => new { succeeded = !FailFinish, message = "Please try again.", status = Status(!FailFinish) },
                "/api/setup/steps/connection-service/complete" => new { succeeded = true, status = Status() },
                "/api/plugins/connection-service/selection" => new ConnectionServiceChoice(SavedChoice?.Mode ?? ConnectionMode, SavedChoice?.Origin ?? ManagedServiceTrust.OfficialOrigin, SavedChoice is null ? ConnectionRevision : ConnectionRevision + 1),
                "/api/plugins/managed-connections/status" => new ManagedConnectionStatus(true, PairingState == "Paired", SavedChoice?.Origin ?? ManagedServiceTrust.OfficialOrigin, "https://localhost:7125/settings/plugins/managed-return") { PairingState = PairingState },
                "/api/plugins/connection-service/probe" => new ConnectionServiceProbe("OAuthSetupAvailable", true, false, false),
                _ => null
            };
            string? json = path switch {
                "/api/llm-provider-profiles" or "/api/genai-provider-profiles" => "[]",
                _ when path.Contains("00000000-0000-0000-0000-000000000001") => """{"models":[],"succeeded":true}""",
                "/api/llm-provider-profiles/discover-local" or "/api/genai-provider-profiles/discover-local" => """{"profiles":[],"results":[]}""",
                "/api/setup/execution-capacity" => """{"selectedMode":"local","isReady":false,"canInstallLocalNode":true,"localOperatingSystem":"windows","localArchitecture":"x64","summary":"Choose capacity","nodes":[],"checks":[],"localPrerequisites":[],"localCapacity":{"isSupported":true,"totalCpuCount":16,"totalMemoryMb":32768,"freeDiskMb":524288,"safeCpuCount":12,"safeMemoryMb":24576,"safeDiskMb":262144,"minimumCpuCount":2,"minimumMemoryMb":4096,"minimumDiskMb":32768,"presets":[{"key":"small","displayName":"Small","percentOfSafeCapacity":25,"cpuCount":3,"memoryMb":6144,"diskMb":65536},{"key":"performance","displayName":"Performance","percentOfSafeCapacity":75,"cpuCount":9,"memoryMb":18432,"diskMb":196608},{"key":"balanced","displayName":"Balanced","percentOfSafeCapacity":50,"cpuCount":6,"memoryMb":12288,"diskMb":131072}]}}""",
                _ => null
            };
            return new HttpResponseMessage(body is null && json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = body is not null ? JsonContent.Create(body) : new StringContent(json ?? "{}", Encoding.UTF8, "application/json") };
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
