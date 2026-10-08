using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryReextractionPanelTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Set(object panel, string name, object? value) => panel.GetType().GetField(name, Members)!.SetValue(panel, value);
    private static object? Get(object panel, string name) => panel.GetType().GetField(name, Members)!.GetValue(panel);
    private static Task Call(object panel, string name, params object[] args) => (Task)panel.GetType().GetMethod(name, Members)!.Invoke(panel, args)!;
    private static void Navigate(MemoryReextractionPanel panel, Guid employee)
    { panel.GetType().GetProperty("EmployeeId", Members)!.SetValue(panel, employee); panel.GetType().GetMethod("OnParametersSet", Members)!.Invoke(panel, null); }
    private static MemoryReextractionPanel Panel(HttpClient http)
    {
        var panel = new MemoryReextractionPanel(); panel.GetType().GetProperty("OrganizationId", Members)!.SetValue(panel, Guid.NewGuid());
        panel.GetType().GetProperty("Http", Members)!.SetValue(panel, http); Navigate(panel, Guid.NewGuid()); return panel;
    }
    private static MemoryReextractionPreview Preview(bool allowed = true) => new(Guid.NewGuid(), Guid.NewGuid(), 2,
        new string('b',64), "Retained private source", "agent-proposal", "Employee", "Personal", allowed, null, 7, true);

    [Fact]
    public async Task ReextractionPanelLostResponseKeepsExactConsentOperationAndOriginalEmployee()
    {
        var preview = Preview(); using var handler = new LostResponseHttp(preview);
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_preview", preview); Set(panel, "_acknowledged", true);
        await Call(panel, "QueueAsync"); Assert.NotNull(Get(panel, "_pending"));
        Navigate(panel, Guid.NewGuid()); Assert.Null(Get(panel, "_preview")); Assert.False((bool)Get(panel, "_acknowledged")!);
        await Call(panel, "RefreshAsync"); await Call(panel, "QueueAsync"); Assert.Single(handler.Requests);
        await Call(panel, "RetryAsync");
        Assert.Equal(2, handler.Requests.Count); Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.Contains("preserve-existing-v1", handler.Requests[0].Body); Assert.Null(Get(panel, "_pending"));
        Assert.Contains("previous employee", (string)Get(panel, "_message")!);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ReextractionPanelRequiresBothAvailableEvidenceAndExplicitConsent(bool allowed, bool consent)
    {
        using var handler = new LostResponseHttp(Preview()); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); Set(panel, "_preview", Preview(allowed)); Set(panel, "_acknowledged", consent);
        await Call(panel, "QueueAsync"); Assert.Empty(handler.Requests); Assert.Null(Get(panel, "_pending"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReextractionPanelDefinitiveFailureClearsSourceAndConsent(HttpStatusCode status)
    {
        using var http = new HttpClient(new StatusHttp(status)) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_preview", Preview()); Set(panel, "_acknowledged", true);
        await Call(panel, "QueueAsync"); Assert.Null(Get(panel, "_preview")); Assert.Null(Get(panel, "_pending"));
        Assert.False((bool)Get(panel, "_acknowledged")!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReextractionPanelStaleReadCannotRevealSourceAfterNavigationOrNavigationBack(bool back)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var oldEmployee = panel.EmployeeId; var preview = Preview();
        var read = Call(panel, "PreviewAsync", preview.JobId); Navigate(panel, Guid.NewGuid());
        if (back) Navigate(panel, oldEmployee);
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(preview) });
        await read.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Null(Get(panel, "_preview"));
    }

    [Fact]
    public async Task ReextractionPanelMalformedConfirmationRetainsOriginalRequest()
    {
        using var http = new HttpClient(new StatusHttp(HttpStatusCode.OK)) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_preview", Preview()); Set(panel, "_acknowledged", true);
        await Call(panel, "QueueAsync"); var original = Get(panel, "_pending"); Assert.NotNull(original);
        await Call(panel, "RetryAsync"); Assert.Same(original, Get(panel, "_pending"));
    }

    private sealed class LostResponseHttp(MemoryReextractionPreview preview) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token); Requests.Add((request.RequestUri!.ToString(), body));
            if (Requests.Count == 1) throw new HttpRequestException("Lost response");
            var review = JsonSerializer.Deserialize<ReviewMemoryReextractionRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ReviewMemoryReextractionResponse(Guid.NewGuid(),
                preview.JobId, Guid.NewGuid(), preview.EpisodeId, review.ExpectedInputGeneration + 1, "Completed", true)) };
        }
    }
    private sealed class StatusHttp(HttpStatusCode status) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(status)); }
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Response.Task;
    }
}
