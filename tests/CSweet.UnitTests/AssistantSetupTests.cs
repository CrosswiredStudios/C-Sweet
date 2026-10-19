using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Api.Setup;
using CSweet.Contracts.Llm;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.UI.Components;
using CSweet.UI.Services;
using CSweet.UI.Setup;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class AssistantSetupTests
{
    [Theory]
    [InlineData("/api/personal-assistant/settings", "GET", true)]
    [InlineData("/api/personal-assistant/settings", "PUT", true)]
    [InlineData("/api/personal-assistant/messages", "POST", false)]
    [InlineData("/api/personal-assistant/settings/other", "GET", false)]
    [InlineData("/api/repository-onboarding", "POST", false)]
    public async Task FirstRunAllowsSettingsButKeepsAssistantWorkGuarded(string path, string method, bool allowed)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        await using var disposable = services;
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path; context.Request.Method = method; context.Response.Body = new MemoryStream();
        var reachedEndpoint = false;
        await new FirstRunSetupGuardMiddleware(_ => { reachedEndpoint = true; return Task.CompletedTask; }).InvokeAsync(context, new SetupService(db));
        Assert.Equal(allowed, reachedEndpoint);
        Assert.Equal(allowed ? 200 : 409, context.Response.StatusCode);
    }

    [Fact]
    public async Task AssistantStepSavesAllFieldsBeforeContinuing()
    {
        var api = new Api();
        await Render(api, async (renderer, capture) =>
        {
            Assert.Equal(256000, Get<int>(capture.Form!, "_context"));
            Assert.Equal(128000, Get<int>(capture.Form!, "_output"));
            Set(capture.Form!, "_name", "  Nova  ");
            Set(capture.Form!, "_model", "custom-model");
            Set(capture.Form!, "_context", 192000); Set(capture.Form!, "_output", 96000);
            await Invoke(capture.Form!, "DirtyAsync");
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Equal(1, api.Continued);
            Assert.Equal("Nova", api.Saved!.Name);
            Assert.Equal("custom-model", api.Saved.Model);
            Assert.Equal(api.Provider, api.Saved.ProviderId);
            Assert.Equal(192000, api.Saved.MaxContextWindowTokens);
            Assert.Equal(96000, api.Saved.MaxOutputTokens);
            Assert.True(api.Saved.SetInstallationDefault);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidBudgetsAndSaveFailuresCannotAdvance(bool invalidBudget)
    {
        var api = new Api { FailSave = !invalidBudget };
        var html = await Render(api, async (_, capture) =>
        {
            if (invalidBudget) { Set(capture.Form!, "_output", 256000); await Invoke(capture.Form!, "DirtyAsync"); }
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Equal(0, api.Continued);
        });
        Assert.Contains(invalidBudget ? "Maximum output must be smaller" : "Choose a supported model", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("traceId", html);
    }

    [Fact]
    public async Task ReturningToAssistantRestoresSavedValuesAndDiscoveryCanFailIndependently()
    {
        var api = new Api { Existing = true, FailCatalog = true };
        var html = await Render(api, async (_, capture) =>
        {
            Assert.Equal("Atlas", Get<string>(capture.Form!, "_name"));
            Assert.Equal("saved-model", Get<string>(capture.Form!, "_model"));
            Assert.Equal(64000, Get<int>(capture.Form!, "_context"));
            Assert.Equal(8000, Get<int>(capture.Form!, "_output"));
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Equal(7, api.Saved!.ExpectedRevision);
            Assert.Equal(1, api.Continued);
        });
        Assert.Contains("Model discovery is unavailable", html);
    }

    [Fact]
    public async Task FailedLoadMustBeRetriedBeforeSaving()
    {
        var api = new Api { FailLoad = true };
        await Render(api, async (_, capture) =>
        {
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Null(api.Saved);
            api.FailLoad = false;
            await capture.Form!.ReloadAsync();
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Equal(1, api.Continued);
        });
    }

    [Fact]
    public async Task MissingProviderPreventsSaving()
    {
        var api = new Api { NoProviders = true };
        var html = await Render(api, async (_, capture) =>
        {
            await Invoke(capture.Step!, "ContinueAsync");
            Assert.Null(api.Saved); Assert.Equal(0, api.Continued);
        });
        Assert.Contains("Connect a provider in the AI models step", html);
    }

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Private)!.GetValue(o)!;
    private static void Set(object o, string field, object value) => o.GetType().GetField(field, Private)!.SetValue(o, value);
    private static Task Invoke(object o, string method) => (Task)o.GetType().GetMethod(method, Private)!.Invoke(o, null)!;
    private static async Task<string> Render(Api api, Func<HtmlRenderer, Capture, Task> action)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJs>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        var capture = new Capture(); services.AddSingleton<IComponentActivator>(capture);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PersonalAssistantSetupStep>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OnContinue"] = EventCallback.Factory.Create(new object(), () => { Assert.NotNull(api.Saved); api.Continued++; }) }));
            await action(renderer, capture);
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(capture.Step, null);
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(capture.Form, null);
            await Task.Yield();
            return component.ToHtmlString();
        });
    }
    private sealed class Capture : IComponentActivator
    {
        public PersonalAssistantSettingsForm? Form; public PersonalAssistantSetupStep? Step;
        public IComponent CreateInstance(Type t)
        {
            var c = (IComponent)Activator.CreateInstance(t)!;
            if (c is PersonalAssistantSettingsForm f) Form = f;
            if (c is PersonalAssistantSetupStep s) Step = s;
            return c;
        }
    }
    private sealed class Api : HttpMessageHandler
    {
        public Guid Provider = Guid.NewGuid(); public int Continued;
        public bool Existing, FailSave, FailCatalog, FailLoad, NoProviders;
        public ConfigurePersonalAssistantRequest? Saved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/personal-assistant/settings")
            {
                if (FailLoad && r.Method == HttpMethod.Get) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { detail = "Settings are temporarily unavailable." }) };
                if (r.Method == HttpMethod.Put)
                {
                    if (FailSave) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = "Choose a supported model.", traceId = "hidden" }) };
                    Saved = await r.Content!.ReadFromJsonAsync<ConfigurePersonalAssistantRequest>(token);
                }
                var budgets = Existing ? new Dictionary<string, JsonElement> { ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(64000), ["maxOutputTokens"] = JsonSerializer.SerializeToElement(8000) } : [];
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PersonalAssistantSettings(Existing || Saved is not null ? Guid.NewGuid() : null,
                    Existing ? Provider : Saved?.ProviderId, Existing ? "saved-model" : Saved?.Model, Existing ? 7 : 0, Existing || Saved is not null, null, [], budgets, Existing ? "Atlas" : Saved?.Name ?? "AI Assistant")) };
            }
            if (path == "/api/llm-provider-profiles") return new(HttpStatusCode.OK) { Content = JsonContent.Create(NoProviders ? Array.Empty<LlmProviderProfileResponse>() :
                new[] { new LlmProviderProfileResponse(Provider, "Local models", LlmProviderType.Ollama, "http://localhost:11434", null, "model", null, 256000, 128000, true, true, true, false, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) }) };
            return new(FailCatalog ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = JsonContent.Create(new { models = Array.Empty<ModelDescriptor>(), succeeded = true }) };
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost/", "http://localhost/setup"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => InvokeAsync<T>(id, args);
    }
}
