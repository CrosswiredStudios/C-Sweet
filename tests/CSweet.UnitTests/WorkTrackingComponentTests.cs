using System.Net;
using System.Net.Http.Json;
using CSweet.UI.Components.WorkBoards;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class WorkTrackingComponentTests
{
    [Theory]
    [InlineData(false, false, "Follow")]
    [InlineData(false, true, "Following")]
    [InlineData(true, false, null)]
    [InlineData(true, true, "Following")]
    public async Task FullAndLimitedViewsExposeOnlyAuthorizedControls(bool limited, bool following, string? button)
    {
        var item = Guid.NewGuid();
        var snapshot = new WorkTrackingSnapshot(item, following, limited, limited ? null : "/canonical-work",
            [new(Guid.NewGuid(), item, Guid.NewGuid(), 2, "Blocked", "Approval is needed.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var services = new ServiceCollection().AddLogging(); services.AddCSweetLocalization();
        services.AddSingleton(new HttpClient(new Handler(snapshot)) { BaseAddress = new("http://localhost") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WorkTracking>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WorkTracking.OrganizationId)] = Guid.NewGuid(),
                [nameof(WorkTracking.ItemId)] = item, [nameof(WorkTracking.ShowUpdates)] = true }))).ToHtmlString());
        Assert.Contains("Approval is needed.", html);
        if (button is null) Assert.DoesNotContain("<button", html); else Assert.Contains($">{button}</button>", html);
        if (limited) { Assert.DoesNotContain("/canonical-work", html); Assert.Contains("Updates shared with you", html); }
        else Assert.Contains("/canonical-work", html);
    }
    private sealed class Handler(WorkTrackingSnapshot snapshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) });
    }
}
