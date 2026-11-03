using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Llm;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class PersonalAssistantRenderingTests
{
    [Theory]
    [InlineData("aurora")]
    [InlineData("gold")]
    public async Task FocusedDialogOffersNewAndExistingBusiness(string theme)
    {
        var html = await RenderAsync<NewBusinessDialog>(theme, new() { ["Visible"] = true }, dialog: true);
        Assert.Contains("Enterprise · New business", WebUtility.HtmlDecode(html));
        Assert.Contains("New business", html); Assert.Contains("Existing business", html);
        Assert.DoesNotContain("Personal Assistant model settings", html);
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("gold")]
    public async Task DashboardHasTipsSettingsDraftResumeAndDedicatedChat(string theme)
    {
        var html = await RenderAsync<CSweet.UI.Pages.PersonalAssistant>(theme, []);
        Assert.Contains("Helpful starting points", html); Assert.Contains("Pick up where you left off", html);
        Assert.Contains("Your AI Assistant", WebUtility.HtmlDecode(html)); Assert.Contains("Chat with your Personal Assistant", html);
        Assert.Contains("/?create=true&amp;draft=", html); Assert.Contains("Help me prepare a game business", html);
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("gold")]
    public async Task RepositoryFlowKeepsSourceSelectionAndApprovalVisible(string theme)
    {
        var html = await RenderAsync<RepositoryBusinessOnboarding>(theme, []);
        Assert.Contains("Connect GitHub", html); Assert.Contains("Upload repository ZIP", html);
        Assert.Contains("Allow my Personal Assistant", html); Assert.Contains("Understand my project", html);
        Assert.DoesNotContain("Set up GitHub for this installation", html);
        Assert.DoesNotContain("Configure your Personal Assistant", html);
        Assert.DoesNotContain("href=\"personal-assistant\"", html);
        Assert.DoesNotContain("/settings/source-control", html);
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("gold")]
    public async Task GitHubSetupResumesInsideTheBusinessWizard(string theme)
    {
        var html = await RenderAsync<RepositoryBusinessOnboarding>(theme, new() { [nameof(RepositoryBusinessOnboarding.ResumeGitHubSetup)] = true });
        Assert.Contains("GitHub setup", html);
        Assert.Contains("Continue to GitHub", html);
        Assert.Contains("Back to repositories", html);
        Assert.DoesNotContain("Upload repository ZIP", html);
        Assert.DoesNotContain("/settings/source-control", html);
        Assert.DoesNotContain("Configure your Personal Assistant", html);
    }

    [Fact]
    public async Task ReturningWithOneAuthorizedAccountLoadsItsRepositoryPicker()
    {
        var api = new Api(connected: true);
        var html = await RenderAsync<RepositoryBusinessOnboarding>("aurora", [], api: api);
        Assert.Contains("csweet/csweet", html);
        Assert.Contains(api.Requests, x => x.EndsWith("/github/repositories?installationId=123"));
        Assert.DoesNotContain(api.Requests, x => x.Contains("platform-setup") || x.Contains("personal-assistant/settings"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnteringRepositoryWizardKeepsDialogOpenAcrossDraftLoadAndParentRerender(bool resume)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        var api = new Api(); var navigation = new Navigation(); var capture = new ActivatorCapture();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<IAppRealtimeConnection, OfflineRealtime>(); services.AddSingleton<IComponentActivator>(capture);
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var suppliedDraft = resume ? api.DraftId : (Guid?)null;
            var parameters = new Dictionary<string, object?> { [nameof(NewBusinessDialog.Visible)] = true, [nameof(NewBusinessDialog.DraftId)] = suppliedDraft };
            await renderer.RenderComponentAsync<NewBusinessDialog>(ParameterView.FromDictionary(parameters));
            var wizard = capture.Wizard!;
            if (!resume)
            {
                typeof(NewBusinessDialog).GetMethod("ShowExistingBusiness", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null);
            }
            var content = await renderer.RenderComponentAsync<DialogPreview>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = capture.Dialog!.DialogContent }));
            Assert.Contains("Connect GitHub", content.ToHtmlString());
            Assert.True(capture.Dialog.GetState(x => x.Visible));
            Assert.Empty(navigation.Navigations);

            // The parent still supplies its original parameter after unrelated rerenders.
            await wizard.SetParametersAsync(ParameterView.FromDictionary(parameters));
            await capture.Preview!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = capture.Dialog.DialogContent }));
            Assert.Equal(api.DraftId, capture.Repository!.DraftId);
            Assert.Equal(suppliedDraft, wizard.DraftId);
            Assert.Equal(resume ? 0 : 1, api.Requests.Count(x => x == "/api/repository-onboarding/"));
            Assert.True(capture.Dialog.GetState(x => x.Visible));
            Assert.Empty(navigation.Navigations);
        });
    }

    private static async Task<string> RenderAsync<T>(string theme, Dictionary<string, object?> parameters, bool dialog = false, HttpMessageHandler? api = null) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<IAppRealtimeConnection, OfflineRealtime>();
        services.AddSingleton(new HttpClient(api ?? new Api()) { BaseAddress = new Uri("http://localhost/") });
        var activator = new ActivatorCapture(); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            if (!dialog) return component.ToHtmlString();
            var content = await renderer.RenderComponentAsync<DialogPreview>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = activator.Dialog!.DialogContent }));
            return content.ToHtmlString();
        });
        var wrapped = $"<div class=\"mud-layout\" data-appearance=\"{theme}\"><div class=\"aurora-page business-workspace\">{html}</div></div>";
        if (Environment.GetEnvironmentVariable("CSWEET_ONBOARDING_PREVIEWS") is { } path)
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, $"{typeof(T).Name}-{theme}.html"), wrapped);
        }
        return wrapped;
    }
    private sealed class ActivatorCapture : IComponentActivator
    {
        public MudDialog? Dialog; public NewBusinessDialog? Wizard; public RepositoryBusinessOnboarding? Repository; public DialogPreview? Preview;
        public IComponent CreateInstance(Type type)
        {
            var value = (IComponent)Activator.CreateInstance(type)!;
            if (value is MudDialog d) Dialog = d;
            if (value is NewBusinessDialog w) Wizard = w;
            if (value is RepositoryBusinessOnboarding r) Repository = r;
            if (value is DialogPreview p) Preview = p;
            return value;
        }
    }
    private sealed class DialogPreview : ComponentBase
    {
        [Parameter] public RenderFragment? Content { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "div"); b.AddAttribute(1, "class", "mud-dialog mud-dialog-width-md"); b.AddContent(2, Content); b.CloseElement(); }
    }
    private sealed class Navigation : NavigationManager
    {
        public List<string> Navigations { get; } = [];
        public Navigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Record(uri);
        protected override void NavigateToCore(string uri, NavigationOptions options) => Record(uri);
        private void Record(string uri) { Navigations.Add(uri); Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }
    private sealed class OfflineRealtime : IAppRealtimeConnection
    {
        public event Action<CSweet.Contracts.Realtime.AppRealtimeEventEnvelope>? EventReceived { add { } remove { } }
        public event Action? Reconnected { add { } remove { } }
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => InvokeAsync<T>(id, args);
    }
    private sealed class Api(bool connected = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private readonly Guid user = Guid.NewGuid(), provider = Guid.NewGuid(), draft = Guid.NewGuid();
        public Guid DraftId => draft;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            var path = r.RequestUri!.AbsolutePath;
            Requests.Add(r.RequestUri.PathAndQuery);
            object value = path switch
            {
                "/api/personal-assistant/settings" => new PersonalAssistantSettings(Guid.NewGuid(), provider, "model", 1, true, null, [], new Dictionary<string, System.Text.Json.JsonElement>()),
                "/api/personal-assistant/messages" => new[] { new PersonalAssistantMessageResponse(Guid.NewGuid(), "user", "Help me prepare a game business", "Completed", DateTimeOffset.UtcNow, null) },
                "/api/llm-provider-profiles" => new[] { new LlmProviderProfileResponse(provider, "Local models", LlmProviderType.Ollama, "http://localhost:11434", null, "model", null, 65536, 8192, true, true, true, false, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) },
                "/api/repository-onboarding/" when r.Method == HttpMethod.Post => Draft(),
                "/api/repository-onboarding/" => new[] { Draft() },
                _ when path == $"/api/repository-onboarding/{draft:D}" => Draft(),
                "/api/source-control/platform-setup/" => new PlatformSourceControlSetupResponse(new(false, false, null), null),
                _ when path.EndsWith("/github/accounts") => connected ? new[] { new OnboardingGitHubAccount(123, "csweet", "Organization") } : Array.Empty<OnboardingGitHubAccount>(),
                _ when path.EndsWith("/github/repositories") => new[] { new OnboardingGitHubRepository(456, "csweet/csweet", true, false, true, null) },
                _ => new { models = Array.Empty<ModelDescriptor>(), succeeded = true }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
        }
        private RepositoryOnboardingDraftResponse Draft() => new(draft, "Sources", 1, 1, [], null, null, null, null, null, null, DateTimeOffset.UtcNow);
    }
}
