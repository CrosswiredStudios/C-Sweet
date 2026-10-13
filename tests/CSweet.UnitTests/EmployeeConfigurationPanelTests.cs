using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.UI.Components.Employees;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class EmployeeConfigurationPanelTests
{
    internal static AgentConfigurationView SampleView() => new(
        "producer", "1.0", "1", [
            new("style", "Working style", "text", true, "How this employee approaches its work."),
            new("budget", "Response budget", "number", true, Minimum: 1, Maximum: 100),
            new("enabled", "Allow proactive updates", "boolean", false),
            new("mode", "Instruction mode", "select", false, Options: [new("default", "Default"), new("custom", "Custom")]),
            new("instructions", "Custom instructions", "textarea", false, VisibleWhenFieldKey: "mode", VisibleWhenValue: "custom"),
            new("token", "Access token", "secret", false)],
        Values("Balanced"), Values("Detailed"), Values("Detailed"), ["style"], 7, 7, 7, "Current");

    private static Dictionary<string, JsonElement> Values(string style) => new()
    {
        ["style"] = JsonSerializer.SerializeToElement(style),
        ["budget"] = JsonSerializer.SerializeToElement(40),
        ["enabled"] = JsonSerializer.SerializeToElement(false),
        ["token"] = JsonSerializer.SerializeToElement("test-only-secret")
    };

    [Fact]
    public async Task RendersDeclaredSettingsWithoutInventingModelControls()
    {
        await using var harness = await Harness.Create();
        var html = await harness.Html();
        Assert.Contains("Working style", html);
        Assert.Contains("type=\"password\"", html);
        Assert.DoesNotContain("Custom instructions</", html);
        Assert.DoesNotContain("Select a model", html);
        Assert.Single(harness.Api.Requests);
        await harness.Call("SetConfigurationValue", "mode", "custom");
        Assert.Contains("Custom instructions", await harness.Html());
    }

    [Fact]
    public async Task SaveUsesEmployeeScopeRevisionAndTypedValues()
    {
        await using var harness = await Harness.Create();
        await harness.Call("SaveConfigurationAsync");
        Assert.Single(harness.Api.Requests);
        await harness.Call("SetConfigurationValue", "style", "Concise");
        await harness.Call("SetConfigurationValue", "budget", 64m);
        await harness.Call("SaveConfigurationAsync");
        var saved = Assert.Single(harness.Api.Saves);
        Assert.Equal(7, saved.ExpectedRevision);
        Assert.Equal("Concise", saved.Overrides["style"].GetString());
        Assert.Equal(64, saved.Overrides["budget"].GetInt32());
        Assert.False(saved.Overrides["enabled"].GetBoolean());
        Assert.Contains($"/users/{harness.EmployeeId}/agent-configuration/overrides", harness.Api.Requests.Last());
        Assert.Contains("Settings saved", await harness.Html());
    }

    [Fact]
    public async Task RevisionConflictPreservesDraftAndOffersReload()
    {
        await using var harness = await Harness.Create(new Api { Conflict = true });
        await harness.Call("SetConfigurationValue", "style", "Concise");
        await harness.Call("SaveConfigurationAsync");
        var html = await harness.Html();
        Assert.Contains("changed elsewhere", html);
        Assert.Contains("Concise", html);
        Assert.Contains("Unsaved changes", html);
        Assert.Single(harness.Api.Saves);
    }

    [Fact]
    public async Task RestoresAreRevisionCheckedAndDoNotDiscardDrafts()
    {
        await using var harness = await Harness.Create();
        await harness.Call("SetConfigurationValue", "style", "Draft");
        await harness.Call("RestoreAllConfigurationAsync");
        Assert.Single(harness.Api.Requests);
        await harness.Call("DiscardChanges");
        await harness.Call("RestoreConfigurationKeyAsync", "style");
        Assert.Contains("/overrides/style?expectedRevision=7", harness.Api.Requests.Last());
        Assert.Contains("Default restored", await harness.Html());
        await harness.Call("RestoreAllConfigurationAsync");
        Assert.Contains("/overrides?expectedRevision=8", harness.Api.Requests.Last());
    }

    [Fact]
    public async Task EmptySchemaHasNoSaveAction()
    {
        await using var harness = await Harness.Create(new Api { View = SampleView() with { Fields = [] } });
        var html = await harness.Html();
        Assert.Contains("No configurable settings", html);
        Assert.DoesNotContain("Save changes", html);
    }

    [Fact]
    public async Task RequiredSettingsAreValidatedBeforeSaving()
    {
        await using var harness = await Harness.Create();
        await harness.Call("SetConfigurationValue", "style", "");
        await harness.Call("SaveConfigurationAsync");
        Assert.Empty(harness.Api.Saves);
    }

    [Fact]
    public async Task CatalogDiscoveryPreservesSavedModelUntilProviderChanges()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var api = new Api { Providers = [first, second] };
        api.View = SampleView() with
        {
            Fields = [new("provider", "LLM provider", "llmProvider", true),
                new("model", "Model", "llmModel", true, DependsOnFieldKey: "provider")],
            EffectiveValues = new Dictionary<string, JsonElement>
            {
                ["provider"] = JsonSerializer.SerializeToElement(first.ToString()),
                ["model"] = JsonSerializer.SerializeToElement("saved-model")
            }
        };
        await using var harness = await Harness.Create(api);
        Assert.Contains("saved-model", await harness.Html());
        Assert.Empty(api.Saves);
        await harness.Call("SetProviderValueAsync", "provider", second.ToString());
        await harness.Call("SaveConfigurationAsync");
        var save = Assert.Single(api.Saves);
        Assert.Equal(second.ToString(), save.Overrides["provider"].GetString());
        Assert.Equal("default-model", save.Overrides["model"].GetString());
    }

    [Fact]
    public async Task AccessFailureCanBeRetried()
    {
        await using var harness = await Harness.Create(new Api { Denied = true });
        Assert.Contains("permission to configure", await harness.Html());
        harness.Api.Denied = false;
        await harness.Call("LoadAsync");
        Assert.Contains("Working style", await harness.Html());
    }

    private sealed class Api : HttpMessageHandler
    {
        public AgentConfigurationView View = SampleView();
        public bool Conflict, Denied;
        public Guid[] Providers = [];
        public List<string> Requests = [];
        public List<PutAgentConfigurationOverridesRequest> Saves = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (Denied) return new(HttpStatusCode.Forbidden);
            if (request.RequestUri.AbsolutePath.EndsWith("/llm-provider-profiles"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Providers.Select(id => new
                { id, name = "Test provider", isEnabled = true, defaultChatModel = "default-model" })) };
            if (request.RequestUri.AbsolutePath.EndsWith("/models"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { succeeded = true, models = Array.Empty<object>() }) };
            if (request.Method == HttpMethod.Put)
            {
                var save = (await request.Content!.ReadFromJsonAsync<PutAgentConfigurationOverridesRequest>(token))!;
                Saves.Add(save);
                if (Conflict) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "Revision conflict" }) };
                View = View with { EffectiveValues = save.Overrides, ExpectedRevision = 8 };
            }
            if (request.Method == HttpMethod.Delete)
                View = View with { EffectiveValues = View.DefaultValues, OverriddenKeys = [], ExpectedRevision = 8 };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(View) };
        }
    }

    private sealed class Harness : IAsyncDisposable, IComponentActivator
    {
        public Api Api = default!;
        public Guid EmployeeId = Guid.NewGuid();
        private ServiceProvider _services = default!;
        private HtmlRenderer _renderer = default!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private EmployeeConfigurationPanel _panel = default!;
        public IComponent CreateInstance(Type type)
        {
            var instance = (IComponent)Activator.CreateInstance(type)!;
            if (instance is EmployeeConfigurationPanel panel) _panel = panel;
            return instance;
        }
        public static async Task<Harness> Create(Api? api = null)
        {
            var h = new Harness { Api = api ?? new Api() };
            var services = new ServiceCollection().AddLogging();
            services.AddMudServices();
            services.AddSingleton<IJSRuntime, NoJavaScript>();
            services.AddSingleton<IComponentActivator>(h);
            services.AddSingleton(new HttpClient(h.Api) { BaseAddress = new("http://localhost/") });
            services.AddScoped<IAgentApiClient, AgentApiClient>();
            services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
            h._services = services.BuildServiceProvider();
            h._renderer = new(h._services, h._services.GetRequiredService<ILoggerFactory>());
            await h._renderer.Dispatcher.InvokeAsync(async () => h._root = await h._renderer.RenderComponentAsync<EmployeeConfigurationPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = Guid.NewGuid(), ["EmployeeId"] = h.EmployeeId })));
            return h;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString());
        public Task Call(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(EmployeeConfigurationPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_panel, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_panel, null);
        });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
