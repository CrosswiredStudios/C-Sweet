using System.Net;
using System.Reflection;
using CSweet.UI.Localization;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class GitHubSetupReturnTests
{
    [Theory]
    [InlineData("verified")]
    [InlineData("failed")]
    public async Task ManifestCallbackResumesTheSameBusinessDraft(string result)
    {
        var draftId = Guid.NewGuid();
        var (target, js) = await RenderReturnAsync(result, draftId.ToString("D"));
        Assert.Equal($"/?create=true&draft={draftId:D}&resumeGitHubSetup=true", target);
        Assert.Contains("takeDraftId", js.Calls);
    }

    [Fact]
    public async Task OverlappingRendersConsumeTheDraftReturnHintOnlyOnce()
    {
        var draftId = Guid.NewGuid();
        var (target, js) = await RenderReturnAsync("verified", draftId.ToString("D"), overlapRenders: true);
        Assert.Equal($"/?create=true&draft={draftId:D}&resumeGitHubSetup=true", target);
        Assert.Single(js.Calls, x => x == "takeDraftId");
    }

    [Fact]
    public async Task NormalSettingsVisitDoesNotRedirectToRememberedDraft()
    {
        var (target, js) = await RenderReturnAsync(null, Guid.NewGuid().ToString("D"));
        Assert.Null(target);
        Assert.DoesNotContain("takeDraftId", js.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("../../other-page")]
    public async Task CallbackWithoutValidDraftStaysInSettings(string? draft)
    {
        var (target, _) = await RenderReturnAsync("verified", draft);
        Assert.Null(target);
    }

    private static async Task<(string? Target, JavaScript JS)> RenderReturnAsync(string? result, string? draft, bool overlapRenders = false)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        var js = new JavaScript(draft); var navigation = new Navigation(); var activator = new Capture();
        services.AddSingleton<IJSRuntime>(js); services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new HttpClient(new DeniedApi()) { BaseAddress = new("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<SourceControlSettings>();
            activator.Page!.GitHubSetupResult = result;
            var afterRender = typeof(SourceControlSettings).GetMethod("OnAfterRenderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (overlapRenders) js.ImportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = (Task)afterRender.Invoke(activator.Page, [true])!;
            if (overlapRenders)
            {
                Assert.False(first.IsCompleted);
                await (Task)afterRender.Invoke(activator.Page, [false])!;
                Assert.Single(js.Calls, x => x == "import");
                js.ImportGate!.SetResult();
            }
            await first;
        });
        return (navigation.Target, js);
    }

    private sealed class Capture : IComponentActivator
    {
        public SourceControlSettings? Page;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is SourceControlSettings page) Page = page;
            return component;
        }
    }
    private sealed class Navigation : NavigationManager
    {
        public string? Target;
        public Navigation() => Initialize("http://localhost/", "http://localhost/settings/source-control");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Target = uri;
    }
    public sealed class JavaScript(string? draft) : IJSRuntime, IJSObjectReference
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource? ImportGate { get; set; }
        public async ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "import" && ImportGate is not null) await ImportGate.Task;
            object? value = identifier == "import" ? this : identifier is "takeDraftId" or "rememberDraftId" ? draft : default(T);
            return (T)value!;
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => InvokeAsync<T>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DeniedApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
