using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeResultHttpClientTests
{
    [Fact]
    public async Task Lost_acknowledgement_retries_identical_evidence_and_accepts_unchanged_reconciliation()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var envelope = f.Sign(); var bodies = new List<string>(); var calls = 0;
        using var client = new ComputeResultHttpClient(new("https://core.example"), new Handler(async (request, token) =>
        {
            Assert.Equal(ComputeResultTransport.Path, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Empty(request.Headers);
            var json = await request.Content!.ReadAsStringAsync(token); bodies.Add(json);
            var received = JsonSerializer.Deserialize<SignedComputeProviderResult>(json, ComputeProtocol.Json)!;
            var applied = await f.Reconciler.ApplyAsync(received, token);
            if (++calls == 1) { Assert.True(applied); throw new HttpRequestException("Simulated lost response after commit."); }
            Assert.False(applied);
            return Reply(new(f.Result.OperationId, f.Result.Sequence, ComputeProtocol.Digest(received.PayloadJson), applied));
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeliverAsync(envelope, default));
        var ack = await client.DeliverAsync(envelope, default);
        Assert.False(ack.Applied); Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(1, (await f.Broker.Db.ComputeOperations.SingleAsync()).LastResultSequence);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("sequence")]
    [InlineData("digest")]
    [InlineData("redirect")]
    [InlineData("encoded")]
    [InlineData("oversized")]
    [InlineData("json")]
    public async Task Delivery_never_acknowledges_unmatched_or_invalid_response(string mode)
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var envelope = f.Sign(); var calls = 0;
        using var client = new ComputeResultHttpClient(new("https://core.example"), new Handler((_, _) =>
        {
            calls++;
            var ack = new ComputeResultAcknowledgement(f.Result.OperationId, f.Result.Sequence, ComputeProtocol.Digest(envelope.PayloadJson), false);
            if (mode == "operation") ack = ack with { OperationId = Guid.NewGuid() };
            if (mode == "sequence") ack = ack with { Sequence = ack.Sequence + 1 };
            if (mode == "digest") ack = ack with { PayloadDigest = "sha256:wrong" };
            var response = Reply(ack);
            if (mode == "redirect") { response.StatusCode = HttpStatusCode.TemporaryRedirect; response.Headers.Location = new("https://other.example"); }
            if (mode == "encoded") response.Content.Headers.ContentEncoding.Add("gzip");
            if (mode is "oversized" or "json")
            {
                response.Content.Dispose();
                response.Content = new StringContent(mode == "oversized" ? new string('x', 4097) : "{invalid", Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<IOException>(() => client.DeliverAsync(envelope, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Production_transport_disables_ambient_authority_and_rejects_non_origin_configuration()
    {
        using var handler = ComputeMaintenanceHttpClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.False(handler.UseDefaultCredentials);
        foreach (var origin in new[] { "http://core.example", "https://user:pass@core.example", "https://core.example/path", "https://core.example/?q=x", "https://core.example/#fragment" })
            Assert.Throws<ArgumentException>(() => new ComputeResultHttpClient(new(origin), handler));
    }

    private static HttpResponseMessage Reply(ComputeResultAcknowledgement ack) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(ack, ComputeProtocol.Json), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
