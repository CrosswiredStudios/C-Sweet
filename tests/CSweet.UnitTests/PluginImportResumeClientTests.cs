using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class PluginImportResumeClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeBodyDeadlineCoversStalledSuccessfulResponseWithoutRetry(bool ignoreReadCancellation)
    {
        using var stalled = new StalledStream(ignoreReadCancellation);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stalled) });
        using var http = new HttpClient(handler, false) { BaseAddress = new("https://synthetic.invalid/"), Timeout = TimeSpan.FromMilliseconds(300) };
        using var caller = new CancellationTokenSource();
        var pending = new PluginAccountApiClient(http).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7, caller.Token);
        try
        {
            await stalled.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(pending, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(caller.IsCancellationRequested); Assert.True(stalled.Disposed);
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            caller.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeBodyDeadlinePreservesCallerCancellation(bool ignoreReadCancellation)
    {
        using var stalled = new StalledStream(ignoreReadCancellation);
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stalled) });
        using var http = new HttpClient(handler, false) { BaseAddress = new("https://synthetic.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        using var caller = new CancellationTokenSource();
        var pending = new PluginAccountApiClient(http).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7, caller.Token);
        await stalled.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(caller.IsCancellationRequested); Assert.True(stalled.Disposed); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OneSubmissionReturnsBoundedReceiptAndSendsExpectedRevision()
    {
        var setup = Guid.NewGuid(); var receipt = new PluginImportResumeReceipt(setup, 8, Guid.NewGuid());
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) });
        var client = Client(handler);
        Assert.Equal(receipt, await client.ResumeImportAsync(Guid.NewGuid(), setup, 7));
        Assert.Equal(1, handler.Requests); Assert.Contains("\"expectedRevision\":7", handler.LastBody);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task FailedAcknowledgmentNeverRendersRawResponseOrAutomaticallyRetries(int status)
    {
        using var handler = new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("CANARY access_token=private proof=signature") });
        var error = await Assert.ThrowsAsync<ApiClientException>(() => Client(handler).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7));
        Assert.DoesNotContain("CANARY", error.Message); Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LegacyNoContentIsUnconfirmedRatherThanInventedSuccess()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.NoContent));
        Assert.Null(await Client(handler).ResumeImportAsync(Guid.NewGuid(), Guid.NewGuid(), 7)); Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("wrong-setup")]
    [InlineData("stale-revision")]
    [InlineData("empty-correlation")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task InvalidReceiptIsUnconfirmedAndNeverResubmitted(string variant)
    {
        var setup = Guid.NewGuid(); var receipt = new PluginImportResumeReceipt(setup, 8, Guid.NewGuid());
        var json = variant switch
        {
            "wrong-setup" => JsonSerializer.Serialize(receipt with { SetupId = Guid.NewGuid() }),
            "stale-revision" => JsonSerializer.Serialize(receipt with { Revision = 7 }),
            "empty-correlation" => JsonSerializer.Serialize(receipt with { CorrelationId = Guid.Empty }),
            "duplicate" => $"{{\"setupId\":\"{setup}\",\"SetupId\":\"{setup}\",\"revision\":8,\"correlationId\":\"{receipt.CorrelationId}\"}}",
            "oversized" => new string('x', 2049) + "CANARY",
            _ => "CANARY malformed response"
        };
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).ResumeImportAsync(Guid.NewGuid(), setup, 7));
        Assert.DoesNotContain("CANARY", error.Message); Assert.Equal(1, handler.Requests);
    }

    private static PluginAccountApiClient Client(Handler handler) => new(new HttpClient(handler, false) { BaseAddress = new("https://synthetic.invalid/") });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests; public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct); return response(request); }
    }

    private sealed class StalledStream(bool ignoreReadCancellation) : Stream
    {
        private readonly CancellationTokenSource disposed = new();
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ignoreReadCancellation ? CancellationToken.None : ct, disposed.Token);
            ReadStarted.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, read.Token); return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (!Disposed) { Disposed = true; disposed.Cancel(); disposed.Dispose(); }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
