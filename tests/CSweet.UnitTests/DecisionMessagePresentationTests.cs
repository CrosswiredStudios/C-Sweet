using CSweet.UI.Components;
using CSweet.UI.Components.Communications;
using CSweet.UI.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed class DecisionMessagePresentationTests
{
    [Theory]
    [InlineData("Choose **the plan**.")]
    [InlineData("Choose **the plan**.\n\nPlease review this project decision. Your response will be recorded against the project. Select an option below.")]
    [InlineData("Choose **the plan**.\r\n\r\nPlease review this project decision. Your response will be recorded against the project. Use Something else to provide specific direction.")]
    public async Task Attached_question_is_not_repeated_in_the_message_body(string content)
    {
        var html = await RenderAsync<DecisionMessageBody>(new() { ["Content"] = content, ["DecisionPrompt"] = "Choose **the plan**." });
        Assert.DoesNotContain("Choose", html);
        Assert.DoesNotContain("Please review", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A different question")]
    public async Task Ordinary_message_context_is_preserved(string? prompt)
    {
        var html = await RenderAsync<DecisionMessageBody>(new() { ["Content"] = "**Important context**", ["DecisionPrompt"] = prompt });
        Assert.Contains("<strong>Important context</strong>", html);
    }

    [Fact]
    public async Task Additional_context_after_a_question_is_not_silently_hidden()
    {
        var html = await RenderAsync<DecisionMessageBody>(new() { ["Content"] = "Choose the plan.\n\nBudget evidence: **$100**.", ["DecisionPrompt"] = "Choose the plan." });
        Assert.Contains("Budget evidence", html);
        Assert.Contains("<strong>$100</strong>", html);
    }

    [Fact]
    public async Task Full_question_uses_safe_markdown_and_remains_unclipped()
    {
        var html = await RenderAsync<DecisionPrompt>(new() { ["Content"] = "## Choose the plan\n\n**Recommendation**\n\n- Keep scope\n- Plan QA\n\n[unsafe link](javascript:alert%281%29)\n\n<script>unsafe()</script>" });
        Assert.Contains("<h2", html);
        Assert.Contains("<strong>Recommendation</strong>", html);
        Assert.Contains("<li>Plan QA</li>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("decision-preview-text", html);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddCSweetLocalization();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
