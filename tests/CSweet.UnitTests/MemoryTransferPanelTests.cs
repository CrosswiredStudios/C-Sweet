using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryTransferPanelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownWriteOutcomeRetainsTheExactOperationUntilReplayConfirmsIt(bool prepare)
    {
        var package = Guid.NewGuid(); var target = Guid.NewGuid();
        using var handler = new TransferHttp(package, target);
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = new MemoryTransferPanel();
        typeof(MemoryTransferPanel).GetProperty("Http", Members)!.SetValue(panel, http);
        Set(panel, "_target", target); Set(panel, "_debrief", "original reviewed notes");
        Set(panel, "_transfer", new MemoryTransferResponse(package, target, "Approved", new string('a', 64), "content", "Personal", false, true, true, null));
        if (prepare) await Call(panel, "PrepareAsync"); else await Call(panel, "TransitionAsync", "apply");
        Assert.Single(handler.Writes);
        Assert.NotNull(Get(panel, prepare ? "_pendingPrepare" : "_pendingTransition"));
        Set(panel, "_debrief", "subsequent field value must not alter retry");
        await Call(panel, "RetryAsync");
        Assert.Equal(2, handler.Writes.Count);
        Assert.Equal(handler.Writes[0], handler.Writes[1]);
        Assert.Null(Get(panel, "_pendingPrepare")); Assert.Null(Get(panel, "_pendingTransition"));
        Assert.Equal("Applied", Assert.IsType<MemoryTransferResponse>(Get(panel, "_transfer")).Status);
    }

    [Fact]
    public async Task RevokedReadClearsPreviouslyVisibleTransferContent()
    {
        using var http = new HttpClient(new DeniedHttp()) { BaseAddress = new("http://test/") };
        var panel = new MemoryTransferPanel();
        typeof(MemoryTransferPanel).GetProperty("Http", Members)!.SetValue(panel, http);
        Set(panel, "_transfer", new MemoryTransferResponse(Guid.NewGuid(), Guid.NewGuid(), "Approved", new string('a', 64), "private", "Personal", false, true, true, null));
        await Call(panel, "LoadTransferAsync", Guid.NewGuid());
        Assert.Null(Get(panel, "_transfer"));
        Assert.Contains("permission", Assert.IsType<string>(Get(panel, "_message")));
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    [Fact]
    public async Task InspectorCoreItemsArePreparedAsTransferBlocks()
    {
        var id = Guid.NewGuid();
        using var handler = new TransferHttp(Guid.NewGuid(), Guid.NewGuid());
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = new MemoryTransferPanel();
        typeof(MemoryTransferPanel).GetProperty("Http", Members)!.SetValue(panel, http);
        typeof(MemoryTransferPanel).GetProperty(nameof(MemoryTransferPanel.Selected))!.SetValue(panel,
            new AgentMemoryItemResponse(id, "Core", "Employee", null, null, "Core preferences", "content", "curated", "Personal",
                "Pinned", null, DateTimeOffset.UtcNow, null, null));
        typeof(MemoryTransferPanel).GetMethod("AddSelected", Members)!.Invoke(panel, null);
        await Call(panel, "PrepareAsync");
        using var payload = JsonDocument.Parse(Assert.Single(handler.Writes));
        var item = payload.RootElement.GetProperty("items")[0];
        Assert.Equal("Block", item.GetProperty("kind").GetString());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
    }
    private static void Set(object panel, string field, object value) => typeof(MemoryTransferPanel).GetField(field, Members)!.SetValue(panel, value);
    private static object? Get(object panel, string field) => typeof(MemoryTransferPanel).GetField(field, Members)!.GetValue(panel);
    private static Task Call(object panel, string method, params object[] args) => (Task)typeof(MemoryTransferPanel).GetMethod(method, Members)!.Invoke(panel, args)!;
    private sealed class TransferHttp(Guid package, Guid target) : HttpMessageHandler
    {
        public List<string> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Writes.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (Writes.Count == 1) throw new HttpRequestException("Response lost after server commit.");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryTransferResult(Guid.NewGuid(), package, "Applied", Guid.NewGuid(), true)) };
            }
            return new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith(package.ToString())
                ? JsonContent.Create(new MemoryTransferResponse(package, target, "Applied", new string('b', 64), "content", "Personal", false, false, true, Guid.NewGuid()))
                : JsonContent.Create(new MemoryTransferPage([], null)) };
        }
    }
    private sealed class DeniedHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
