using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Plugins;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class ManagedConnectionIdentityUiTests
{
    [Theory]
    [InlineData(false, "https://connections.c-sweet.com", "Official C-Sweet service address")]
    [InlineData(true, "https://connections.c-sweet.com", "Official C-Sweet service address")]
    [InlineData(false, "https://custom.example", "Custom self-hosted service")]
    [InlineData(true, "https://custom.example", "Custom self-hosted service")]
    public async Task ConfiguredOriginAndAppOwnedIdentityStayVisibleBeforeAndAfterPairing(bool paired, string origin, string label)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<NavigationManager, Navigation>(); services.AddSingleton<IJSRuntime, NoJavaScript>();
        using var http = new HttpClient(new StatusHandler(new(true, paired, origin, "https://host.example/settings/plugins/managed-return")))
        { BaseAddress = new("https://host.example/") };
        services.AddSingleton(http);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ManagedConnections>()).ToHtmlString());
        Assert.Contains(origin, html); Assert.Contains(label, html);
        if (origin == "https://custom.example") Assert.DoesNotContain("Official C-Sweet service address", html);
    }

    private sealed class StatusHandler(ManagedConnectionStatus status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status) });
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://host.example/", "https://host.example/settings/connections");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
