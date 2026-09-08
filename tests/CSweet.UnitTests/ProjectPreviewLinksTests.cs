using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ProjectPreviewLinksTests
{
    [Theory]
    [InlineData("Ready", "http://127.0.0.1:54321/preview/", 5, true)]
    [InlineData("Requested", "http://127.0.0.1:54321/preview/", 5, false)]
    [InlineData("Failed", "http://127.0.0.1:54321/preview/", 5, false)]
    [InlineData("Ready", "http://127.0.0.1:54321/preview/", -5, false)]
    [InlineData("Ready", "javascript:alert(1)", 5, false)]
    [InlineData("Ready", "http://user:password@localhost/", 5, false)]
    public async Task OnlyReadyUnexpiredWebPreviewsHaveAnOpenControl(string status, string url, int minutes, bool open)
    {
        var build = Guid.NewGuid();
        var resource = new ProjectInspectionResource(Guid.NewGuid(), "Preview", "web-static", "web-static", status,
            null, null, null, null, null, null, DateTimeOffset.UtcNow, "/project",
            JsonSerializer.SerializeToElement(new { BuildId = build, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes), AccessReference = url }));
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProjectPreviewLinks>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ProjectPreviewLinks.Resources)] = new[] { resource } }));
            return output.ToHtmlString();
        });
        Assert.Contains(build.ToString(), html);
        Assert.Equal(open, html.Contains("Open playable preview"));
        if (open) { Assert.Contains("target=\"_blank\"", html); Assert.Contains("noopener noreferrer", html); }
        else Assert.DoesNotContain("href=", html);
    }
}
