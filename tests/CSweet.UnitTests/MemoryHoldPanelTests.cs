using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;

namespace CSweet.UnitTests;

public sealed class MemoryHoldPanelTests
{
    [Fact]
    public async Task UpstreamBlockerPreventsSubmissionAndResolvedReviewRetainsExactRetry()
    {
        using var handler = new ReviewHttp();
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http);
        var blocked = Preview() with { LegalHold = true, CanRelease = false, IsTransferred = true,
            UpstreamSources = 2, HeldUpstreamSources = 1, ReleaseBlocker = "memory_transfer_upstream_held" };
        Set(panel, "_review", blocked);
        await Call(panel, "SubmitAsync"); Assert.Empty(handler.Writes); Assert.Null(Get(panel, "_pending"));
        Set(panel, "_review", blocked with { CanRelease = true, HeldUpstreamSources = 0, ReleaseBlocker = null });
        await Call(panel, "SubmitAsync");
        var pending = Assert.IsType<ReviewMemoryHoldRequest>(Get(panel, "_pending"));
        Assert.False(pending.LegalHold); Assert.Equal(blocked.EvidenceToken, pending.EvidenceToken);
        await Call(panel, "RetryAsync");
        Assert.Equal(handler.Writes[0], handler.Writes[1]); Assert.Null(Get(panel, "_pending"));
    }

    [Fact]
    public async Task EarlierSearchCannotClearAnEpisodeWithAnUncertainReview()
    {
        using var handler = new DelayedReadHttp();
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var page = new CSweet.UI.Pages.AgentMemory(); var type = page.GetType();
        type.GetProperty("Http", Members)!.SetValue(page, http);
        var search = (Task)type.GetMethod("LoadItemsAsync", Members)!.Invoke(page, null)!;
        var selected = new AgentMemoryItemResponse(Guid.NewGuid(), "Episode", "Employee", null, null, "Episode", "Steps", "user", "Personal",
            "Pending", null, DateTimeOffset.UtcNow, null, null);
        type.GetField("_selected", Members)!.SetValue(page, selected);
        type.GetField("_sourceHoldLocked", Members)!.SetValue(page, true);
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentMemoryPageResponse([], null, 0)) });
        await search;
        Assert.Same(selected, type.GetField("_selected", Members)!.GetValue(page));
    }

    [Fact]
    public async Task LostCorrectionResponseRetainsExactRequestAndLocksSelectionUntilReplay()
    {
        using var handler = new ReviewHttp();
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http);
        var locks = new List<bool>(); var results = new List<Guid>();
        Property(panel, "LockedChanged", EventCallback.Factory.Create<bool>(locks, (bool value) => locks.Add(value)));
        Property(panel, "Reviewed", EventCallback.Factory.Create<Guid>(results, (Guid value) => results.Add(value)));
        Set(panel, "_review", Preview());
        await Call(panel, "SubmitAsync");
        Assert.NotNull(Get(panel, "_pending")); Assert.True(locks.Last()); Assert.Empty(results);
        await Call(panel, "LoadAsync"); await Call(panel, "SubmitAsync");
        Assert.Single(handler.Writes); Assert.Equal(0, handler.Reads);
        await Call(panel, "RetryAsync");
        Assert.Equal(2, handler.Writes.Count); Assert.Equal(handler.Writes[0], handler.Writes[1]);
        Assert.Null(Get(panel, "_pending")); Assert.Null(Get(panel, "_review"));
        Assert.False(locks.Last()); Assert.Equal(handler.ResultId, Assert.Single(results));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task DefinitiveRejectionClearsThePendingDecisionAndOldEvidence(HttpStatusCode status)
    {
        using var http = new HttpClient(new StatusHttp(status)) { BaseAddress = new("http://test/") };
        var panel = Panel(http); Set(panel, "_review", Preview());
        await Call(panel, "SubmitAsync");
        Assert.Null(Get(panel, "_pending")); Assert.Null(Get(panel, "_review"));
        Assert.False((bool)typeof(MemoryHoldPanel).GetProperty("Locked", Members)!.GetValue(panel)!);
    }

    [Fact]
    public async Task RevokedReadClearsPreviouslyVisibleEvidence()
    {
        using var http = new HttpClient(new StatusHttp(HttpStatusCode.Forbidden)) { BaseAddress = new("http://test/") };
        var panel = Panel(http); Set(panel, "_review", Preview());
        await Call(panel, "LoadAsync");
        Assert.Null(Get(panel, "_review"));
        Assert.Contains("permission", Assert.IsType<string>(Get(panel, "_message")));
    }

    private static MemoryHoldPreview Preview() => new(Guid.NewGuid(), 2, new string('a', 64), "Steps", false, true);
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static MemoryHoldPanel Panel(HttpClient http)
    { var panel = new MemoryHoldPanel(); Property(panel, "Http", http); return panel; }
    private static void Property(object panel, string name, object value) => typeof(MemoryHoldPanel).GetProperty(name, Members)!.SetValue(panel, value);
    private static void Set(object panel, string field, object value) => typeof(MemoryHoldPanel).GetField(field, Members)!.SetValue(panel, value);
    private static object? Get(object panel, string field) => typeof(MemoryHoldPanel).GetField(field, Members)!.GetValue(panel);
    private static Task Call(object panel, string method, params object[] args) => (Task)typeof(MemoryHoldPanel).GetMethod(method, Members)!.Invoke(panel, args)!;
    private sealed class ReviewHttp : HttpMessageHandler
    {
        public Guid ResultId { get; } = Guid.NewGuid();
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) { Reads++; return new(HttpStatusCode.Forbidden); }
            Writes.Add(await request.Content!.ReadAsStringAsync(token));
            if (Writes.Count == 1) throw new HttpRequestException("Response lost after commit");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ReviewMemoryHoldResponse(Guid.NewGuid(),
                ResultId, 3, true, DateTimeOffset.UtcNow, true)) };
        }
    }
    private sealed class StatusHttp(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
    private sealed class DelayedReadHttp : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Response.Task;
    }
}
