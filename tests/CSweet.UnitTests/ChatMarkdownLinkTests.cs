using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ChatMarkdownLinkTests
{
    [Fact]
    public async Task Application_links_open_in_a_new_tab_with_opener_isolation_and_raw_html_stays_disabled()
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ChatMarkdown>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(ChatMarkdown.Content)] = "[Open application ↗](http://127.0.0.1:43210/)\n<script>alert(1)</script>" }));
            return component.ToHtmlString();
        });
        Assert.Contains("target=\"_blank\"", html); Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.DoesNotContain("<script>", html);
    }
}
