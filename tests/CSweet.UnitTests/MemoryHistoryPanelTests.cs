using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryHistoryPanelTests
{
    [Fact]
    public async Task HistoryFollowsSourcesAndPagesWithoutChangingTheSelectedMemory()
    {
        using var handler = new HistoryHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        using var panel = Panel(http); var root = (Guid)Property(panel, "RecordId")!;
        await Call(panel, "LoadAsync"); await Call(panel, "NextAsync");
        Assert.Contains("afterRevision=9", handler.Urls.Last());
        await Call(panel, "PreviousAsync"); Assert.Contains("afterRevision=0", handler.Urls.Last());
        var source = Guid.NewGuid(); await Call(panel, "FollowAsync", new MemoryHistoryLink("Episode", source, "Source 1"));
        Assert.Contains($"/history/Episode/{source}", handler.Urls.Last());
        await Call(panel, "BackAsync"); Assert.Contains($"/history/Claim/{root}", handler.Urls.Last());
        Assert.Equal(root, Property(panel, "RecordId"));
    }

    [Fact]
    public async Task DeniedRefreshClearsPreviouslyVisibleHistoricalContent()
    {
        using var handler = new HistoryHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        using var panel = Panel(http); await Call(panel, "LoadAsync"); Assert.NotNull(Field(panel, "_page"));
        handler.Denied = true; await Call(panel, "LoadAsync");
        Assert.Null(Field(panel, "_page")); Assert.Contains("permission", Assert.IsType<string>(Field(panel, "_message")));
    }

    [Fact]
    public async Task LateReadCannotExposeThePreviousEmployeeHistoryAfterNavigation()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        using var panel = Panel(http); var pending = Call(panel, "LoadAsync");
        typeof(MemoryHistoryPanel).GetProperty("EmployeeId")!.SetValue(panel, Guid.NewGuid());
        typeof(MemoryHistoryPanel).GetMethod("OnParametersSet", Members)!.Invoke(panel, null);
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryHistoryPage("Claim", Guid.NewGuid(), "Employee", [], null)) });
        await pending; Assert.Null(Field(panel, "_page"));
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MemoryHistoryPanel Panel(HttpClient http)
    {
        var panel = new MemoryHistoryPanel();
        typeof(MemoryHistoryPanel).GetProperty("Http", Members)!.SetValue(panel, http);
        typeof(MemoryHistoryPanel).GetProperty("RecordId")!.SetValue(panel, Guid.NewGuid());
        typeof(MemoryHistoryPanel).GetProperty("Kind")!.SetValue(panel, "Claim");
        typeof(MemoryHistoryPanel).GetMethod("OnParametersSet", Members)!.Invoke(panel, null); return panel;
    }
    private static object? Property(MemoryHistoryPanel panel, string name) => typeof(MemoryHistoryPanel).GetProperty(name, Members)!.GetValue(panel);
    private static object? Field(MemoryHistoryPanel panel, string name) => typeof(MemoryHistoryPanel).GetField(name, Members)!.GetValue(panel);
    private static Task Call(MemoryHistoryPanel panel, string method, params object[] args) => (Task)typeof(MemoryHistoryPanel).GetMethod(method, Members)!.Invoke(panel, args)!;
    private sealed class HistoryHttp : HttpMessageHandler
    {
        public List<string> Urls { get; } = []; public bool Denied { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Denied ? new HttpResponseMessage(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new MemoryHistoryPage("Claim", Guid.NewGuid(), "Employee", [], 9)) });
        }
    }
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Response.Task;
    }
}
