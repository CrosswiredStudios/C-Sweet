using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;
public sealed class IssueAttentionCardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullEvidenceIsRetainedInNativeCollapsedDetailsAndRetryPermissionAloneAddsNoAction(bool requiresAction)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization(); services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient { BaseAddress = new("http://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var detail = "Full diagnostic evidence " + new string('x', 12000);
        var item = new OverviewIssueCard("case", Guid.NewGuid(), Guid.NewGuid(), "Brickwave", "Technical review needs reconciliation",
            "The manager is handling the remaining responsibility.", "Gabriel", "Review current evidence", requiresAction, false,
            [new(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Gabriel", "Review feasibility", "Original failure", 2, true, "/work")], [], [], detail);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<IssueAttentionCard>(
            ParameterView.FromDictionary(new Dictionary<string,object?> { ["OrganizationId"] = Guid.NewGuid(), ["Item"] = item }))).ToHtmlString());
        Assert.Contains("<details class=\"issue-details\"", html); Assert.Contains("<summary", html); Assert.Contains(detail, html);
        Assert.DoesNotContain("Full diagnostic evidence", html[..html.IndexOf("<details", StringComparison.Ordinal)]);
        Assert.Equal(requiresAction, html.Contains("Retry after review", StringComparison.Ordinal));
        Assert.Equal(!requiresAction, html.Contains("No action needed from you.", StringComparison.Ordinal));
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string id, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
