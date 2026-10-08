using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Memory;
using CSweet.UI.Components;

namespace CSweet.UnitTests;

public sealed class MemoryIngestionRecoveryPanelTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Set(object panel, string name, object? value) => panel.GetType().GetField(name,Members)!.SetValue(panel,value);
    private static object? Get(object panel, string name) => panel.GetType().GetField(name,Members)!.GetValue(panel);
    private static Task Call(object panel, string name, params object[] args) => (Task)panel.GetType().GetMethod(name,Members)!.Invoke(panel,args)!;
    private static void Parameter(object panel,string name,object value) => panel.GetType().GetProperty(name,Members)!.SetValue(panel,value);
    private static MemoryIngestionRecoveryPanel Panel(HttpClient http)
    {
        var panel=new MemoryIngestionRecoveryPanel(); Parameter(panel,"OrganizationId",Guid.NewGuid()); Parameter(panel,"EmployeeId",Guid.NewGuid()); return panel;
    }
    private static void Http(MemoryIngestionRecoveryPanel panel, HttpClient http) => panel.GetType().GetProperty("Http",Members)!.SetValue(panel,http);
    private static MemoryIngestionRecoveryPreview Preview(bool allowed=true) => new(Guid.NewGuid(),2,new string('b',64),"Retained private source","agent-proposal","Personal",allowed,allowed?null:"memory_ingestion_source_unavailable");

    [Fact]
    public async Task IngestionRecoveryPanelLostResponseKeepsExactRequestAndOriginalEmployee()
    {
        using var handler = new RecoveryHttp(); using var http = new HttpClient(handler) { BaseAddress=new("http://test/") };
        var panel=Panel(http); Http(panel,http); Set(panel,"_preview",Preview());
        await Call(panel,"QueueAsync"); Assert.NotNull(Get(panel,"_pending"));
        Parameter(panel,"EmployeeId",Guid.NewGuid());
        panel.GetType().GetMethod("OnParametersSet",Members)!.Invoke(panel,null);
        Assert.Null(Get(panel,"_preview"));
        await Call(panel,"RefreshAsync"); await Call(panel,"QueueAsync"); Assert.Single(handler.Requests);
        await Call(panel,"RetryAsync");
        Assert.Equal(2,handler.Requests.Count); Assert.Equal(handler.Requests[0],handler.Requests[1]);
        Assert.Null(Get(panel,"_pending"));
    }
    [Fact]
    public async Task IngestionRecoveryPanelBlockedPreviewCannotQueue()
    {
        using var handler=new RecoveryHttp(); using var http=new HttpClient(handler) { BaseAddress=new("http://test/") };
        var panel=Panel(http); Http(panel,http); Set(panel,"_preview",Preview(false));
        await Call(panel,"QueueAsync"); Assert.Empty(handler.Requests); Assert.Null(Get(panel,"_pending"));
    }
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task IngestionRecoveryPanelDefinitiveFailureClearsOldPreview(HttpStatusCode status)
    {
        using var http=new HttpClient(new StatusHttp(status)) { BaseAddress=new("http://test/") };
        var panel=Panel(http); Http(panel,http); Set(panel,"_preview",Preview());
        await Call(panel,"QueueAsync"); Assert.Null(Get(panel,"_preview")); Assert.Null(Get(panel,"_pending"));
    }
    [Fact]
    public async Task IngestionRecoveryPanelReadFromEarlierEmployeeCannotShowPrivateContent()
    {
        using var handler=new DelayedHttp(); using var http=new HttpClient(handler) { BaseAddress=new("http://test/") };
        var panel=Panel(http); Http(panel,http); var preview=Preview();
        var read=Call(panel,"PreviewAsync",preview.EpisodeId); Parameter(panel,"EmployeeId",Guid.NewGuid());
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content=JsonContent.Create(preview) });
        await read.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Null(Get(panel,"_preview"));
    }
    [Fact]
    public async Task IngestionRecoveryPanelRequiresReconciliationChoiceAndPreservesItOnReplay()
    {
        using var handler=new RecoveryHttp(); using var http=new HttpClient(handler) { BaseAddress=new("http://test/") };
        var panel=Panel(http); Http(panel,http);
        Set(panel,"_preview",Preview() with { ExistingRecords=7,RequiredReconciliationPolicy="preserve-existing-v1" });
        await Call(panel,"QueueAsync"); Assert.Empty(handler.Requests);
        Set(panel,"_reconcileAcknowledged",true); await Call(panel,"QueueAsync");
        Assert.Contains("preserve-existing-v1",Assert.Single(handler.Requests).Body);
        await Call(panel,"RetryAsync"); Assert.Equal(handler.Requests[0],handler.Requests[1]);
    }
    [Fact]
    public void IngestionRecoveryPanelNavigationClearsReconciliationChoice()
    {
        using var http=new HttpClient(); var panel=Panel(http);
        Set(panel,"_reconcileAcknowledged",true);
        panel.GetType().GetMethod("OnParametersSet",Members)!.Invoke(panel,null);
        Assert.False((bool)Get(panel,"_reconcileAcknowledged")!);
    }
    private sealed class RecoveryHttp : HttpMessageHandler
    {
        public List<(string Url,string Body)> Requests { get; }=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests.Add((request.RequestUri!.ToString(),await request.Content!.ReadAsStringAsync(token)));
            if (Requests.Count==1) throw new HttpRequestException("Lost response");
            return new(HttpStatusCode.OK) { Content=JsonContent.Create(new RecoverMemoryIngestionResponse(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Pending",true)) };
        }
    }
    private sealed class StatusHttp(HttpStatusCode status) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(status)); }
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Response.Task;
    }
}
