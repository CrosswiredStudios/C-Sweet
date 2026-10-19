using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using System.Net;
using System.Net.Http.Json;
using CSweet.Agent.SDK;
using CSweet.UI.Components.Hiring;
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

public sealed class AgentHireDialogRenderingTests
{
    [Fact]
    public async Task OpeningFromAnotherComponentRendersBeforeReadsCompleteAndShowsFailures()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activator = new CapturingActivator();
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new HttpClient(new Handler(entered, release)) { BaseAddress = new Uri("http://localhost/") });
        services.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
        services.AddScoped<AppRealtimeState>();
        services.AddScoped<AgentHireOperationState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AgentHireDialog>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AgentHireDialog.OrganizationId)] = Guid.NewGuid()
            })));
        var dialog = Assert.IsType<MudDialog>(activator.Dialog);
        Assert.False(dialog.GetState(component => component.Visible));
        var agent = new AvailableAgent("chief", "chief", AgentCatalogSource.LocalDirectory, [],
            AgentAvailabilityState.AvailableToInstall, null, "Evelyn Brooks", "Chief", "C-Sweet", "Management",
            [], [], [], null, null, null, 0, null, null, 1, "FirstParty");
        var opening = renderer.Dispatcher.InvokeAsync(() => activator.Review!.OpenAsync(agent));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await renderer.Dispatcher.InvokeAsync(() => Assert.True(dialog.GetState(component => component.Visible)));
        }
        finally
        {
            release.TrySetResult();
        }
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        // The final render must also propagate errors into the dialog content,
        // even though OpenAsync was invoked without a component event callback.
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var content = await renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(FragmentHost.ChildContent)] = dialog.DialogContent }));
            Assert.Contains("Preview unavailable", content.ToHtmlString());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileUsesPortraitAndOnlyDeclaredModelSettings(bool declaresModel)
    {
        await using var fixture = await ProfileFixture.CreateAsync(declaresModel
            ? [new() { Key = "model", Type = "model", Label = "Model", DefaultValue = JsonSerializer.SerializeToElement("qwen3.8-27b") }]
            : []);
        var html = await fixture.HtmlAsync();
        Assert.Contains("video-game-producer-v1.jpg", html);
        Assert.Contains("Edit employee name", html);
        Assert.Equal(declaresModel, html.Contains("profile-model-title"));
        Assert.Equal(declaresModel ? 1 : 0, fixture.Handler.ProviderReads);
        Assert.DoesNotContain("hire-agent-facts", html);
        Set(fixture.Review, "_portraitFailed", true);
        Assert.Contains("agent-default.svg", await fixture.HtmlAsync());
    }

    [Fact]
    public async Task HiddenModelIsOmittedAndRequiredAgentSettingsRemainVisible()
    {
        await using var fixture = await ProfileFixture.CreateAsync([
            new() { Key = "model", Type = "model", Label = "Model", VisibleWhenFieldKey = "mode", VisibleWhenValue = "custom" },
            new() { Key = "endpoint", Label = "Service endpoint", Required = true }
        ]);
        var html = await fixture.HtmlAsync();
        Assert.DoesNotContain("profile-model-title", html);
        Assert.Contains("Service endpoint", html);
        Assert.Contains("Agent settings", html);
        Assert.True(Get<bool>(fixture.Review, "HasMissingRequiredConfiguration"));
    }

    [Theory]
    [InlineData(0, "project.plan", "project.read")]
    [InlineData(1, "project.read", "project.plan")]
    [InlineData(2, "work.changed", "project.plan")]
    [InlineData(3, "None requested.", "project.plan")]
    public async Task AccessTabsShowCountsAndOnlySelectedCategory(int tab, string shown, string hidden)
    {
        await using var fixture = await ProfileFixture.CreateAsync([]);
        Set(fixture.Review, "_grantDetailsOpen", true);
        Set(fixture.Review, "_accessTab", tab);
        var html = await fixture.HtmlAsync();
        foreach (var label in new[] { "Capabilities (1)", "Platform (1)", "Events (1)", "Network (0)" })
            Assert.Contains(label, html);
        Assert.Contains(shown, html);
        Assert.DoesNotContain(hidden, html);
    }

    [Fact]
    public async Task InlineNameSaveCancelAndSubmissionPreserveConfiguration()
    {
        await using var fixture = await ProfileFixture.CreateAsync([
            new() { Key = "limit", Type = "number", Label = "Output limit", DefaultValue = JsonSerializer.SerializeToElement(128000) }
        ]);
        await fixture.InvokeAsync("BeginNameEdit");
        Set(fixture.Review, "_nameDraft", "  ");
        await fixture.InvokeAsync("SaveName");
        Assert.True(Get<bool>(fixture.Review, "_editingName"));
        await fixture.InvokeAsync("ConfirmHireAsync");
        Assert.Single(fixture.Handler.Previews);
        Set(fixture.Review, "_nameDraft", "Cancelled");
        await fixture.InvokeAsync("CancelNameEdit");
        Assert.Equal("Gabriel Reyes", Get<string>(fixture.Review, "_employeeDisplayName"));
        await fixture.InvokeAsync("BeginNameEdit");
        Set(fixture.Review, "_nameDraft", "  Gabriel Studio  ");
        await fixture.InvokeAsync("SaveName");
        Assert.Equal("Gabriel Studio", Get<string>(fixture.Review, "_employeeDisplayName"));
        Get<Dictionary<string, object?>>(fixture.Review, "_configurationValues")["limit"] = 64000m;
        await fixture.InvokeAsync("ConfirmHireAsync");
        Assert.Equal(2, fixture.Handler.Previews.Count);
        Assert.Equal("Gabriel Studio", fixture.Handler.Previews.Last().EmployeeDisplayName);
        Assert.NotNull(fixture.Handler.Previews.Last().SupersedesWorkflowId);
        Assert.Equal(64000m, Get<Dictionary<string, object?>>(fixture.Review, "_configurationValues")["limit"]);
    }

    [Fact]
    public async Task ExportProfilePreviewWhenRequested()
    {
        var directory = Environment.GetEnvironmentVariable("CSWEET_HIRE_PREVIEWS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await using var fixture = await ProfileFixture.CreateAsync([
            new() { Key = "model", Type = "model", Label = "Model", DefaultValue = JsonSerializer.SerializeToElement("qwen3.8-27b") },
            new() { Key = "limit", Type = "number", Label = "Maximum output tokens", DefaultValue = JsonSerializer.SerializeToElement(128000) }
        ], recommendation: true);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile.html"), await fixture.HtmlAsync());
        Set(fixture.Review, "_grantDetailsOpen", true);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-access.html"), await fixture.HtmlAsync());
    }

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target))!;

    private sealed class ProfileFixture(ServiceProvider services, HtmlRenderer renderer, CapturingActivator activator, ProfileHandler handler) : IAsyncDisposable
    {
        public AgentHireDialog Review => activator.Review!;
        public ProfileHandler Handler => handler;
        public static async Task<ProfileFixture> CreateAsync(IReadOnlyList<PluginConfigurationField> fields, bool recommendation = false)
        {
            var activator = new CapturingActivator();
            var handler = new ProfileHandler(fields);
            var collection = new ServiceCollection().AddLogging();
            collection.AddMudServices(); collection.AddCSweetLocalization();
            collection.AddSingleton<IJSRuntime, NoJavaScript>();
            collection.AddSingleton<NavigationManager, TestNavigation>();
            collection.AddSingleton<IComponentActivator>(activator);
            collection.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
            collection.AddScoped<ILlmProviderApiClient, LlmProviderApiClient>();
            collection.AddScoped<AppRealtimeState>();
            collection.AddScoped<AgentHireOperationState>();
            var services = collection.BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AgentHireDialog>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(AgentHireDialog.OrganizationId)] = Guid.NewGuid() })));
            var agent = new AvailableAgent("producer", "producer", AgentCatalogSource.LocalDirectory, [],
                AgentAvailabilityState.AvailableToInstall, null, "Gabriel Reyes", "Turns your creative vision into a clear production plan, coordinating the team from first pitch to launch.",
                "C-Sweet", "Production", [], [], [], null, null, null, 0, null, null, 1, "FirstParty")
                { ImageUrl = "_content/CSweet.UI/images/agents/video-game-producer-v1.jpg" };
            await renderer.Dispatcher.InvokeAsync(() => activator.Review!.OpenAsync(agent, recommendationId: recommendation ? Guid.NewGuid() : null));
            return new(services, renderer, activator, handler);
        }
        public Task InvokeAsync(string name) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AgentHireDialog).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Review, null);
            if (result is Task task) await task;
        });
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var content = await renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(FragmentHost.ChildContent)] = activator.Dialog!.DialogContent }));
            return content.ToHtmlString();
        });
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); }
    }

    private sealed class ProfileHandler(IReadOnlyList<PluginConfigurationField> fields) : HttpMessageHandler
    {
        private readonly Guid _managerId = Guid.NewGuid();
        private readonly Guid _teamId = Guid.NewGuid();
        public int ProviderReads { get; private set; }
        public List<PreviewMarketplaceHireRequest> Previews { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/preview"))
            {
                var body = (await request.Content!.ReadFromJsonAsync<PreviewMarketplaceHireRequest>(token))!;
                Previews.Add(body);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MarketplaceHirePreviewResponse(
                    Guid.NewGuid(), "producer", "Gabriel Reyes", body.EmployeeDisplayName!, "Video Game Producer",
                    _managerId, "C-Sweet", "Platform verified", null, null,
                    ["project.plan"], ["project.read"], ["work.changed"], [],
                    "Install this agent with the reviewed access, then create the employee.", "Preview")
                    { ConfigurationFields = fields, TeamId = body.RecommendationId.HasValue ? _teamId : body.TeamId }) };
            }
            if (path.EndsWith("/users"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new OrganizationUserResponse(
                    _managerId, Guid.NewGuid(), null, null, null, "Naomi Chen", null, 0, 0, DateTimeOffset.UtcNow) }) };
            if (path.EndsWith("/teams"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { teams = new[] { new { id = _teamId, name = "Video Game Team" } } }) };
            if (request.Method == HttpMethod.Post)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = "Test submission captured" }) };
            ProviderReads++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
        }
    }
    public sealed class FragmentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public AgentHireDialog? Review { get; private set; }
        public MudDialog? Dialog { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is AgentHireDialog review) Review = review;
            if (component is MudDialog dialog) Dialog = dialog;
            return component;
        }
    }

    private sealed class Handler(TaskCompletionSource entered, TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/users"))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            if (request.Method == HttpMethod.Post)
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = "Preview unavailable" }) };
            var json = request.RequestUri.AbsolutePath.Contains("/teams") ? "{\"teams\":[]}" : "[]";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
