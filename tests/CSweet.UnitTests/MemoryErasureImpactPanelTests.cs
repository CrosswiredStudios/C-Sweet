using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryErasureImpactPanelTests
{
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

    [Fact]
    public async Task StaleReviewRequiresNewPreviewAndCannotSubmitOldTokenAgain()
    {
        using var handler = new DelayedHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var episode = (Guid)GetProperty(panel, "EpisodeId")!;
        Set(panel, "_impact", Impact(episode) with { EvidenceToken = new string('a', 64) }); Set(panel, "_confirmed", true);
        var pending = Call(panel, "ApplyAsync"); handler.Responses[0].SetResult(new(HttpStatusCode.Conflict)); await pending;
        Assert.Null(Get(panel, "_impact")); Assert.Null(Get(panel, "_request"));
        await Call(panel, "ApplyAsync"); Assert.Single(handler.Methods);
    }

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
    private static Task Call(object panel, string name) => (Task)panel.GetType().GetMethod(name, Members)!.Invoke(panel, null)!;
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public List<TaskCompletionSource<HttpResponseMessage>> Responses { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method); var source = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Responses.Add(source); return source.Task;
        }
    }
}
