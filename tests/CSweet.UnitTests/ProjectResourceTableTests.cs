using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;
public sealed class ProjectResourceTableTests
{
    [Fact]
    public async Task ResourcesRenderThroughComponentChildContentWithoutRenderTreeFailure()
    {
        var resource = new ProjectInspectionResource(Guid.NewGuid(), "Decision", "direction", "Choose browser targets", "Pending",
            null, null, null, null, null, null, DateTimeOffset.UtcNow, "/project", JsonSerializer.SerializeToElement(new {}));
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ProjectResourceTable>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Resources"] = new[] { resource } }))).ToHtmlString());
        Assert.Contains("Choose browser targets", html);
        Assert.Contains("Pending", html);
        Assert.Contains("href=", html);
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
