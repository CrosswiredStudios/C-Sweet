using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeWorkEndpointTests
{
    [Fact]
    public async Task Discovery_and_claim_return_only_scoped_work_and_duplicate_claim_is_no_content()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var env = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        var request = new ComputeProviderWorkRequest(env.OrganizationId, env.ProviderNodeId!.Value, env.ProviderId!, Guid.NewGuid(), f.Time.Now, f.Time.Now.AddMinutes(1));
        var trust = new ComputeResultTests.Trust(new("node", request.OrganizationId, request.NodeId, request.ProviderId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        var discovery = await ComputeWorkEndpoints.DiscoverAsync(Context(request, key), service, default);
        Assert.Equal(f.OperationId, Assert.Single(Assert.IsType<ComputeProviderWorkPage>(Assert.IsAssignableFrom<IValueHttpResult>(discovery).Value).OperationIds));
        request = request with { OperationId = f.OperationId };
        var claim = await ComputeWorkEndpoints.ClaimAsync(Context(request, key), service, default);
        Assert.IsType<ComputeDispatchPacket>(Assert.IsAssignableFrom<IValueHttpResult>(claim).Value);
        Assert.Equal(204, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await ComputeWorkEndpoints.ClaimAsync(Context(request, key), service, default)).StatusCode);
        trust.Revoked = true;
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await ComputeWorkEndpoints.ClaimAsync(Context(request, key), service, default)).StatusCode);
    }

    [Theory]
    [InlineData("http", 403)]
    [InlineData("encoded", 415)]
    [InlineData("media", 415)]
    [InlineData("declared", 413)]
    [InlineData("streamed", 413)]
    [InlineData("json", 400)]
    public async Task Transport_rejects_invalid_requests_before_calling_service(string kind, int status)
    {
        var http = new DefaultHttpContext(); http.Request.Scheme = "https"; http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream("{invalid"u8.ToArray());
        if (kind == "http") http.Request.Scheme = "http";
        if (kind == "encoded") http.Request.Headers.ContentEncoding = "gzip";
        if (kind == "media") http.Request.ContentType = "text/plain";
        if (kind == "declared") http.Request.ContentLength = ComputeWorkTransport.MaximumRequestBytes + 1;
        if (kind == "streamed") http.Request.Body = new MemoryStream(new byte[ComputeWorkTransport.MaximumRequestBytes + 1]);
        var result = await ComputeWorkEndpoints.DiscoverAsync(http, null!, default);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl);
    }

    private static DefaultHttpContext Context(ComputeProviderWorkRequest request, ECDsa key)
    {
        var json = JsonSerializer.Serialize(request, ComputeProtocol.Json);
        var envelope = new SignedComputeProviderWorkRequest("node", json, Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
        var http = new DefaultHttpContext(); http.Request.Scheme = "https"; http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(envelope, ComputeProtocol.Json)); return http;
    }
}
