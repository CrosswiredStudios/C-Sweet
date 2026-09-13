using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeResultEndpointTests
{
    [Fact]
    public async Task Runtime_signed_observation_is_reconciled_before_exact_digest_acknowledgement_and_replay_is_unchanged()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        using var certificate = Certificate(f);
        var envelope = Signer(f, certificate).Sign(f.Result);
        var response = await ComputeResultEndpoint.ReceiveAsync(Context(envelope), f.Reconciler, default);
        var ack = Assert.IsType<ComputeResultAcknowledgement>(Assert.IsAssignableFrom<IValueHttpResult>(response).Value);
        Assert.True(ack.Applied); Assert.Equal(f.Result.OperationId, ack.OperationId); Assert.Equal(1, ack.Sequence);
        Assert.Equal(ComputeProtocol.Digest(envelope.PayloadJson), ack.PayloadDigest);
        Assert.Equal(f.Result.State, (await f.Broker.Db.ComputeEnvironments.SingleAsync()).State);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        var replay = await ComputeResultEndpoint.ReceiveAsync(Context(envelope), f.Reconciler, default);
        var replayAck = Assert.IsType<ComputeResultAcknowledgement>(Assert.IsAssignableFrom<IValueHttpResult>(replay).Value);
        Assert.False(replayAck.Applied); Assert.Equal(ack.PayloadDigest, replayAck.PayloadDigest);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_state_change_or_acknowledgement()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var before = (await f.Broker.Db.ComputeEnvironments.SingleAsync()).State;
        var response = await ComputeResultEndpoint.ReceiveAsync(Context(f.Sign() with { SignatureBase64 = "invalid" }), f.Reconciler, default);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.Equal(before, (await f.Broker.Db.ComputeEnvironments.SingleAsync()).State);
    }

    [Theory]
    [InlineData("http", 403)]
    [InlineData("encoded", 415)]
    [InlineData("declared-size", 413)]
    [InlineData("streamed-size", 413)]
    [InlineData("json", 400)]
    public async Task Ingress_enforces_transport_and_body_limits(string kind, int expected)
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var http = Context(f.Sign());
        if (kind == "http") http.Request.Scheme = "http";
        if (kind == "encoded") http.Request.Headers.ContentEncoding = "gzip";
        if (kind == "declared-size") http.Request.ContentLength = ComputeResultTransport.MaximumRequestBytes + 1;
        if (kind == "streamed-size") http.Request.Body = new MemoryStream(new byte[ComputeResultTransport.MaximumRequestBytes + 1]);
        if (kind == "json") http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{invalid"));
        var response = await ComputeResultEndpoint.ReceiveAsync(http, f.Reconciler, default);
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.Equal(0, (await f.Broker.Db.ComputeOperations.SingleAsync()).LastResultSequence);
    }

    [Fact]
    public async Task Signer_cannot_change_scope_or_refresh_expired_observation_or_invent_teardown()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        using var certificate = Certificate(f);
        var signer = Signer(f, certificate);
        Assert.Throws<InvalidDataException>(() => signer.Sign(f.Result with { OrganizationId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => signer.Sign(f.Result with { ExpiresAt = f.Result.ObservedAt }));
        Assert.Throws<InvalidDataException>(() => signer.Sign(f.Result with { TeardownConfirmed = true }));
        using var expiring = new CertificateRequest("CN=result-expiry-test", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(f.Result.ObservedAt.AddDays(-1), f.Result.ObservedAt.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => Signer(f, expiring).Sign(f.Result));
    }

    private static DefaultHttpContext Context(SignedComputeProviderResult envelope)
    {
        var http = new DefaultHttpContext(); http.Request.Scheme = "https"; http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(envelope, ComputeProtocol.Json));
        return http;
    }
    private static X509Certificate2 Certificate(ComputeResultTests.Fixture f) =>
        new CertificateRequest("CN=compute-result-test", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(f.Result.ObservedAt.AddDays(-1), f.Result.ObservedAt.AddDays(1));
    private static ComputeProviderResultSigner Signer(ComputeResultTests.Fixture f, X509Certificate2 certificate) =>
        new(new(f.Result.OrganizationId, f.Result.NodeId, f.Result.ProviderId,
            new("control-plane", Convert.ToBase64String(f.Key.ExportSubjectPublicKeyInfo()))), certificate, "key-1", new ComputeBrokerTests.Clock());
}
