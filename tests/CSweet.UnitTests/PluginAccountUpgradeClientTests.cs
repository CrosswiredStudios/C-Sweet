using System.Net;
using System.Net.Http.Json;
using System.Text;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class PluginAccountUpgradeClientTests
{
    [Fact]
    public async Task StalledSuccessfulBodyHasEndToEndDeadlineAndExactlyOneSubmission()
    {
        var handler=new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(new Stalled())});
        using var http=new HttpClient(handler){BaseAddress=new("https://app.example"),Timeout=TimeSpan.FromMilliseconds(100)};
        var client=new PluginAccountApiClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.ApproveUpgradeAsync(Guid.NewGuid(),new("synthetic-review")));
        Assert.Equal(1,handler.Calls);Assert.Equal(HttpMethod.Post,handler.Method);
    }
    [Fact]
    public async Task CallerCancellationIsPreservedWithoutRetry()
    {
        var handler=new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(new Stalled())});
        using var http=new HttpClient(handler){BaseAddress=new("https://app.example"),Timeout=Timeout.InfiniteTimeSpan};
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new PluginAccountApiClient(http).UpgradeStatusAsync(Guid.NewGuid(),cancel.Token));
        Assert.True(cancel.IsCancellationRequested);Assert.Equal(1,handler.Calls);
    }
    [Theory]
    [InlineData(400)][InlineData(403)][InlineData(404)][InlineData(409)][InlineData(500)][InlineData(503)]
    public async Task ErrorsNeverRenderUntrustedResponseTextOrRepeatSubmission(int status)
    {
        var handler=new Handler(_=>new((HttpStatusCode)status){Content=new StringContent("{\"error\":\"CANARY-provider-proof-body\"}")});
        using var http=new HttpClient(handler){BaseAddress=new("https://app.example")};
        var error=await Assert.ThrowsAsync<ApiClientException>(()=>new PluginAccountApiClient(http).ApproveUpgradeAsync(Guid.NewGuid(),new("synthetic-review")));
        Assert.DoesNotContain("CANARY",error.Message);Assert.Equal(1,handler.Calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MalformedOrOversizedSuccessHasSafeUnconfirmedOutcome(bool oversized)
    {
        var handler=new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(oversized?new string('x',33000):"CANARY-invalid-json")});
        using var http=new HttpClient(handler){BaseAddress=new("https://app.example")};
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>new PluginAccountApiClient(http).ApproveUpgradeAsync(Guid.NewGuid(),new("synthetic-review")));
        Assert.DoesNotContain("CANARY",error.Message);Assert.Contains("Check upgrade status",error.Message);Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task ReceiptUsesExplicitTargetAndReviewAndDoesNotContainProviderCredentials()
    {
        var target=Guid.NewGuid();var progress=new PluginAccountUpgradeProgress(Guid.NewGuid(),Guid.NewGuid(),target,"Pending");
        var handler=new Handler(request=>{Assert.EndsWith($"/{target:D}/account-management/upgrade",request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(progress)};});
        using var http=new HttpClient(handler){BaseAddress=new("https://app.example")};
        var approval=new ApprovePluginAccountUpgrade("synthetic-review");Assert.DoesNotContain("synthetic-review",approval.ToString());
        Assert.Equal(progress,await new PluginAccountApiClient(http).ApproveUpgradeAsync(target,approval));Assert.Equal(1,handler.Calls);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> result):HttpMessageHandler
    {
        public int Calls;public HttpMethod? Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;Method=request.Method;return Task.FromResult(result(request));}
    }
    private sealed class Stalled:Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>new(new TaskCompletionSource<int>().Task);
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
