using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.Plugins;
using CSweet.UI.Localization;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class PluginSetupActivationComponentTests
{
    [Theory]
    [InlineData("native-selected", true)]
    [InlineData("native-unselected", false)]
    [InlineData("native-removed", false)]
    [InlineData("legacy-selected", true)]
    [InlineData("legacy-unavailable", false)]
    [InlineData("legacy-wrong-selection", false)]
    [InlineData("mixed-ready", true)]
    [InlineData("one-dependency-missing", false)]
    [InlineData("no-dependencies", true)]
    public async Task ActualSetupPageRecognizesExplicitNativeAndLegacySelectionsWithoutActivating(string scenario, bool ready)
    {
        var connector = Guid.NewGuid();
        var resource = new PluginAccountResourceChoice(connector, "Example workspace", Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64), true);
        var native = new PluginDependencySetupResponse("workspace", "example.connector", ["example.api.read.v1"], null, [])
            { ResourceChoices = [resource] };
        var legacy = new PluginDependencySetupResponse("legacy", "example.legacy", ["example.api.read.v1"], connector,
            [new(connector, "Legacy", "Ready", true, null, [new("account-a", "Account A")])]);
        PluginDependencySetupResponse[] dependencies = scenario switch
        {
            "native-selected" => [native],
            "native-unselected" => [native with { ResourceChoices = [resource with { Selected = false }] }],
            "native-removed" => [native with { ResourceChoices = [] }],
            "legacy-selected" => [legacy],
            "legacy-unavailable" => [legacy with { Choices = [legacy.Choices[0] with { CanSelect = false }] }],
            "legacy-wrong-selection" => [legacy with { SelectedInstallationId = Guid.NewGuid() }],
            "mixed-ready" => [native, legacy],
            "one-dependency-missing" => [native, legacy with { SelectedInstallationId = null }],
            "no-dependencies" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var http = new SetupReads(dependencies);
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton(new HttpClient(http) { BaseAddress = new("https://app.example/") });
        services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<IAppLocalizer, Labels>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<MudThemeProvider>();
            var page = await renderer.RenderComponentAsync<PluginSetup>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = Guid.NewGuid(), ["InstallationId"] = Guid.NewGuid() }));
            return page.ToHtmlString();
        });
        var activate = Regex.Matches(html, "<button\\b[^>]*>.*?</button>", RegexOptions.Singleline)
            .Cast<Match>().Single(x => x.Value.Contains("PluginSetup.Ready.Activate", StringComparison.Ordinal)).Value;
        Assert.Equal(!ready, Regex.IsMatch(activate, "\\bdisabled(?:=|\\s|>)"));
        Assert.Equal(!ready, html.Contains("PluginSetup.Ready.SelectAccount", StringComparison.Ordinal));
        Assert.Equal(2, http.Reads);
        Assert.Equal(0, http.Writes);
    }

    private sealed class SetupReads(PluginDependencySetupResponse[] dependencies) : HttpMessageHandler
    {
        public int Reads, Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new InvalidOperationException("Rendering must not activate, bind or authorize."); }
            Reads++;
            object response = request.RequestUri!.AbsolutePath.EndsWith("/dependencies", StringComparison.Ordinal)
                ? dependencies : new PluginSetupResponse(Guid.NewGuid(), "NeedsSetup", "setup", null, [],
                    new() { Id = "setup", Title = "Setup", Steps = [] }, []);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, response.GetType()) });
        }
    }
    private sealed class Labels : IAppLocalizer
    { public string this[string key] => key; public string Format(string key, object? args) => key; public bool Has(string key) => true; }
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("https://app.example/", "https://app.example/setup"); protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("No navigation during readiness checks."); }
    private sealed class NoJs : IJSRuntime
    { public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!); public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!); }
}
