using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeProviderWakeEndpointTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("operation-selector")]
    public async Task Wake_rechecks_current_enrollment_and_returns_no_execution_authority(string scenario)
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var environment = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        var request = new ComputeProviderWorkRequest(f.Broker.Organization, environment.ProviderNodeId!.Value,
            environment.ProviderId!, Guid.NewGuid(), f.Time.Now, f.Time.Now.AddMinutes(1),
            scenario == "operation-selector" ? f.OperationId : null);
        var trust = new ComputeResultTests.Trust(new("node", request.OrganizationId, request.NodeId, request.ProviderId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        var json = JsonSerializer.Serialize(request, ComputeProtocol.Json);
        var envelope = new SignedComputeProviderWorkRequest("node", json,
            Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
        var http = new DefaultHttpContext(); http.Request.Scheme = "https"; http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(envelope, ComputeProtocol.Json));
        var channels = new ComputeProviderWakeChannels();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = ComputeWorkEndpoints.WakeAsync(http, service, channels, timeout.Token);
        if (scenario == "revoked") trust.Revoked = true;
        if (scenario == "expired") f.Time.Now = request.ExpiresAt;
        var eventId = Guid.NewGuid();
        Assert.Equal(scenario != "operation-selector", await channels.PublishAsync(request.OrganizationId, request.NodeId, request.ProviderId, eventId, default));
        var result = await waiting;
        if (scenario == "valid") Assert.Equal(eventId, Assert.IsType<JsonHttpResult<ComputeProviderWakeHint>>(result).Value!.EventId);
        else Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, (await f.Broker.Db.ComputeOperations.SingleAsync()).Attempts);
        Assert.Empty(f.Signing.Claims);
        Assert.False(await channels.PublishAsync(request.OrganizationId, request.NodeId, request.ProviderId, eventId, default));
    }
}
