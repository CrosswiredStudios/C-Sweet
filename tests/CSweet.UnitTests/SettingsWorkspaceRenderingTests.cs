using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Localization;
using CSweet.UI.Layout;
using CSweet.UI.Pages;
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

public sealed class SettingsWorkspaceRenderingTests
{
    [Theory]
    [InlineData(typeof(Settings), "overview", "Make it yours")]
    [InlineData(typeof(Appearance), "appearance", "appearance-options")]
    [InlineData(typeof(LlmProviders), "llm", "Saved providers")]
    [InlineData(typeof(GenAiProviders), "genai", "Connected providers")]
    [InlineData(typeof(Agents), "agents", "Installed agents")]
    [InlineData(typeof(AgentRuntimeSettings), "runtime", "Save runtime settings")]
    [InlineData(typeof(ExecutionFleet), "offices", "Studio Office")]
    [InlineData(typeof(SourceControlSettings), "source-control", "Internal Git storage")]
    [InlineData(typeof(Plugins), "plugins", "Preview manifest")]
    [InlineData(typeof(CommunicationSettings), "communications", "Set up Discord")]
    [InlineData(typeof(CommunicationProviderSetup), "discord", "Provisioning preview")]
    [InlineData(typeof(AccountSecurity), "security", "Regenerate recovery codes")]
    public async Task EverySettingsPageUsesSharedNavigationAndKeepsItsControls(Type page, string name, string expected)
    {
        var api = new SettingsApi();
        var html = await RenderAsync(page, api);
        Assert.Contains("aurora-page settings-workspace", html);
        Assert.Contains("settings-mobile-navigation", html);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
        if (page == typeof(AccountSecurity)) Assert.DoesNotContain("Coming soon", html);
        Assert.Contains($"/organizations/{Business.Id}/command-center", html);
        Assert.DoesNotContain(api.Methods, method => method != HttpMethod.Get); // Rendering must never mutate configuration.
        if (Environment.GetEnvironmentVariable("CSWEET_SETTINGS_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"settings-{name}.html"), html);
    }

    [Theory]
    [InlineData(0, "Office care")]
    [InlineData(1, "Recent work")]
    [InlineData(2, "Office settings")]
    public async Task OfficeDetailTabsRetainTheirManagementSections(int tab, string expected)
    {
        var html = await RenderAsync(typeof(ExecutionFleet), new SettingsApi(), officeTab: tab);
        Assert.Contains(expected, html);
        if (Environment.GetEnvironmentVariable("CSWEET_SETTINGS_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"settings-office-{tab}.html"), html);
    }

    [Theory]
    [InlineData(typeof(LlmProviders), "StartNewProvider", "llm-editor", "Provider")]
    [InlineData(typeof(Agents), "OpenImportDialog", "agent-import", "Import agent")]
    public async Task ConfigurationDialogsRemainAvailable(Type page, string method, string name, string expected)
    {
        var html = await RenderAsync(page, new SettingsApi(), openMethod: method);
        Assert.Contains("mud-dialog", html);
        Assert.Contains(expected, html);
        if (Environment.GetEnvironmentVariable("CSWEET_SETTINGS_PREVIEWS") is string path)
            await File.WriteAllTextAsync(Path.Combine(path, $"settings-{name}.html"), html);
    }

    [Fact]
    public async Task SettingsOverviewLinksEverySectionAndSearchFiltersTheCatalog()
    {
        var html = await RenderAsync(typeof(Settings), new SettingsApi());
        foreach (var section in SettingsSections.All) Assert.Contains($"href=\"{section.Url}\"", html);
        Assert.DoesNotContain("Marketplace", html);
        Assert.DoesNotContain("/settings/agents/marketplace", html);
        var page = TestLocalization.Inject(new Settings());
        typeof(Settings).GetProperty("L", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page,
            new AppLocalizer(new LocalizationResources(), new AppCulture(null, new AuthSessionStore())));
        typeof(Settings).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "image");
        var matches = (IEnumerable<SettingsSection>)typeof(Settings).GetProperty("MatchingSections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal("/settings/genai-providers", Assert.Single(matches).Url);
    }

    [Fact]
    public async Task AppearanceShowsOneSelectedThemeAndUpdatesTheSelection()
    {
        var html = await RenderAsync(typeof(Appearance), new SettingsApi(), "gold");
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Contains("appearance-option selected", html);
        Assert.Contains("Black &amp; Gold", html);
    }

    private static readonly OrganizationResponse Business = new(Guid.Parse("ab2184e9-9ac3-441d-9194-3cd65fe60052"), "Super Awesome Games", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static async Task<string> RenderAsync(Type page, SettingsApi api, string theme = "aurora", int? officeTab = null, string? openMethod = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddCSweetLocalization();
        var activator = new TrackingActivator();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager>(new TestNavigation(page));
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        services.AddSingleton<IBusinessContext, TestBusinessContext>();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<AppearanceState>().SetThemeAsync(theme);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var themeComponent = await renderer.RenderComponentAsync<MudThemeProvider>();
            var dialogs = await renderer.RenderComponentAsync<MudDialogProvider>();
            ComponentBase? pageInstance = null;
            RenderFragment body = builder =>
            {
                builder.OpenComponent(0, page);
                if (page == typeof(CommunicationProviderSetup)) builder.AddAttribute(1, "OrganizationId", Business.Id);
                if (officeTab.HasValue) builder.AddAttribute(2, "OfficeId", SettingsApi.OfficeId);
                builder.AddComponentReferenceCapture(3, instance => pageInstance = (ComponentBase)instance);
                builder.CloseComponent();
            };
            var component = await renderer.RenderComponentAsync<SettingsLayout>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SettingsLayout.Body)] = body }));
            if (officeTab.HasValue) page.GetField("_activeTab", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pageInstance, officeTab.Value);
            if (openMethod is not null) page.GetMethod(openMethod, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pageInstance, null);
            if (officeTab.HasValue || openMethod is not null)
            {
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pageInstance, null);
                await Task.Yield();
            }
            if (openMethod is not null)
            {
                // Static HTML rendering skips OnAfterRender; inline MudDialog opens its portal there.
                foreach (var dialog in activator.Dialogs.Where(x => x.GetState(component => component.Visible)).ToArray())
                    await (Task)typeof(MudDialog).GetMethod("OnAfterRenderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [true])!;
                await Task.Yield();
            }
            return themeComponent.ToHtmlString() + component.ToHtmlString() + dialogs.ToHtmlString();
        });
    }

    private sealed class SettingsApi : HttpMessageHandler
    {
        public static readonly Guid OfficeId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            string? json = path switch
            {
                "/api/setup/status" => "{}",
                "/api/setup/execution-capacity" => "{\"isReady\":true}",
                "/api/agent-runtime/settings" => """{"enableImportedAgents":true,"defaultActivationMode":"OnDemand","defaultOverlapPolicy":"Skip","defaultRestartPolicy":"OnFailure","defaultNetworkPolicy":"McpOnly","allowedPackageFeedHosts":"nuget.org","blockedNetworkCidrs":"","defaultTickFrequencySeconds":60,"minimumTickFrequencySeconds":10,"defaultMaxRuntimeSeconds":300,"defaultWorkloadMemoryMb":512,"maximumWorkloadMemoryMb":4096,"globalMaxActiveWorkloads":8} """,
                "/api/llm-provider-profiles/usage/summary" => """{"last24Hours":{"totalTokens":12500,"requestCount":12},"last7Days":{"totalTokens":148000,"requestCount":145},"last30Days":{"totalTokens":610000,"requestCount":580},"providers":[],"agents":[],"recentChatTurns":[]} """,
                "/api/agents/available" => "{\"agents\":[],\"sources\":[]}",
                "/api/execution-fleet" => """{"pools":[],"nodes":[{"id":"10000000-0000-0000-0000-000000000001","name":"Studio Office","machineName":"studio-desktop","operatingSystem":"linux","architecture":"x64","nodeVersion":"1.2.0","status":"ready","providers":[],"labels":{},"allocatableCpuCount":8,"allocatableMemoryMb":16000,"maximumConcurrentWorkloads":4}],"recentAssignments":[],"installationOverrides":[]} """,
                "/api/source-control/storage" => """{"ready":true,"repositoryRoot":"/data/repositories","temporaryRoot":"/data/tmp","lfsProvider":"FileSystem","lfsLocation":"/data/lfs","backupProvider":"FileSystem","backupLocation":"/data/backups"} """,
                "/api/source-control/platform-setup" => "{\"readiness\":{\"existingGitHubAvailable\":false,\"guidedSetupAvailable\":true}}",
                "/api/llm-provider-profiles" or "/api/genai-provider-profiles" or "/api/agents/definitions" or "/api/plugins/installations" or "/api/plugins/provider-profiles" or "/api/setup/email-delivery/profiles" => "[]",
                _ => null
            };
            if (path.EndsWith("/activity")) json = "{\"activeAssignmentCount\":0,\"recentAssignments\":[]}";
            return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TrackingActivator : IComponentActivator
    {
        public List<MudDialog> Dialogs { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is MudDialog dialog) Dialogs.Add(dialog);
            return component;
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(Type page) => Initialize("http://localhost/", "http://localhost" + (SettingsSections.All.FirstOrDefault(x => page.Name.Equals(x.TitleKey.Split('.')[2], StringComparison.OrdinalIgnoreCase))?.Url ?? "/settings"));
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class TestBusinessContext : IBusinessContext
    {
        public IReadOnlyList<OrganizationResponse> Businesses => [Business];
        public OrganizationResponse? SelectedBusiness => Business;
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
