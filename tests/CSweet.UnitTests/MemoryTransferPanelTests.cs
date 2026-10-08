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
    private static MemoryTransferPanel Panel(HttpClient http)
    {
        var panel = new MemoryTransferPanel();
        typeof(MemoryTransferPanel).GetProperty("OrganizationId", Members)!.SetValue(panel, Guid.NewGuid());
        typeof(MemoryTransferPanel).GetProperty("EmployeeId", Members)!.SetValue(panel, Guid.NewGuid());
        typeof(MemoryTransferPanel).GetProperty("Http", Members)!.SetValue(panel, http);
        typeof(MemoryTransferPanel).GetMethod("OnParametersSet", Members)!.Invoke(panel, null);
        return panel;
    }
    private static void Navigate(MemoryTransferPanel panel, Guid organization, Guid employee)
    {
        typeof(MemoryTransferPanel).GetProperty("OrganizationId", Members)!.SetValue(panel, organization);
        typeof(MemoryTransferPanel).GetProperty("EmployeeId", Members)!.SetValue(panel, employee);
        typeof(MemoryTransferPanel).GetMethod("OnParametersSet", Members)!.Invoke(panel, null);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LostResponseReplayKeepsOriginalEmployeeAndOrganizationAndHidesPreviousContent(bool prepare, bool organization)
    {
        var package = Guid.NewGuid(); var target = Guid.NewGuid();
        using var handler = new TransferHttp(package, target);
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_target", target); Set(panel, "_debrief", "private original notes");
        Set(panel, "_transfer", new MemoryTransferResponse(package, target, "Approved", new string('a', 64), "private original review", "Personal", false, true, true, null));
        if (prepare) await Call(panel, "PrepareAsync"); else await Call(panel, "TransitionAsync", "apply");
        var original = Get(panel, prepare ? "_pendingPrepare" : "_pendingTransition");
        Navigate(panel, organization ? Guid.NewGuid() : panel.OrganizationId, Guid.NewGuid());
        Assert.Null(Get(panel, "_transfer")); Assert.Equal("", Get(panel, "_debrief"));
        await Call(panel, "PrepareAsync"); await Call(panel, "LoadListAsync", false);
        Assert.Single(handler.Writes); Assert.Same(original, Get(panel, prepare ? "_pendingPrepare" : "_pendingTransition"));
        await Call(panel, "RetryAsync");
        Assert.Equal(handler.WriteUrls[0], handler.WriteUrls[1]); Assert.Equal(handler.Writes[0], handler.Writes[1]);
        Assert.Null(Get(panel, "_pendingPrepare")); Assert.Null(Get(panel, "_pendingTransition")); Assert.Null(Get(panel, "_transfer"));
        Assert.Contains("previous employee", (string)Get(panel, "_message")!);
        Assert.False((bool)Get(panel, "_opened")!);
        Assert.Empty(handler.ReadUrls);
    }

    [Theory]
    [InlineData("transfer", false)]
    [InlineData("transfer", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("employees", false)]
    [InlineData("employees", true)]
    public async Task DelayedReadsCannotReappearAfterNavigationOrNavigationBack(string kind, bool back)
    {
        using var handler = new DelayedTransferHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var org = panel.OrganizationId; var employee = panel.EmployeeId; var package = Guid.NewGuid();
        var read = kind switch { "transfer" => Call(panel, "LoadTransferAsync", package), "list" => Call(panel, "LoadListAsync", false), _ => Call(panel, "OpenAsync") };
        Navigate(panel, Guid.NewGuid(), Guid.NewGuid()); if (back) Navigate(panel, org, employee);
        var content = kind switch
        {
            "transfer" => JsonContent.Create(new MemoryTransferResponse(package, Guid.NewGuid(), "Approved", new string('a', 64), "private", "Personal", false, true, true, null)),
            "list" => JsonContent.Create(new MemoryTransferPage([new(package, Guid.NewGuid(), DateTimeOffset.UtcNow)], Guid.NewGuid())),
            _ => JsonContent.Create(new[] { new CSweet.Contracts.Core.OrganizationUserResponse(Guid.NewGuid(), org, null, null, null, "Private employee", null, 1, 0, DateTimeOffset.UtcNow) })
        };
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = content });
        await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(Get(panel, "_transfer")); Assert.Empty((System.Collections.IEnumerable)Get(panel, "_saved")!);
        Assert.Empty((System.Collections.IEnumerable)Get(panel, "_employees")!); Assert.Null(Get(panel, "_nextCursor"));
        Assert.False((bool)Get(panel, "_opened")!); Assert.False((bool)Get(panel, "_busy")!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedConfirmationPreservesOriginalOperationAndReviewIsNotDisplayed(bool prepare)
    {
        var package = Guid.NewGuid(); using var handler = new WrongPackageHttp();
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_target", Guid.NewGuid()); Set(panel, "_debrief", "notes");
        Set(panel, "_transfer", new MemoryTransferResponse(package, Guid.NewGuid(), "Approved", new string('a', 64), "review", "Personal", false, true, true, null));
        if (prepare) await Call(panel, "PrepareAsync"); else await Call(panel, "TransitionAsync", "apply");
        var pending = Get(panel, prepare ? "_pendingPrepare" : "_pendingTransition"); Assert.NotNull(pending);
        await Call(panel, "RetryAsync"); Assert.Same(pending, Get(panel, prepare ? "_pendingPrepare" : "_pendingTransition"));
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteCompletingAfterNavigationDoesNotLoadOriginalReviewIntoTheCurrentView(bool back)
    {
        using var handler = new DelayedTransferHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var organization = panel.OrganizationId; var employee = panel.EmployeeId;
        Set(panel, "_target", Guid.NewGuid()); Set(panel, "_debrief", "private notes");
        var write = Call(panel, "PrepareAsync");
        Navigate(panel, Guid.NewGuid(), Guid.NewGuid()); if (back) Navigate(panel, organization, employee);
        await Call(panel, "PrepareAsync"); Assert.Equal(1, handler.Calls);
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryTransferResult(Guid.NewGuid(), Guid.NewGuid(), "PendingApproval", null, false)) });
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Calls); Assert.Null(Get(panel, "_transfer")); Assert.Null(Get(panel, "_pendingPrepare"));
        Assert.Contains("previous employee", (string)Get(panel, "_message")!);
        Assert.False((bool)Get(panel, "_opened")!);
    }

    [Fact]
    public async Task AnOldReadCannotReleaseTheBusyFlagOwnedByANewerRead()
    {
        using var handler = new TwoDelayedReadsHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var first = Call(panel, "LoadTransferAsync", Guid.NewGuid());
        Navigate(panel, panel.OrganizationId, Guid.NewGuid()); var second = Call(panel, "LoadListAsync", false);
        handler.First.SetResult(new(HttpStatusCode.Forbidden)); await first;
        Assert.True((bool)Get(panel, "_busy")!); Assert.Null(Get(panel, "_message"));
        handler.Second.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryTransferPage([], null)) }); await second;
        Assert.False((bool)Get(panel, "_busy")!);
    }
    [Fact]
    public async Task InspectorCoreItemsArePreparedAsTransferBlocks()
    {
        var id = Guid.NewGuid();
        using var handler = new TransferHttp(Guid.NewGuid(), Guid.NewGuid());
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = new MemoryTransferPanel();
        Set(panel, "_target", Guid.NewGuid());
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
    [Theory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task SharedPreparationRequiresTheExactAudienceAndPreservesItOnRetry(string scope)
    {
        var target = Guid.NewGuid(); var audience = Guid.NewGuid();
        using var handler = new TransferHttp(Guid.NewGuid(), target);
        using var http = new HttpClient(handler) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        Set(panel, "_target", target); Set(panel, "_scope", scope); Set(panel, "_sourceAudience", audience); Set(panel, "_debrief", "shared notes");
        await Call(panel, "PrepareAsync"); Assert.Empty(handler.Writes);
        Set(panel, "_audiences", new[] { new MemoryTransferAudience(scope, audience, "Shared group") });
        await Call(panel, "PrepareAsync"); Assert.NotNull(Get(panel, "_pendingPrepare"));
        Navigate(panel, panel.OrganizationId, Guid.NewGuid()); await Call(panel, "RetryAsync");
        Assert.Equal(handler.Writes[0], handler.Writes[1]); Assert.Equal(handler.WriteUrls[0], handler.WriteUrls[1]);
        using var payload = JsonDocument.Parse(handler.Writes[0]);
        Assert.Equal(audience, payload.RootElement.GetProperty("sourceAudienceId").GetGuid());
        Assert.Equal(scope, payload.RootElement.GetProperty("sourceScope").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedSharedAudiencesCannotReappearAfterNavigation(bool back)
    {
        using var handler = new DelayedTransferHttp(); using var http = new HttpClient(handler) { BaseAddress = new("http://test/") };
        var panel = Panel(http); var organization = panel.OrganizationId; var employee = panel.EmployeeId;
        Set(panel, "_audiences", new[] { new MemoryTransferAudience("Team", Guid.NewGuid(), "Old group") });
        var read = Call(panel, "TargetChangedAsync", Guid.NewGuid());
        Assert.Empty((System.Collections.IEnumerable)Get(panel, "_audiences")!);
        Navigate(panel, panel.OrganizationId, Guid.NewGuid()); if (back) Navigate(panel, organization, employee);
        handler.Response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new MemoryTransferAudience("Team", Guid.NewGuid(), "Private group") }) });
        await read;
        Assert.Empty((System.Collections.IEnumerable)Get(panel, "_audiences")!); Assert.False((bool)Get(panel, "_busy")!);
    }
    [Fact]
    public void SelectedMemoriesFromDifferentSharedAudiencesCannotBeMixed()
    {
        using var http = new HttpClient(new DeniedHttp()) { BaseAddress = new("http://test/") }; var panel = Panel(http);
        AgentMemoryItemResponse Item(Guid group) => new(Guid.NewGuid(), "Episode", "Team", null, "Group", "Title", "Content", "user", "Personal", "Current", null,
            DateTimeOffset.UtcNow, null, null) { AudienceId = group };
        typeof(MemoryTransferPanel).GetProperty(nameof(MemoryTransferPanel.Selected))!.SetValue(panel, Item(Guid.NewGuid()));
        typeof(MemoryTransferPanel).GetMethod("AddSelected", Members)!.Invoke(panel, null);
        typeof(MemoryTransferPanel).GetProperty(nameof(MemoryTransferPanel.Selected))!.SetValue(panel, Item(Guid.NewGuid()));
        typeof(MemoryTransferPanel).GetMethod("AddSelected", Members)!.Invoke(panel, null);
        Assert.Single((IEnumerable<AgentMemoryItemResponse>)Get(panel, "_items")!);
        Assert.Contains("one audience", (string)Get(panel, "_message")!);
    }
    private static object? Get(object panel, string field) => typeof(MemoryTransferPanel).GetField(field, Members)!.GetValue(panel);
    private static Task Call(object panel, string method, params object[] args) => (Task)typeof(MemoryTransferPanel).GetMethod(method, Members)!.Invoke(panel, args)!;
    private sealed class TransferHttp(Guid package, Guid target) : HttpMessageHandler
    {
        public List<string> Writes { get; } = [];
        public List<string> WriteUrls { get; } = [];
        public List<string> ReadUrls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Writes.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                WriteUrls.Add(request.RequestUri!.ToString());
                if (Writes.Count == 1) throw new HttpRequestException("Response lost after server commit.");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryTransferResult(Guid.NewGuid(), package, "Applied", Guid.NewGuid(), true)) };
            }
            ReadUrls.Add(request.RequestUri!.ToString());
            return new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith(package.ToString())
                ? JsonContent.Create(new MemoryTransferResponse(package, target, "Applied", new string('b', 64), "content", "Personal", false, false, true, Guid.NewGuid()))
                : JsonContent.Create(new MemoryTransferPage([], null)) };
        }
    }
    private sealed class DelayedTransferHttp : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Response.Task; }
    }
    private sealed class TwoDelayedReadsHttp : HttpMessageHandler
    {
        private int calls;
        public TaskCompletionSource<HttpResponseMessage> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => ++calls == 1 ? First.Task : Second.Task;
    }
    private sealed class WrongPackageHttp : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(token)));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MemoryTransferResult(Guid.NewGuid(), Guid.Empty, "Applied", Guid.NewGuid(), true)) };
        }
    }
    private sealed class DeniedHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
