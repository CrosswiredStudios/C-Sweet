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

public sealed class ConnectorApprovalCardRenderingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeReviewEscapesExternalTextAndHidesUnauthorizedDecisions(bool authorized)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(new NoRequests()));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var action = new ManagedAgentActionApprovalResponse(Guid.NewGuid(), "example.reply.v1", "channel", new string('a', 64),
            1, "key", true, "comment", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), AccountName: "Company channel",
            ReviewPayloadJson: JsonSerializer.Serialize(new { changes = new { reply = "<script>alert('bad')</script>" } }));
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ConnectorActionApprovalCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ConnectorActionApprovalCard.OrganizationId)] = Guid.NewGuid(),
                [nameof(ConnectorActionApprovalCard.Card)] = new ConnectorActionApprovalCardResponse(action, "Review reply", "Pending", "AwaitingApproval", authorized),
                [nameof(ConnectorActionApprovalCard.CanDecide)] = authorized
            }));
            return output.ToHtmlString();
        });
        Assert.Contains("Company channel", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        if (authorized) Assert.Contains("Request revision", html);
        else Assert.DoesNotContain("Request revision", html);
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rendering a review must never send a decision.");
    }
}
