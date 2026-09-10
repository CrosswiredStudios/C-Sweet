using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Communications;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class PendingAgentQuestionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverviewGroupsAgentQuestionsAndInboxRendersResponseControls(bool summary)
    {
        var org = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var questions = Enumerable.Range(1, 2).Select(i => new PendingAgentQuestionResponse(Guid.NewGuid(), agent,
            "Producer", new ExecutiveDecisionCardResponse(Guid.NewGuid(), $"Project question {i}", "Pending",
                [new("a", "Proceed", null, true), new("b", "Wait", null, false)], "a", null, null,
                DateTimeOffset.UtcNow, null))).ToList();
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(new QuestionsHandler(questions)) { BaseAddress = new Uri("http://localhost/") });
        services.AddSingleton<AppRealtimeState>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<PendingAgentQuestions>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["OrganizationId"] = org, ["SummaryOnly"] = summary }))).ToHtmlString());
        Assert.Contains("Producer", html);
        Assert.Contains("2 pending questions", html);
        if (summary)
        {
            Assert.Contains("2 awaiting response", html);
            Assert.Contains($"/organizations/{org}/approvals?tab=decisions", html);
            Assert.DoesNotContain("Send response", html);
        }
        else
        {
            Assert.Contains("Project question 1", html);
            Assert.Contains("Project question 2", html);
            Assert.Contains("Send response", html);
            Assert.Contains("View conversation", html);
            Assert.Contains("type=\"radio\"", html);
        }
    }
    private sealed class QuestionsHandler(List<PendingAgentQuestionResponse> questions) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(questions) });
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
