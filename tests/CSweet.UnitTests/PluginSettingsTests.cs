using System.Reflection;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.UI.Localization;
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

public sealed class PluginSettingsTests
{
    [Fact]
    public async Task DisabledRuntimeShowsExplanationAndCannotInstallAfterApproval()
    {
        var api = new Api { ImportsEnabled = false };
        var html = await Render(api, async page =>
        {
            Set(page, "_repositoryUrl", "https://github.com/example/plugin");
            await Invoke(page, "PreviewAsync"); Set(page, "_approve", true);
            await Invoke(page, "InstallAsync");
        });
        Assert.Null(api.InstalledRequest);
        Assert.Contains("disabled by the runtime security policy", html);
        Assert.Contains("/settings/agents/runtime", html);
    }
    [Fact]
    public async Task InstalledTabExcludesAgentsAndShowsNonAgentVersionsAndManagement()
    {
        var html = await Render(new Api(), previewName: "plugins-installed");
        Assert.Contains("Service example", html);
        Assert.Contains("Connector example", html);
        Assert.Contains("1.2.3", html);
        Assert.DoesNotContain("v@", html);
        Assert.Contains("Manage", html);
        Assert.Contains("2 installed", html);
        Assert.DoesNotContain("Hidden agent", html);
        Assert.DoesNotContain("Import from GitHub", html);
        Assert.DoesNotContain("Plugin repository URL", html);
    }

    [Theory]
    [InlineData(1, "Find your next capability", "Plugin repository URL")]
    [InlineData(2, "Plugin repository URL", "Find your next capability")]
    public async Task TabsRenderTheirOwnFlows(int tab, string visible, string absent)
    {
        var html = await Render(new Api(), page => { Call(page, "ChangeTab", tab); return Task.CompletedTask; }, $"plugins-tab-{tab}");
        Assert.Contains(visible, html);
        Assert.DoesNotContain(absent, html);
        Assert.DoesNotContain("Hidden agent", html);
    }

    [Fact]
    public async Task DiscoverySearchAndCategoryFilterRealCatalogEntries()
    {
        var html = await Render(new Api(), page =>
        {
            Call(page, "ChangeTab", 1); Set(page, "_catalogSearch", "Godot"); Set(page, "_catalogCategory", "Development");
            return Task.CompletedTask;
        });
        Assert.Contains("Godot Toolchain", html);
        Assert.DoesNotContain("YouTube Connection", html);
        Assert.DoesNotContain("Web Previews", html);
    }

    [Fact]
    public async Task BundleMethodShowsCspbInputAndPreviewRequiresAFile()
    {
        var html = await Render(new Api(), page =>
        {
            Call(page, "ChangeTab", 2); Call(page, "SetSource", true); return Task.CompletedTask;
        }, "plugins-bundle");
        Assert.Contains("accept=\".cspb\"", html);
        Assert.Contains("512 MB", html);
        Assert.Contains("Preview bundle", html);
        Assert.DoesNotContain("Plugin repository URL", html);
    }

    [Theory]
    [InlineData("Service", "System", "enterprise")]
    [InlineData("Connector", "Organization", "11111111-1111-1111-1111-111111111111")]
    public async Task RepositoryReviewRequiresApprovalAndUsesTheSelectedScope(string kind, string scope, string business)
    {
        var api = new Api { PreviewKind = kind };
        await Render(api, async page =>
        {
            Set(page, "_repositoryUrl", "https://github.com/example/plugin");
            await Invoke(page, "PreviewAsync");
            Assert.Null(api.PreviewRequest!.Ref); // Repo URL alone resolves the default branch.
            await Invoke(page, "InstallAsync");
            Assert.Null(api.InstalledRequest);
            Set(page, "_approve", true);
            await Invoke(page, "InstallAsync");
        });
        Assert.Equal(scope, api.InstalledRequest!.PluginScope);
        Assert.Equal(business, api.InstalledRequest.BusinessId);
        Assert.Equal(kind == "Service" ? "AlwaysOn" : "OnDemand", api.InstalledRequest.ActivationMode);
    }

    [Fact]
    public async Task AgentRepositoryIsRejectedBeforeReviewOrInstall()
    {
        var api = new Api { PreviewKind = "Agent" };
        var html = await Render(api, async page =>
        {
            Call(page, "ChangeTab", 2); Set(page, "_repositoryUrl", "https://github.com/example/agent");
            await Invoke(page, "PreviewAsync"); Set(page, "_approve", true); await Invoke(page, "InstallAsync");
        });
        Assert.Contains("Agent packages belong in Installed Agents", html);
        Assert.Null(api.InstalledRequest);
    }

    [Fact]
    public async Task MissingBusinessOrAllPublicAcknowledgementCannotInstall()
    {
        var api = new Api { PreviewKind = "Connector", AllPublic = true };
        await Render(api, async page =>
        {
            Set(page, "_repositoryUrl", "https://github.com/example/plugin"); await Invoke(page, "PreviewAsync");
            Set(page, "_approve", true); await Invoke(page, "InstallAsync"); Assert.Null(api.InstalledRequest);
            Set(page, "_acknowledgeAllPublic", true); Set(page, "_businessId", null); await Invoke(page, "InstallAsync"); Assert.Null(api.InstalledRequest);
            Set(page, "_businessId", (Guid?)Business.Id); await Invoke(page, "InstallAsync");
        });
        Assert.True(api.InstalledRequest!.AllPublicWebAccessAcknowledged);
    }

    private static readonly OrganizationResponse Business = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "QFN Games", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static void Set(Plugins page, string name, object? value) => typeof(Plugins).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    private static object? Call(Plugins page, string name, params object?[] args) => typeof(Plugins).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args);
    private static Task Invoke(Plugins page, string method) => (Task)Call(page, method)!;

    private static async Task<string> Render(Api api, Func<Plugins, Task>? interact = null, string? previewName = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization(); services.AddSingleton<IPluginApiClient>(api);
        services.AddSingleton<IBusinessContext, StubBusinessContext>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        var activator = new Activator(); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var theme = await renderer.RenderComponentAsync<MudThemeProvider>();
            var output = await renderer.RenderComponentAsync<Plugins>();
            if (interact is not null)
            {
                await interact(activator.Page!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Page, null);
                await Task.Yield();
            }
            var html = theme.ToHtmlString() + output.ToHtmlString();
            if (previewName is not null && Environment.GetEnvironmentVariable("CSWEET_SETTINGS_PREVIEWS") is string path)
                await File.WriteAllTextAsync(Path.Combine(path, previewName + ".html"), html);
            return html;
        });
    }

    private sealed class Activator : IComponentActivator
    {
        public Plugins? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is Plugins page) Page = page;
            return component;
        }
    }
    private sealed class Api : IPluginApiClient
    {
        public bool ImportsEnabled { get; init; } = true;
        public Task<PluginInstallationPolicyResponse> GetInstallationPolicyAsync(CancellationToken token = default) => Task.FromResult(new PluginInstallationPolicyResponse(ImportsEnabled));
        public string PreviewKind { get; init; } = "Service";
        public bool AllPublic { get; init; }
        public PreviewAgentImportRequest? PreviewRequest { get; private set; }
        public InstallAgentRequest? InstalledRequest { get; private set; }
        public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<AgentInstallationResponse>>([Installation("Service"), Installation("Connector"), Installation("Agent")]);
        private static AgentInstallationResponse Installation(string kind) => new(Guid.NewGuid(), Guid.NewGuid(), Business.Id.ToString("D"), "example." + kind,
            kind == "Agent" ? "Hidden agent" : kind + " example", "1.2.3", "Example", new string('a', 40), true, [], [], [], [], [], 512, 50, null!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { PluginKind = kind, InstallationScope = kind == "Service" ? "System" : "Organization" };
        public Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken token = default)
        {
            PreviewRequest = request;
            return Task.FromResult(new AgentImportPreviewResponse(Guid.NewGuid(), request.RepositoryUrl, new string('a', 40), new string('b', 64), "example", "Example", "1.0.0", "example", "Example", "dotnet-project", "Example.csproj", "net10.0", "OnDemand", [], [], [], [], AllPublic ? ["all-public"] : [], [], "Previewed")
            { PluginKind = PreviewKind, WebAccess = new() { Mode = AllPublic ? PluginWebAccessMode.AllPublic : PluginWebAccessMode.None } });
        }
        public Task<AgentInstallationResponse> InstallAsync(Guid id, InstallAgentRequest request, CancellationToken token = default) { InstalledRequest = request; return Task.FromResult(Installation(PreviewKind)); }
        public Task<AgentImportPreviewResponse> PreviewBundleAsync(Stream bundle, string fileName, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListProviderProfilesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<PluginProviderProfileResponse>>([]);
        public Task SaveConfigurationAsync(Guid id, IReadOnlyDictionary<string, string> settings, CancellationToken token = default) => throw new NotSupportedException();
        public Task SetSecretAsync(Guid id, string key, string value, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentInstallationResponse> SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default) => throw new NotSupportedException();
        public Task<RemoveAgentInstallationResponse> RemoveAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<PluginProviderProfileResponse> SaveProviderProfileAsync(string id, UpsertPluginProviderProfileRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteProviderProfileAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class StubBusinessContext : IBusinessContext
    {
        public IReadOnlyList<OrganizationResponse> Businesses => [Business];
        public OrganizationResponse? SelectedBusiness => Business;
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SelectAsync(Guid id, bool navigate = true, CancellationToken token = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://host.example/", "https://host.example/settings/plugins");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
