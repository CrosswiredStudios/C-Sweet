using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class PluginProviderAdministrationTests
{
    [Fact]
    public void HostDefaultConfigurationDoesNotRegisterAnApplicationProvider()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src/CSweet.Api/appsettings.json")));
        Assert.Empty(config.RootElement.GetProperty("CSweet").GetProperty("PluginConnections")
            .GetProperty("Providers").EnumerateObject());
    }

    [Fact]
    public async Task NoConnectorMeansNoProviderSectionOrCredentialAdministrationRead()
    {
        var api = new FakeApi { Profiles = [Profile(Declaration("video"))] };
        var html = await RenderAsync<CSweet.UI.Pages.Plugins>(api);
        Assert.DoesNotContain("OAuth provider profiles", html);
        Assert.DoesNotContain("video provider", html);
        Assert.Equal(0, api.ProfileReads);
    }

    [Fact]
    public async Task UnrelatedAgentDeclarationDoesNotExposeProviderAdministration()
    {
        var declaration = Declaration("video");
        var api = new FakeApi { Installations = [Installation("Agent", declaration)], Profiles = [Profile(declaration)] };
        var html = await RenderAsync<CSweet.UI.Pages.Plugins>(api);
        Assert.DoesNotContain("OAuth provider profiles", html);
        Assert.Equal(0, api.ProfileReads);
    }

    [Fact]
    public async Task InstalledConnectorDisplaysOnlyItsDeclaredProviderWithoutOpeningACredentialForm()
    {
        var declaration = Declaration("crm");
        var api = new FakeApi
        {
            Installations = [Installation("Connector", declaration)],
            Profiles = [Profile(Declaration("video"))]
        };
        var html = await RenderAsync<CSweet.UI.Pages.Plugins>(api);
        Assert.Contains("crm provider", html);
        Assert.Contains("https://crm.example.com/token", html);
        Assert.Contains("Configure declared provider", html);
        Assert.DoesNotContain("video provider", html);
        Assert.DoesNotContain("OAuth client secret", html);
        Assert.Equal(1, api.ProfileReads);
    }

    [Fact]
    public async Task TwoFakeProvidersRenderFromMetadataAndNeverConfigureThemselves()
    {
        var api = new FakeApi();
        var html = await RenderRequirementsAsync(api, [Declaration("video"), Declaration("crm")]);
        Assert.Contains("video provider", html);
        Assert.Contains("crm provider", html);
        Assert.Contains("https://video.example.com/authorize", html);
        Assert.Contains("https://crm.example.com/authorize", html);
        Assert.Equal(0, api.ProfileReads);
        // Every mutating fake API method throws; rendering only shows the native administrator action.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProfileStatusRequiresMatchingDestinationsAndDoesNotImplyAgentAuthority(bool matches)
    {
        var declaration = Declaration("crm");
        var profile = Profile(declaration);
        if (!matches) profile = profile with { TokenEndpoint = "https://different.example.com/token" };
        var api = new FakeApi { Installations = [Installation("Connector", declaration)], Profiles = [profile] };
        var html = await RenderAsync<CSweet.UI.Pages.Plugins>(api);
        Assert.Contains("does not approve a package or grant an agent access", html);
        Assert.DoesNotContain("OAuth client secret", html);
        if (matches) Assert.Contains("Credentials configured", html);
        else
        {
            Assert.Contains("Needs attention", html);
            Assert.Contains("does not match", html);
            Assert.DoesNotContain("Credentials configured", html);
        }
    }

    [Fact]
    public async Task MaliciousDisplayTextIsEscapedAndConflictingDeclarationsCannotBeConfigured()
    {
        var declaration = Declaration("video");
        var hostile = declaration with { Provider = declaration.Provider! with { DisplayName = "<script>bad()</script>" } };
        var escaped = await RenderRequirementsAsync(new FakeApi(), [hostile]);
        Assert.DoesNotContain("<script>", escaped);
        Assert.Contains("&lt;script&gt;", escaped);
        var conflicting = await RenderRequirementsAsync(new FakeApi(), [declaration, hostile]);
        Assert.Contains("conflicting provider details", conflicting);
        Assert.DoesNotContain("Configure declared provider", conflicting);
    }

    [Fact]
    public async Task ProfileIdImpersonationNeverOffersToOverwriteExistingDestinations()
    {
        var declaration = Declaration("video");
        var existing = Profile(declaration) with { TokenEndpoint = "https://different.example.com/token" };
        var html = await RenderRequirementsAsync(new FakeApi(), [declaration], [existing]);
        Assert.Contains("does not match", html);
        Assert.DoesNotContain("Configure declared provider", html);
        Assert.False(Assert.Single(PluginProviderDeclarations.Collect([declaration])).Matches(existing));
    }

    [Fact]
    public void RepeatedMatchingDeclarationsDeduplicateButAnyMetadataConflictFailsClosed()
    {
        var declaration = Declaration("video");
        var same = declaration with { Id = "second-account", Provider = declaration.Provider! with { } };
        var requirement = Assert.Single(PluginProviderDeclarations.Collect([declaration, same]));
        Assert.True(requirement.CanConfigure);
        Assert.True(requirement.Matches(Profile(declaration)));
        foreach (var changed in new OAuthProviderMetadata?[]
        {
            null,
            declaration.Provider! with { TokenEndpoint = "https://other.example.com/token" },
            declaration.Provider! with { ClientAuthentication = "client_secret_basic" },
            declaration.Provider! with { AuthorizationParameters = new Dictionary<string, string> { ["prompt"] = "consent" } }
        })
            Assert.False(Assert.Single(PluginProviderDeclarations.Collect([declaration, same with { Provider = changed }])).CanConfigure);
        Assert.False(Assert.Single(PluginProviderDeclarations.Collect([declaration with { Provider = null }])).CanConfigure);
    }

    private static PluginConnectionDeclaration Declaration(string name) => new()
    {
        Id = name, ProviderProfile = $"com.example.{name}",
        Provider = new OAuthProviderMetadata
        {
            DisplayName = $"{name} provider", AuthorizationEndpoint = $"https://{name}.example.com/authorize",
            TokenEndpoint = $"https://{name}.example.com/token", RevocationEndpoint = $"https://{name}.example.com/revoke"
        }
    };

    private static PluginProviderProfileResponse Profile(PluginConnectionDeclaration declaration) => new(
        declaration.ProviderProfile, declaration.Provider!.DisplayName, declaration.Provider.AuthorizationEndpoint,
        declaration.Provider.TokenEndpoint, declaration.Provider.RevocationEndpoint, "public-client-id", true, true, true, null);

    private static AgentInstallationResponse Installation(string kind, PluginConnectionDeclaration declaration) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(), "com.example.plugin", "Example plugin", "0.1.0",
        "Example", new string('a', 40), true, [], [], [], [], [], 512, 100, null!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { PluginKind = kind, ConnectionDeclarations = [declaration] };

    private static Task<string> RenderRequirementsAsync(FakeApi api, IReadOnlyList<PluginConnectionDeclaration> declarations,
        IReadOnlyList<PluginProviderProfileResponse>? profiles = null) => RenderAsync<PluginProviderRequirements>(api,
        new Dictionary<string, object?>
        {
            [nameof(PluginProviderRequirements.Declarations)] = declarations,
            [nameof(PluginProviderRequirements.Profiles)] = profiles ?? []
        });

    private static async Task<string> RenderAsync<T>(FakeApi api, Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<IPluginApiClient>(api);
        services.AddSingleton<NavigationManager, TestNavigation>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []));
            return output.ToHtmlString();
        });
    }

    private static string Root([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://host.example.com/", "https://host.example.com/settings/plugins");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class FakeApi : IPluginApiClient
    {
        public IReadOnlyList<AgentInstallationResponse> Installations { get; init; } = [];
        public IReadOnlyList<PluginProviderProfileResponse> Profiles { get; init; } = [];
        public int ProfileReads { get; private set; }
        public Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installations);
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListProviderProfilesAsync(CancellationToken cancellationToken = default)
        { ProfileReads++; return Task.FromResult(Profiles); }
        public Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentInstallationResponse> InstallAsync(Guid importId, InstallAgentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveConfigurationAsync(Guid installationId, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSecretAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentInstallationResponse> SetEnabledAsync(Guid installationId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoveAgentInstallationResponse> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PluginProviderProfileResponse> SaveProviderProfileAsync(string id, UpsertPluginProviderProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteProviderProfileAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
