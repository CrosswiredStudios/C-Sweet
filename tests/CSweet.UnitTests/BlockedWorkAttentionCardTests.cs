using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Components;

namespace CSweet.UnitTests;

public sealed class BlockedWorkAttentionCardTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task RetryUsesCurrentIdentityAndRevisionAndOnlyRefreshesAfterAcceptance(HttpStatusCode status, bool accepted)
    {
        var org = Guid.NewGuid(); var item = Guid.NewGuid();
        var handler = new RetryHandler(status); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var refreshes = 0;
        var card = new BlockedWorkAttentionCard { OrganizationId = org,
            Item = new(item, Guid.NewGuid(), "Producer", "Director", "Resolve planning", "Missing evidence", 7, true, "/work"),
            RefreshRequested = EventCallback.Factory.Create(new object(), () => refreshes++) };
        typeof(BlockedWorkAttentionCard).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(card, http);
        await (Task)typeof(BlockedWorkAttentionCard).GetMethod("RetryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, null)!;
        Assert.Equal($"/api/organizations/{org:D}/work/personal-todos/items/requeue", handler.Path);
        Assert.Equal(item, handler.Request!.ItemId); Assert.Equal(7, handler.Request.ExpectedRevision);
        Assert.Equal(accepted ? 1 : 0, refreshes);
        var error = typeof(BlockedWorkAttentionCard).GetField("_error", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card);
        Assert.Equal(accepted, error is null);
    }

    private sealed class RetryHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public RequeuePersonalTodoItemRequest? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Path = request.RequestUri!.AbsolutePath;
            Request = await request.Content!.ReadFromJsonAsync<RequeuePersonalTodoItemRequest>(token);
            return new(status);
        }
    }
}
