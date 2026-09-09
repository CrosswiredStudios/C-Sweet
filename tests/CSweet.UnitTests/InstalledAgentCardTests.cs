using CSweet.Contracts.Agents;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class InstalledAgentCardTests
{
    [Theory]
    [InlineData("Queued", false)]
    [InlineData("Cloning", false)]
    [InlineData("Building", false)]
    [InlineData("Failed", true)]
    public async Task ActiveOrJustRequestedBuild_RendersUpdatingChipAndProgress(string status, bool pending)
    {
        var html = await Render(Installation(status), pending);
        Assert.Contains("Updating", html);
        Assert.Contains("Show build progress", html);
        Assert.Contains("Restore dependencies", html);
        Assert.DoesNotContain(">Rebuild<", html);
        Assert.Contains("View details for Agent", html);
    }

    [Fact]
    public async Task FailedBuild_RendersRebuildAndVersion()
    {
        var html = await Render(Installation("Failed"));
        Assert.Contains("Rebuild", html);
        Assert.Contains("v1.2.0", html);
        Assert.Contains("Build failed", html);
        Assert.DoesNotContain("building-button", html);
    }

    [Fact]
    public async Task UpdatePreview_EncodesUntrustedNotesAndShowsTargetVersion()
    {
        var update = new AgentDefinitionUpdateAvailabilityResponse(Guid.NewGuid(), "agent", "Agent", "1.2.0", "old",
            true, Guid.NewGuid(), "1.3.0", "new", DateTimeOffset.UtcNow)
        { ReleaseNotes = "<script>alert('x')</script>" };
        var html = await Render(Installation("Succeeded"), update: update);
        Assert.Contains("Update to v1.3.0", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("https://example.com/avatar.jpg", html);
    }

    [Fact]
    public async Task CompletedBuild_DoesNotAddProgressMessageToCard()
    {
        var html = await Render(Installation("Succeeded"), progressTitle: "Agent ready");
        var card = html[..html.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.DoesNotContain("Agent ready", card);
        Assert.DoesNotContain("Available for hire", card);
        Assert.Contains("View details for Agent", card);
        Assert.Contains("Configure global defaults for Agent", card);
        Assert.DoesNotContain(">Details<", card);
    }

    private static AgentInstallationResponse Installation(string status) => new(
        Guid.NewGuid(), Guid.NewGuid(), "global", "agent", "Agent", "1.2.0", "Publisher", "commit", true,
        [], [], [], [], [], 1024, 50,
        new AgentScheduleResponse(Guid.Empty, "OnDemand", 3600, null, null, null, null, 600, 0, 0, null, "Skip", true),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new AgentBuildSummaryResponse(Guid.NewGuid(), status, 1, DateTimeOffset.UtcNow, null, null, false, null,
            [new("restore", "Restore dependencies", "InProgress", "Resolving packages", null, null, null)]))
        { SetupState = "Available", ImageUrl = "https://example.com/avatar.jpg", RoleName = "Researcher" };

    private static async Task<string> Render(AgentInstallationResponse installation, bool pending = false,
        AgentDefinitionUpdateAvailabilityResponse? update = null, string? progressTitle = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<InstalledAgentCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(InstalledAgentCard.Installation)] = installation,
                [nameof(InstalledAgentCard.BuildPending)] = pending,
                [nameof(InstalledAgentCard.Update)] = update,
                [nameof(InstalledAgentCard.ProgressTitle)] = progressTitle
            }));
            return component.ToHtmlString();
        });
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
