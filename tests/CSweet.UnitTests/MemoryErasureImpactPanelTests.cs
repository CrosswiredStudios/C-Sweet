using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryErasureImpactPanelTests
{
    private static MemoryErasureStatusPanel StatusPanel(HttpClient http)
    {
        var panel = new MemoryErasureStatusPanel(); Property(panel, "Http", http);
        Property(panel, "OrganizationId", Guid.NewGuid()); Property(panel, "EmployeeId", Guid.NewGuid()); Parameters(panel);
        return panel;
    }

    [Fact]
    public async Task SavedOperationDiscoveryPaginatesAndSelectedOperationGetsFreshStatus()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = StatusPanel(http); var operation = Guid.NewGuid(); var cursor = Guid.NewGuid();
        var first = Call(panel, "LoadOperationsAsync", true);
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryErasureOperationPage(
            [new(operation, DateTimeOffset.UtcNow, "ownership-review-required")], cursor)) }); await first;
        Assert.Null(Get(panel, "_result")); Assert.NotNull(Get(panel, "_page"));
        var selected = Call(panel, "SelectAsync", operation); handler.Responses[1].SetResult(OwnershipConflict()); await selected;
        Assert.Equal(operation.ToString("D"), Get(panel, "_operation")); Assert.Null(Get(panel, "_result"));
        Assert.Contains("Ownership review is required", Assert.IsType<string>(Get(panel, "_message")));
        var next = Call(panel, "LoadOperationsAsync", false);
        Assert.Contains($"beforeReceiptId={cursor:D}", handler.Uris[2].Query);
        handler.Responses[2].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryErasureOperationPage([], null)) }); await next;
        Assert.Empty(Assert.IsType<MemoryErasureOperationPage>(Get(panel, "_page")).Items);
        await Call(panel, "LoadOperationsAsync", false); Assert.Equal(3, handler.Methods.Count);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task SavedOperationDiscoveryDiscardsEarlierEmployeeResponseAfterNavigation()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = StatusPanel(http); var pending = Call(panel, "LoadOperationsAsync", true);
        Property(panel, "EmployeeId", Guid.NewGuid()); Parameters(panel);
        var current = Call(panel, "LoadOperationsAsync", true); var operation = Guid.NewGuid();
        handler.Responses[1].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryErasureOperationPage(
            [new(operation, DateTimeOffset.UtcNow, "available")], null)) }); await current;
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryErasureOperationPage(
            [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "available")], null)) }); await pending;
        Assert.Equal(operation, Assert.Single(Assert.IsType<MemoryErasureOperationPage>(Get(panel, "_page")).Items).OperationId);
        Assert.Null(Get(panel, "_result")); Assert.False(Assert.IsType<bool>(Get(panel, "_busy")));
    }

    [Theory]
    [InlineData("forbidden")] [InlineData("malformed")] [InlineData("unknown-status")]
    public async Task SavedOperationDiscoveryFailureClearsEarlierPageAndCompletion(string defect)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = StatusPanel(http);
        Set(panel, "_page", new MemoryErasureOperationPage([new(Guid.NewGuid(), DateTimeOffset.UtcNow, "available")], null));
        Set(panel, "_result", Erased(Guid.NewGuid(), Guid.NewGuid()));
        var pending = Call(panel, "LoadOperationsAsync", true);
        Assert.Null(Get(panel, "_page")); Assert.Null(Get(panel, "_result"));
        handler.Responses[0].SetResult(defect switch
        {
            "forbidden" => new(HttpStatusCode.Forbidden),
            "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("not JSON") },
            _ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryErasureOperationPage(
                [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "completed")], null)) }
        });
        await pending; Assert.Null(Get(panel, "_page")); Assert.Null(Get(panel, "_result"));
        Assert.NotNull(Get(panel, "_message")); Assert.False(Assert.IsType<bool>(Get(panel, "_busy")));
    }

    [Theory]
    [InlineData("memory_erasure_generic_ingestion_review_required","extraction job still retains")]
    [InlineData("memory_erasure_generic_lineage_review_required","cannot be verified")]
    public async Task GenericExtractionRetentionBlocksApplyEvenWithAnEarlierEvidenceToken(string code,string message)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http);
        Set(panel, "_impact", Impact((Guid)GetProperty(panel, "EpisodeId")!) with {
            ApplyBlockedReason = code, EvidenceToken = new string('a', 64) });
        Set(panel, "_confirmed", true);
        await Call(panel, "ApplyAsync").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(handler.Methods);
        var label = typeof(MemoryErasureImpactPanel).GetMethod("Blocker", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [code]);
        Assert.Contains(message, Assert.IsType<string>(label));
    }
    [Fact]
    public async Task NavigationDiscardsEarlierResponseAndKeepsNewPreview()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var first = (Guid)GetProperty(panel, "EpisodeId")!;
        var pending = Call(panel, "LoadAsync");
        var second = Guid.NewGuid(); Property(panel, "EpisodeId", second); Parameters(panel);
        var current = Call(panel, "LoadAsync");
        handler.Responses[1].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Impact(second)) });
        await current;
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Impact(first)) });
        await pending;
        Assert.Equal(second, Assert.IsType<MemoryErasureImpactResponse>(Get(panel, "_impact")).EpisodeId);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedRefreshClearsPriorImpact(HttpStatusCode status)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); Set(panel, "_impact", Impact((Guid)GetProperty(panel, "EpisodeId")!));
        var pending = Call(panel, "LoadAsync");
        Assert.Null(Get(panel, "_impact"));
        handler.Responses[0].SetResult(new(status)); await pending;
        Assert.Null(Get(panel, "_impact")); Assert.NotNull(Get(panel, "_message")); Assert.Equal(false, Get(panel, "_busy"));
    }

    [Fact]
    public async Task MismatchedResponseCannotPopulateAnotherEpisodesImpact()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var pending = Call(panel, "LoadAsync");
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Impact(Guid.NewGuid())) });
        await pending; Assert.Null(Get(panel, "_impact")); Assert.NotNull(Get(panel, "_message"));
    }

    private static MemoryErasureImpactResponse Impact(Guid episode) => new(episode, 1, [new("Employee", Guid.NewGuid(), "Employee", [new("Episode", 1)])], null);

    [Fact]
    public async Task LostApplyResponseRetriesTheSameOperationAndCanRecoverCommittedStatus()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var episode = (Guid)GetProperty(panel, "EpisodeId")!;
        Set(panel, "_impact", Impact(episode) with { EvidenceToken = new string('a', 64) }); Set(panel, "_confirmed", true);
        var first = Call(panel, "ApplyAsync");
        var request = Assert.IsType<EraseMemorySourceRequest>(Get(panel, "_request"));
        handler.Responses[0].SetException(new HttpRequestException("lost response")); await first;
        var retry = Call(panel, "ApplyAsync"); Assert.Equal(request, Get(panel, "_request"));
        handler.Responses[1].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Erased(episode, request.OperationId)) }); await retry;
        Assert.Null(Get(panel, "_request")); Assert.NotNull(Get(panel, "_result"));
        var status = Call(panel, "StatusAsync");
        handler.Responses[2].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Erased(episode, request.OperationId)) }); await status;
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Post, HttpMethod.Get }, handler.Methods);
    }

    [Fact]
    public async Task ApplyRequiresConfirmationAndDiscardsResponseAfterNavigation()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var episode = (Guid)GetProperty(panel, "EpisodeId")!;
        Set(panel, "_impact", Impact(episode) with { EvidenceToken = new string('a', 64) });
        await Call(panel, "ApplyAsync"); Assert.Empty(handler.Methods);
        Set(panel, "_confirmed", true); var pending = Call(panel, "ApplyAsync");
        var request = Assert.IsType<EraseMemorySourceRequest>(Get(panel, "_request"));
        Property(panel, "EpisodeId", Guid.NewGuid()); Parameters(panel);
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Erased(episode, request.OperationId)) }); await pending;
        Assert.Null(Get(panel, "_result")); Assert.Null(Get(panel, "_request"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{\"error\":\"another_conflict\"}")]
    [InlineData("{\"error\":5}")]
    public async Task StaleReviewRequiresNewPreviewAndCannotSubmitOldTokenAgain(string body)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var episode = (Guid)GetProperty(panel, "EpisodeId")!;
        Set(panel, "_impact", Impact(episode) with { EvidenceToken = new string('a', 64) }); Set(panel, "_confirmed", true);
        var pending = Call(panel, "ApplyAsync");
        handler.Responses[0].SetResult(new(HttpStatusCode.Conflict) { Content = new StringContent(body) }); await pending;
        Assert.Null(Get(panel, "_impact")); Assert.Null(Get(panel, "_request"));
        await Call(panel, "ApplyAsync"); Assert.Single(handler.Methods);
    }

    [Theory]
    [InlineData("ownership", "Ownership review is required")]
    [InlineData("evidence", "Evidence review is required")]
    public async Task OwnershipReviewBlockerPreservesTheOperationForRetry(string kind, string message)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var episode = (Guid)GetProperty(panel, "EpisodeId")!;
        Set(panel, "_impact", Impact(episode) with { EvidenceToken = new string('a', 64) }); Set(panel, "_confirmed", true);
        var pending = Call(panel, "ApplyAsync");
        var request = Assert.IsType<EraseMemorySourceRequest>(Get(panel, "_request"));
        handler.Responses[0].SetResult(OwnershipConflict(kind)); await pending;
        Assert.Equal(request, Get(panel, "_request")); Assert.Null(Get(panel, "_result"));
        Assert.Null(Get(panel, "_impact")); Assert.False(Assert.IsType<bool>(Get(panel, "_confirmed")));
        Assert.Contains(message, Assert.IsType<string>(Get(panel, "_message")));
        var retry = Call(panel, "ApplyAsync"); handler.Responses[1].SetResult(OwnershipConflict(kind)); await retry;
        Assert.Equal(request, Get(panel, "_request")); Assert.Null(Get(panel, "_result"));
        Assert.Contains(message, Assert.IsType<string>(Get(panel, "_message")));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Post }, handler.Methods);
    }

    [Theory]
    [InlineData("ownership", "Ownership review is required")]
    [InlineData("evidence", "Evidence review is required")]
    public async Task SavedStatusExplainsOwnershipReviewWithoutReportingCompletion(string kind, string message)
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = new MemoryErasureStatusPanel(); Property(panel, "Http", http);
        Property(panel, "OrganizationId", Guid.NewGuid()); Property(panel, "EmployeeId", Guid.NewGuid()); Parameters(panel);
        var operation = Guid.NewGuid().ToString("D"); Set(panel, "_operation", operation);
        var pending = Call(panel, "LoadAsync"); handler.Responses[0].SetResult(OwnershipConflict(kind)); await pending;
        Assert.Null(Get(panel, "_result")); Assert.Equal(operation, Get(panel, "_operation"));
        Assert.Contains(message, Assert.IsType<string>(Get(panel, "_message")));
        Assert.False(Assert.IsType<bool>(Get(panel, "_busy")));
    }

    private static HttpResponseMessage OwnershipConflict(string kind = "ownership") => new(HttpStatusCode.Conflict)
        { Content = JsonContent.Create(new { error = $"memory_erasure_{kind}_review_required" }) };

    private static MemoryErasureResponse Erased(Guid episode, Guid operation) => new(Guid.NewGuid(), operation, episode, 1, 2, 1, 1, 1, 0, "completed", DateTimeOffset.UtcNow, true);

    [Fact]
    public async Task SavedErasureStatusCanBeRecoveredWithoutTheErasedSourceAndRejectsOldAudienceResponses()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = new MemoryErasureStatusPanel(); Property(panel, "Http", http); Property(panel, "OrganizationId", Guid.NewGuid()); Property(panel, "EmployeeId", Guid.NewGuid()); Parameters(panel);
        var operation = Guid.NewGuid(); Set(panel, "_operation", operation.ToString());
        var pending = Call(panel, "LoadAsync"); Property(panel, "EmployeeId", Guid.NewGuid()); Parameters(panel);
        handler.Responses[0].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Erased(Guid.NewGuid(), operation)) }); await pending;
        Assert.Null(Get(panel, "_result"));
        Set(panel, "_operation", operation.ToString()); var current = Call(panel, "LoadAsync");
        handler.Responses[1].SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Erased(Guid.NewGuid(), operation)) }); await current;
        Assert.Equal(operation, Assert.IsType<MemoryErasureResponse>(Get(panel, "_result")).OperationId);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static MemoryErasureImpactPanel Panel(HttpClient http)
    {
        var panel = new MemoryErasureImpactPanel(); Property(panel, "Http", http);
        Property(panel, "OrganizationId", Guid.NewGuid()); Property(panel, "EmployeeId", Guid.NewGuid()); Property(panel, "EpisodeId", Guid.NewGuid());
        Parameters(panel); return panel;
    }
    private static void Parameters(object panel) => panel.GetType().GetMethod("OnParametersSet", Members)!.Invoke(panel, null);
    private static void Property(object panel, string name, object value) => panel.GetType().GetProperty(name, Members)!.SetValue(panel, value);
    private static object? GetProperty(object panel, string name) => panel.GetType().GetProperty(name, Members)!.GetValue(panel);
    private static void Set(object panel, string name, object value) => panel.GetType().GetField(name, Members)!.SetValue(panel, value);
    private static object? Get(object panel, string name) => panel.GetType().GetField(name, Members)!.GetValue(panel);
    private static Task Call(object panel, string name, params object[] arguments) => (Task)panel.GetType().GetMethod(name, Members)!.Invoke(panel, arguments)!;
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public List<TaskCompletionSource<HttpResponseMessage>> Responses { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method); Uris.Add(request.RequestUri!); var source = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Responses.Add(source); return source.Task;
        }
    }
}
