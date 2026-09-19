using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
namespace CSweet.UnitTests;

public sealed class ProjectSetupRenderingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opening_the_creation_form_only_reads_options_and_respects_creation_permission(bool allowed)
    {
        var api = new SetupApi(allowed);
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("https://host.example.com/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ProjectSetup>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["OrganizationId"] = Guid.NewGuid() }))).ToHtmlString());
        Assert.Equal(HttpMethod.Get, Assert.Single(api.Requests));
        Assert.Contains("Project name", html); Assert.Contains("Project members", html); Assert.Contains("Create project", html);
        Assert.Contains("Cancel", html);
        Assert.Equal(!allowed, html.Contains("Ask an organization owner or manager"));
        if (!allowed) Assert.Contains("disabled", html);
    }
    private sealed class SetupApi(bool allowed) : HttpMessageHandler
    {
        public List<HttpMethod> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method);
            Assert.EndsWith("/project-setup/options", request.RequestUri!.AbsolutePath);
            var human = Guid.NewGuid();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ProjectSetupOptions(human, allowed,
                [new(human, "Human manager", "Human", null, true)], [], [])) });
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://host.example.com/", "https://host.example.com/projects/new");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
