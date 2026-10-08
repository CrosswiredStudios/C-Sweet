using CSweet.Contracts.Core;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class ProjectApprovalRenderingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RendersHumanReviewAndOnlyAuthorizedControls(bool compact, bool authorized)
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, _, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var item = (await new CSweet.Infrastructure.Core.ProjectApprovalReader(db).ReadAsync(owner.OrganizationId, owner.Id, proposal.Id))!;
        item = item with { ProjectCreation = item.ProjectCreation! with { Outcome = "<script>unsafe</script>" } };
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton(new HttpClient());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ProjectCreationApprovalCard>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["OrganizationId"] = owner.OrganizationId,
                ["Item"] = item, ["Compact"] = compact, ["CanInteract"] = authorized }))).ToHtmlString());
        Assert.Contains("Create project: Prism Break", html);
        Assert.Contains("Not specified", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        if (compact)
        {
            Assert.Contains("More info", html);
            Assert.Contains($"approvalId={proposal.Id:D}", html);
            Assert.DoesNotContain("Payload binding", html);
            Assert.Equal(authorized, html.Contains(">Deny<", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("What you’re approving", System.Net.WebUtility.HtmlDecode(html));
            Assert.Contains("Request changes", html);
            Assert.Matches("<summary[^>]*>Technical details</summary>", html);
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
