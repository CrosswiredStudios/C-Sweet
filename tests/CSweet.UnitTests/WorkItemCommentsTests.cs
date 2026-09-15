using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.WorkBoards;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class WorkItemCommentsTests
{
    [Fact]
    public async Task RendersAuthorTimestampEditedMarkerAndAgentChip()
    {
        var viewerId = Guid.NewGuid();
        var (html, requests) = await Render(Payload(viewerId));

        Assert.Contains("/api/organizations/", Assert.Single(requests), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("collaboration", Assert.Single(requests), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dana Reed", html);
        Assert.Contains("Please re-test the retry path.", html);
        // Absolute and relative timestamps accompany every comment.
        Assert.Contains("<time", html);
        Assert.Contains("ago", html);
        // An edited comment is labelled rather than silently rewritten.
        Assert.Contains("edited", html);
        // The viewer's own comment is marked, and agent authors are distinguishable.
        Assert.Contains("You", html);
        Assert.Contains("Agent", html);
        Assert.Contains("Software Architect", html);
    }

    [Fact]
    public async Task OffersEditAndDeleteOnlyOnTheViewersOwnComments()
    {
        var viewerId = Guid.NewGuid();
        var (html, _) = await Render(Payload(viewerId));

        Assert.Contains("Edit comment by Dana Reed", html);
        Assert.Contains("Delete comment by Dana Reed", html);
        Assert.DoesNotContain("Edit comment by Sam Patel", html);
        Assert.DoesNotContain("Edit comment by Software Architect", html);
        Assert.Contains("Add a comment", html);
    }

    [Fact]
    public async Task HidesTheComposerAndControlsWhenTheServerWithholdsAuthority()
    {
        var viewerId = Guid.NewGuid();
        var payload = Payload(viewerId) with
        {
            CanComment = false,
            Comments =
            [
                new WorkItemCommentResponse(
                    Guid.NewGuid(), Guid.NewGuid(), "OrganizationUser", viewerId, "Dana Reed",
                    "Read-only thread.", 1, DateTimeOffset.UtcNow.AddHours(-1), null)
            ]
        };
        var (html, _) = await Render(payload);

        Assert.Contains("Read-only thread.", html);
        Assert.DoesNotContain("Add a comment", html);
        Assert.DoesNotContain("aria-label=\"Edit comment", html);
        Assert.DoesNotContain("aria-label=\"Delete comment", html);
    }

    [Fact]
    public async Task ReportsAnEmptyThreadInsteadOfAnEmptyList()
    {
        var viewerId = Guid.NewGuid();
        var payload = Payload(viewerId) with { Comments = [] };
        var (html, _) = await Render(payload);

        Assert.Contains("No comments yet", html);
        Assert.DoesNotContain("comments-list", html);
    }

    private static WorkItemCollaborationResponse Payload(Guid viewerId) => new(
        [
            new WorkItemCommentResponse(
                Guid.NewGuid(), Guid.NewGuid(), "OrganizationUser", viewerId, "Dana Reed",
                "Please re-test the retry path.", 3, DateTimeOffset.UtcNow.AddHours(-2), null, true, true),
            new WorkItemCommentResponse(
                Guid.NewGuid(), Guid.NewGuid(), "OrganizationUser", Guid.NewGuid(), "Sam Patel",
                "Agreed, adding a regression test.", 1, DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddHours(-3)),
            new WorkItemCommentResponse(
                Guid.NewGuid(), Guid.NewGuid(), "AgentInstallation", Guid.NewGuid(), "Software Architect",
                "Architecture guidance recorded.", 1, DateTimeOffset.UtcNow.AddMinutes(-5), null)
        ],
        [],
        true,
        viewerId);

    private static async Task<(string Html, List<string> Requests)> Render(WorkItemCollaborationResponse payload)
    {
        var requests = new List<string>();
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(new CommentHandler(requests, payload))
        {
            BaseAddress = new Uri("http://localhost")
        });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkItemComments>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WorkItemComments.OrganizationId)] = Guid.NewGuid(),
                    [nameof(WorkItemComments.BoardId)] = Guid.NewGuid(),
                    [nameof(WorkItemComments.ItemId)] = Guid.NewGuid(),
                    [nameof(WorkItemComments.Compact)] = true
                }));
            return component.ToHtmlString();
        });
        return (html, requests);
    }

    private sealed class CommentHandler(
        List<string> requests,
        WorkItemCollaborationResponse payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload)
            });
        }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(T)!);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) =>
            ValueTask.FromResult(default(T)!);
    }
}
