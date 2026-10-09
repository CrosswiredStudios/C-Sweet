using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.Communications;
using CSweet.Contracts.Realtime;
using CSweet.UI.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class WorkInstructionUiTests
{
    internal static async Task<string> PublishThroughUiAsync(HttpClient http, Guid org, Guid chat, Guid message,
        Guid board, Guid item, string source, int offset, int length)
    {
        await using var fixture = new Fixture(http, org, chat, message, board, item, source);
        fixture.JavaScript.Range = [offset,length];
        await fixture.OpenTargetAsync(); await fixture.Invoke("PreviewAsync");
        Assert.Contains("Publish this instruction", await fixture.Html());
        await fixture.Invoke("PublishAsync");
        return await fixture.Html();
    }
    [Fact]
    public async Task WorkInstructionUiReviewsExactSelectionAndRetriesLostResponseWithSameOperation()
    {
        await using var f = new Fixture();
        await f.OpenTargetAsync();
        await f.Invoke("PreviewAsync");
        Assert.Equal(f.Message, f.Handler.Selection!.MessageId);
        Assert.Equal(9, f.Handler.Selection.Offset); Assert.Equal(12, f.Handler.Selection.Length);
        var html = await f.Html();
        Assert.Contains("Publish to Production · Paddle change", html);
        Assert.Matches("<blockquote[^>]*>Make it blue</blockquote>", html);
        Assert.Contains("attributed to you", html);
        f.Handler.LoseResponse = true;
        await f.Invoke("PublishAsync");
        Assert.Contains("response was lost", await f.Html());
        await f.Invoke("PublishAsync");
        Assert.Equal(2, f.Handler.Publications.Count);
        Assert.Equal(f.Handler.Publications[0], f.Handler.Publications[1]);
        Assert.Contains("Instruction published", await f.Html());
        Assert.Contains($"?item={f.Handler.Item:D}", await f.Html());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task WorkInstructionUiDeniedPublicationRequiresAnotherReview(HttpStatusCode status)
    {
        await using var f = new Fixture(); await f.OpenTargetAsync(); await f.Invoke("PreviewAsync");
        f.Handler.PublishStatus = status; await f.Invoke("PublishAsync");
        var html = await f.Html();
        Assert.DoesNotContain("Publish this instruction", html);
        Assert.Contains("Review highlighted text", html);
        Assert.Contains("role=\"alert\"", html);
        await f.Invoke("PublishAsync"); Assert.Single(f.Handler.Publications);
    }

    [Fact]
    public async Task WorkInstructionUiChangingTargetAndChatCannotReuseReviewedTextOrLateReply()
    {
        await using var f = new Fixture(); await f.OpenTargetAsync(); await f.Invoke("PreviewAsync");
        await f.Invoke("ChangeItemAsync", new ChangeEventArgs { Value = Guid.NewGuid().ToString() });
        await f.Invoke("PublishAsync"); Assert.Empty(f.Handler.Publications);
        f.Handler.PreviewReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = f.Invoke("PreviewAsync");
        await f.Handler.PreviewStarted.Task;
        await f.SetParameters(Guid.NewGuid());
        f.Handler.PreviewReply.SetResult(new("Make it blue", "Old private case", "Production", new string('a',64)));
        await pending;
        Assert.DoesNotContain("Old private case", await f.Html());
        await f.Invoke("PublishAsync"); Assert.Empty(f.Handler.Publications);
    }

    [Fact]
    public async Task WorkInstructionUiDiscoveryPagesDeduplicatesAndRemovesDeniedCachedReceipts()
    {
        await using var f = new Fixture(); await f.OpenTargetAsync();
        f.Handler.History = new([new(f.Handler.Publication, f.Handler.Item, Guid.NewGuid(), "Changed", true)], f.Handler.Publication);
        await f.Invoke("RefreshPublicationsAsync");
        Assert.Contains("edited since publication", await f.Html());
        f.Handler.History = f.Handler.History with { NextCursor = null };
        await f.Invoke("LoadMoreAsync");
        Assert.EndsWith($"?cursor={f.Handler.Publication:D}", f.Handler.HistoryPaths.Last());
        Assert.Equal(1, (await f.Html()).Split("edited since publication").Length - 1);
        f.Handler.HistoryStatus = HttpStatusCode.Forbidden;
        await f.Invoke("RefreshPublicationsAsync");
        Assert.DoesNotContain("edited since publication", await f.Html());
        Assert.Contains("no longer have access", await f.Html());
        f.Handler.HistoryStatus = HttpStatusCode.OK;
        f.Handler.History = new([new(f.Handler.Publication, f.Handler.Item, Guid.NewGuid(), "Withdrawn", true)], null);
        await f.Invoke("RefreshPublicationsAsync"); Assert.Contains("withdrawn", await f.Html());
    }

    [Fact]
    public async Task WorkInstructionUiEmptyHighlightCannotReachPreviewOrPublication()
    {
        await using var f = new Fixture(); await f.OpenTargetAsync(); f.JavaScript.Range = [-1, 0];
        await f.Invoke("PreviewAsync"); await f.Invoke("PublishAsync");
        Assert.Null(f.Handler.Selection); Assert.Empty(f.Handler.Publications);
        Assert.Contains("Highlight the relevant instruction", await f.Html());
    }

    [Fact]
    public async Task WorkInstructionUiWakeAndReconnectReauthorizeCurrentStateWithoutTrustingHintContent()
    {
        await using var f = new Fixture(); await f.OpenTargetAsync(); await f.Invoke("PreviewAsync");
        var before = f.Handler.HistoryPaths.Count;
        await f.Invoke("OnEvent", new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, AppRealtimeEvents.WorkBoardChanged,
            Guid.NewGuid(), "work", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { boardId = f.Handler.Board, itemId = f.Handler.Item })));
        Assert.Equal(before, f.Handler.HistoryPaths.Count);
        f.Handler.HistoryStatus = HttpStatusCode.Forbidden;
        await f.Invoke("OnReconnect");
        // Renderer dispatcher drains the queued current-state read.
        var html = await f.Html();
        Assert.Contains("no longer have access", html); Assert.DoesNotContain("Publish this instruction", html);
        await f.Invoke("PublishAsync"); Assert.Empty(f.Handler.Publications);
        f.Handler.HistoryStatus = HttpStatusCode.OK;
        f.Handler.History = new([new(f.Handler.Publication, f.Handler.Item, Guid.NewGuid(), "Changed", true)], null);
        await f.Invoke("OnEvent", new AppRealtimeEventEnvelope(Guid.NewGuid(), 2, AppRealtimeEvents.WorkBoardChanged,
            f.Organization, "work", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { boardId = f.Handler.Board, itemId = f.Handler.Item, body = "Do not trust this event" })));
        html = await f.Html(); Assert.Contains("edited since publication", html); Assert.DoesNotContain("Do not trust this event", html);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _output;
        private readonly Capture _capture = new();
        public Guid Organization { get; } = Guid.NewGuid();
        public Guid Chat { get; } = Guid.NewGuid();
        public Guid Message { get; } = Guid.NewGuid();
        public Handler Handler { get; } = new();
        public Script JavaScript { get; } = new();
        private readonly string _source;
        public Fixture(HttpClient? http = null, Guid? org = null, Guid? chat = null, Guid? message = null,
            Guid? board = null, Guid? item = null, string? source = null)
        {
            Organization = org ?? Organization; Chat = chat ?? Chat; Message = message ?? Message;
            Handler.Board = board ?? Handler.Board; Handler.Item = item ?? Handler.Item;
            _source = source ?? "Private: Make it blue. My personal account.";
            var services = new ServiceCollection().AddLogging(); services.AddMudServices();
            services.AddSingleton<IJSRuntime>(JavaScript); services.AddSingleton<IComponentActivator>(_capture);
            services.AddSingleton(http ?? new HttpClient(Handler) { BaseAddress = new Uri("http://localhost/") });
            services.AddSingleton<AppRealtimeState>();
            _services = services.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        private ParameterView Parameters(Guid chat) => ParameterView.FromDictionary(new Dictionary<string, object?> {
            [nameof(ShareWorkInstruction.OrganizationId)] = Organization, [nameof(ShareWorkInstruction.ConversationId)] = chat,
            [nameof(ShareWorkInstruction.MessageId)] = Message, [nameof(ShareWorkInstruction.Content)] = _source });
        public Task SetParameters(Guid chat) => _renderer.Dispatcher.InvokeAsync(() => _capture.Component!.SetParametersAsync(Parameters(chat)));
        public async Task OpenTargetAsync()
        {
            _output = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<ShareWorkInstruction>(Parameters(Chat)));
            await Invoke("OpenAsync");
            await Invoke("ChangeBoardAsync", new ChangeEventArgs { Value = Handler.Board.ToString() });
            await Invoke("ChangeItemAsync", new ChangeEventArgs { Value = Handler.Item.ToString() });
        }
        public Task Invoke(string method, params object[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(ShareWorkInstruction).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_capture.Component, args);
            if (result is Task task) await task;
        });
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_output.ToHtmlString);
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator
    {
        public ShareWorkInstruction? Component { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is ShareWorkInstruction instruction) Component = instruction;
            return component;
        }
    }
    private sealed class Script : IJSRuntime, IJSObjectReference
    {
        public int[] Range { get; set; } = [9,12];
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) =>
            ValueTask.FromResult(identifier == "import" ? (T)(object)this : identifier == "selection" ? (T)(object)Range : default!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Guid Board { get; set; } = Guid.NewGuid(); public Guid Item { get; set; } = Guid.NewGuid();
        public Guid Publication { get; } = Guid.NewGuid();
        public SelectWorkInstructionRequest? Selection { get; private set; }
        public List<PublishWorkInstructionRequest> Publications { get; } = [];
        public List<string> HistoryPaths { get; } = [];
        public bool LoseResponse { get; set; }
        public HttpStatusCode PublishStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode HistoryStatus { get; set; } = HttpStatusCode.OK;
        public WorkInstructionPublicationPage History { get; set; } = new([], null);
        public TaskCompletionSource<WorkInstructionPreview>? PreviewReply { get; set; }
        public TaskCompletionSource PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; object result;
            if (path.EndsWith("/preview"))
            {
                Selection = await request.Content!.ReadFromJsonAsync<SelectWorkInstructionRequest>(token);
                PreviewStarted.TrySetResult();
                result = PreviewReply is null ? new WorkInstructionPreview("Make it blue", "Paddle change", "Production", new string('a',64)) : await PreviewReply.Task;
            }
            else if (path.EndsWith("/publish"))
            {
                Publications.Add((await request.Content!.ReadFromJsonAsync<PublishWorkInstructionRequest>(token))!);
                if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("Response lost after commit"); }
                return new(PublishStatus) { Content = JsonContent.Create(new WorkInstructionPublicationResponse(Publication, Item, Guid.NewGuid(), "Published", true)) };
            }
            else if (path.EndsWith("/publications"))
            {
                HistoryPaths.Add(request.RequestUri.PathAndQuery);
                return new(HistoryStatus) { Content = JsonContent.Create(History) };
            }
            else
            {
                var board = new WorkBoardSummaryResponse(Board, Guid.NewGuid(), null, "Production", "", true, false, false, null,1,1,1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
                result = path.EndsWith("/boards") ? new WorkBoardDirectoryResponse([board], false) :
                    new WorkBoardDetailResponse(board, [], [new(Item, Board, Guid.NewGuid(), null, null, "Task", "Paddle change", "", "Ready", "Medium", null,0,1,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)]);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
}
