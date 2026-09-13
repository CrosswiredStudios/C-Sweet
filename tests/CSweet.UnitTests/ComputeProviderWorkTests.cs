using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeProviderWorkTests
{
    [Fact]
    public async Task Signed_discovery_and_claim_are_scoped_and_recovery_returns_observation_only()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var environment = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        var request = new ComputeProviderWorkRequest(f.Broker.Organization, environment.ProviderNodeId!.Value, environment.ProviderId!,
            Guid.NewGuid(), f.Time.Now, f.Time.Now.AddMinutes(1));
        var trust = new ComputeResultTests.Trust(new("node", request.OrganizationId, request.NodeId, request.ProviderId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        Assert.Equal(f.OperationId, Assert.Single((await service.DiscoverAsync(Sign(request, key), default)).OperationIds));
        var claimRequest = request with { OperationId = f.OperationId };
        Assert.NotNull(await service.ClaimAsync(Sign(claimRequest, key), default));
        Assert.Null(await service.ClaimAsync(Sign(claimRequest, key), default));
        Assert.Empty((await service.DiscoverAsync(Sign(request, key), default)).OperationIds);
        f.Time.Now = f.Time.Now.AddMinutes(1);
        claimRequest = claimRequest with { RequestId = Guid.NewGuid(), IssuedAt = f.Time.Now, ExpiresAt = f.Time.Now.AddMinutes(1) };
        Assert.NotNull(await service.ClaimAsync(Sign(claimRequest, key), default));
        Assert.Equal(ComputeDispatchMode.Observe, f.Signing.Claims[^1].Mode); Assert.Empty(f.Signing.Claims[^1].Grants);
        trust.Revoked = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClaimAsync(Sign(claimRequest, key), default));
    }

    [Fact]
    public async Task Another_enrolled_node_cannot_discover_or_claim_placed_work()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new ComputeProviderWorkRequest(f.Broker.Organization, Guid.NewGuid(), "test-provider", Guid.NewGuid(), f.Time.Now, f.Time.Now.AddMinutes(1));
        var trust = new ComputeResultTests.Trust(new("node", request.OrganizationId, request.NodeId, request.ProviderId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, f.Time), f.Authorizer(), f.Broker.Ledger, f.Time);
        Assert.Empty((await service.DiscoverAsync(Sign(request, key), default)).OperationIds);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClaimAsync(Sign(request with { OperationId = f.OperationId }, key), default));
        Assert.Equal(0, (await f.Broker.Db.ComputeOperations.SingleAsync()).Attempts); Assert.Empty(f.Signing.Claims);
    }

    [Fact]
    public async Task Work_proof_rejects_wrong_purpose_expiry_and_ambiguous_request_shape()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new ComputeDispatchTests.Clock();
        var request = new ComputeProviderWorkRequest(Guid.NewGuid(), Guid.NewGuid(), "test-provider", Guid.NewGuid(), clock.Now, clock.Now.AddMinutes(1));
        var trust = new ComputeResultTests.Trust(new("node", request.OrganizationId, request.NodeId, request.ProviderId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var verifier = new ComputeProviderWorkRequestVerifier(trust, clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.VerifyAsync(Sign(request, key, wrongPurpose: true), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.VerifyAsync(Sign(request with { ExpiresAt = clock.Now }, key), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => verifier.VerifyAsync(Sign(request with { OperationId = Guid.NewGuid(), AfterOperationId = Guid.NewGuid() }, key), default));
    }

    private static SignedComputeProviderWorkRequest Sign(ComputeProviderWorkRequest request, ECDsa key, bool wrongPurpose = false)
    {
        var json = JsonSerializer.Serialize(request, ComputeProtocol.Json);
        return new("node", json, Convert.ToBase64String(key.SignData(wrongPurpose ? ComputeProtocol.ResultPayload(json) :
            ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
    }
}
