using System.Reflection;
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

public sealed class PluginManageConnectionTests
{
    [Fact]
    public async Task InitialPageHasOneConnectActionAndCollapsedAccessDetailsWithVisiblePolicyLinks()
    {
        var api = new Api();
        var html = await Render(api);
        Assert.Contains("Read selected workspaces", html);
        Assert.Contains("https://example.com/privacy", html);
        Assert.Contains("https://example.com/terms", html);
        Assert.Contains("By choosing Connect account", html);
        Assert.Contains("What am I allowing?", html);
        Assert.Matches("<details class=\"connection-access\"[^>]*>", html);
        Assert.DoesNotMatch("<details class=\"connection-access\"[^>]* open", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, @">\s*Connect account\s*</span>"));
        Assert.DoesNotContain("Administration</", html);
        Assert.DoesNotContain("I have reviewed the access", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task AdministratorGetsAnActionableSetupLinkInsteadOfADisabledConnectButton()
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, CanAdminister = true, ReadinessMessage = "provider_configuration_required" };
        var html = await Render(api);
        Assert.Contains("Account connections need setup", html);
        Assert.Contains("Set up connections", html);
        Assert.Contains("/settings/plugins#provider-administration", html);
        Assert.DoesNotContain("What am I allowing?", html);
        Assert.DoesNotContain(">Connect account</span>", html);
        Assert.Matches("<details id=\"plugin-settings\" class=\"plugin-settings\"[^>]*>", html);
        Assert.DoesNotMatch("<details id=\"plugin-settings\"[^>]* open", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task AccountOwnerSeesSetupExplanationWithoutAdministratorControls()
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, ReadinessMessage = "provider_configuration_required" };
        var html = await Render(api);
        Assert.Contains("Account connections need setup", html);
        Assert.Contains("deployment administrator", html);
        Assert.DoesNotContain("Set up connections", html);
        Assert.DoesNotContain("Plugin settings", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task BrandIconComesFromTheGenericCatalogueIdentity()
    {
        var api = new Api(); api.View = api.View with { PluginId = "com.csweet.connector.youtube" };
        var html = await Render(api);
        Assert.Contains("_content/CSweet.UI/images/brands/youtube.svg", html);
    }

    [Fact]
    public async Task FirstConnectClickStartsOnlyRequiredAccessAndNavigatesToProvider()
    {
        var api = new Api(); var navigation = new Navigation();
        await Render(api, page => Invoke(page, "OpenConnect", (object?)null), navigation);
        Assert.Equal(new[] { "base" }, api.Request!.ScopeSetIds);
        Assert.Equal("policy-v2", api.Request.PolicyVersion);
        Assert.True(api.Request.AcceptPolicies);
        Assert.Equal(api.View.PackageDigest, api.Request.PackageDigest);
        Assert.Null(api.Request.ReconnectConnectionId);
        Assert.Equal("https://identity.example.com/authorize?state=opaque", navigation.Target);
        Assert.True(navigation.ForceLoad);
    }

    [Fact]
    public async Task ReconnectShowsReviewAndPreservesExistingPermissionsWithoutStartingConsent()
    {
        var authorization = Guid.NewGuid(); var api = new Api();
        api.View = api.View with { Accounts = [new(Guid.NewGuid(), "Example account", "Connected", 1, true, [],
            [new(authorization, "Connected", ["base", "optional"], true)], [])] };
        var html = await Render(api, page => Invoke(page, "OpenConnect", authorization));
        Assert.Contains("Choose what to enable", html);
        Assert.Contains("Optional changes", html);
        Assert.Contains("I have reviewed the access", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task NewAccountNeverInheritsAnExistingAccountsOptionalPermissionSelection()
    {
        var authorization = Guid.NewGuid(); var api = new Api();
        api.View = api.View with { Accounts = [new(Guid.NewGuid(), "Existing account", "Connected", 1, true, [],
            [new(authorization, "Connected", ["base", "optional"], true)], [])] };
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", authorization);
            await Invoke(page, "OpenConnect", (object?)null);
        });
        Assert.Equal(new[] { "base" }, api.Request!.ScopeSetIds);
        Assert.Null(api.Request.ReconnectConnectionId);
    }

    [Theory]
    [InlineData("provider_configuration_required")]
    [InlineData("plugin_paused")]
    public async Task UnavailableConnectionCannotStartConsent(string reason)
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, ReadinessMessage = reason };
        await Render(api, page => Invoke(page, "OpenConnect", (object?)null));
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task ReconnectCannotTargetAnUnassignedAuthorization()
    {
        var api = new Api();
        await Render(api, page => Invoke(page, "OpenConnect", Guid.NewGuid()));
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task FailedConsentStartLeavesAnInlineErrorAndCanBeRetried()
    {
        var api = new Api { Failure = "The provider is temporarily unavailable." };
        var html = await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null);
            Assert.Equal(api.Failure, typeof(PluginManage).GetField("_error", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
            api.Failure = null;
            await Invoke(page, "OpenConnect", (object?)null);
        });
        Assert.Equal(2, api.Starts);
        Assert.DoesNotContain("The provider is temporarily unavailable.", html);
    }

    [Fact]
    public async Task NonHttpsConsentResponseCannotNavigate()
    {
        var api = new Api { AuthorizationUrl = "http://identity.example.com/authorize" }; var navigation = new Navigation();
        var html = await Render(api, page => Invoke(page, "OpenConnect", (object?)null), navigation);
        Assert.Null(navigation.Target);
        Assert.Contains("The secure sign-in address is unavailable.", html);
    }

    private static Task Invoke(PluginManage page, string method, params object?[] args) =>
        (Task)typeof(PluginManage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args)!;

    private static async Task<string> Render(Api api, Func<PluginManage, Task>? interact = null, Navigation? navigation = null)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IPluginAccountApiClient>(api); services.AddSingleton<IBusinessContext, StubBusinesses>();
        services.AddSingleton<NavigationManager>(navigation ?? new Navigation()); services.AddSingleton<IJSRuntime, NoJavaScript>();
        var activator = new Activator(); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var theme = await renderer.RenderComponentAsync<MudThemeProvider>();
            var output = await renderer.RenderComponentAsync<PluginManage>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["InstallationId"] = api.View.InstallationId }));
            if (interact is not null)
            {
                await interact(activator.Page!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Page, null);
                await Task.Yield();
            }
            return theme.ToHtmlString() + output.ToHtmlString();
        });
    }

    private sealed class Api : IPluginAccountApiClient
    {
        public PluginAccountManagementView View = new(Guid.NewGuid(), "Example connector", "1.0.0", new string('a', 64), "Built", true, null,
            new() { AccountLabel = "Example account", ResourcesLabel = "Workspaces", ConsentPurpose = "Read selected workspaces", PolicyVersion = "policy-v2",
                PrivacyPolicyUrl = "https://example.com/privacy", TermsOfServiceUrl = "https://example.com/terms" },
            [new() { Id = "base", Required = true, Label = "Read", Purpose = "Read workspace information" },
             new() { Id = "optional", Label = "Optional changes", Purpose = "Change workspace content" }], [], [], false);
        public ConnectPluginAccountRequest? Request; public string? Failure; public int Starts;
        public string AuthorizationUrl = "https://identity.example.com/authorize?state=opaque";
        public Task<PluginAccountManagementView> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(View);
        public Task<BeginPluginAccountResponse> ConnectAsync(Guid id, ConnectPluginAccountRequest request, CancellationToken ct = default)
        { Starts++; Request = request; return Failure is null ? Task.FromResult(new BeginPluginAccountResponse(Guid.NewGuid(), AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10))) : Task.FromException<BeginPluginAccountResponse>(new InvalidOperationException(Failure)); }
        public Task RetryAsync(Guid id, Guid setup, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveGrantAsync(Guid id, Guid account, SavePluginAccountGrantRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeGrantAsync(Guid id, Guid grant, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid id, Guid account, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(Guid id, Guid account, DisconnectPluginAccountRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveOperatorAsync(Guid id, SavePluginOperatorRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeOperatorAsync(Guid id, Guid op, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginInstallationImpact> GetImpactAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ControlAsync(Guid id, PluginInstallationControlRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RetryBuildAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginPackageReview> GetReviewAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Activator : IComponentActivator
    {
        public PluginManage? Page;
        public IComponent CreateInstance(Type type)
        { var component = (IComponent)System.Activator.CreateInstance(type)!; if (component is PluginManage page) Page = page; return component; }
    }
    private sealed class Navigation : NavigationManager
    {
        public string? Target; public bool ForceLoad;
        public Navigation() => Initialize("https://host.example/", "https://host.example/settings/plugins");
        protected override void NavigateToCore(string uri, bool forceLoad) { Target = uri; ForceLoad = forceLoad; }
    }
    private sealed class StubBusinesses : IBusinessContext
    {
        public IReadOnlyList<OrganizationResponse> Businesses => [];
        public OrganizationResponse? SelectedBusiness => null; public bool IsLoading => false; public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectAsync(Guid id, bool navigate = true, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
