using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Memory;
using CSweet.UI.Pages;

namespace CSweet.UnitTests;

public sealed class MemoryClaimEntityReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainEntityCorrectionRetainsTheSelectedEvidenceUntilReplay(bool nullResponse)
    {
        using var handler = new CorrectionHttp(nullResponse);
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var page = Page(http); Set(page, "_review", Preview()); Set(page, "_selectedCorrectionTarget", Choice());
        await Call(page, "SubmitClaimReviewAsync", "correct");
        Assert.NotNull(Get(page, "_pendingReview")); Assert.Single(handler.Writes);
        Set(page, "_selectedCorrectionTarget", Choice());
        await Call(page, "FindCorrectionTargetsAsync"); await Call(page, "LoadClaimReviewAsync");
        Assert.Equal(0, handler.Reads);
        await Call(page, "SubmitClaimReviewAsync", "correct");
        Assert.Equal(2, handler.Writes.Count); Assert.Equal(handler.Writes[0], handler.Writes[1]);
        Assert.Contains("replacementEntity", handler.Writes[0]);
        Assert.Null(Get(page, "_pendingReview")); Assert.Null(Get(page, "_review"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task DeniedEntitySearchClearsPriorChoicesAndClaimReview(HttpStatusCode status)
    {
        using var http = new HttpClient(new DeniedHttp(status)) { BaseAddress = new("http://test/") };
        var page = Page(http); Set(page, "_review", Preview()); Set(page, "_selectedCorrectionTarget", Choice());
        Set(page, "_correctionTargets", new[] { Choice() }); Set(page, "_entitySearch", "Carol");
        await Call(page, "FindCorrectionTargetsAsync");
        Assert.Null(Get(page, "_selectedCorrectionTarget")); Assert.Null(Get(page, "_review"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MemoryClaimCorrectionTarget>>(Get(page, "_correctionTargets")));
    }

    private static MemoryClaimReviewResponse Preview() => new(Guid.NewGuid(), 2, new string('a', 64), "Alice reports to Bob",
        "Pending", "Personal", true, true, true, [], true);
    private static MemoryClaimCorrectionTarget Choice() => new(Guid.NewGuid(), "Carol", "person", "Confidential", new string('b', 64), []);
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static AgentMemory Page(HttpClient http)
    { var page = new AgentMemory(); typeof(AgentMemory).GetProperty("Http", Members)!.SetValue(page, http); return page; }
    private static void Set(AgentMemory page, string field, object value) => typeof(AgentMemory).GetField(field, Members)!.SetValue(page, value);
    private static object? Get(AgentMemory page, string field) => typeof(AgentMemory).GetField(field, Members)!.GetValue(page);
    private static Task Call(AgentMemory page, string method, params object[] args) => (Task)typeof(AgentMemory).GetMethod(method, Members)!.Invoke(page, args)!;
    private sealed class CorrectionHttp(bool nullResponse) : HttpMessageHandler
    {
        public List<string> Writes { get; } = [];
        public int Reads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) { Reads++; return new(HttpStatusCode.NotFound); }
            Writes.Add(await request.Content!.ReadAsStringAsync(token));
            if (Writes.Count == 1)
            {
                if (nullResponse) return new(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") };
                throw new HttpRequestException("Response lost after commit.");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ReviewMemoryClaimResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                3, "correct", DateTimeOffset.UtcNow, true)) };
        }
    }
    private sealed class DeniedHttp(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(status));
    }
}
