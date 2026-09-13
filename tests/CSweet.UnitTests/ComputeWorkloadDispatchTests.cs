using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeWorkloadDispatchTests
{
    [Fact]
    public async Task Dispatch_binds_command_bytes_and_observation_cannot_reexecute_them()
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var environment = await f.Broker.Db.ComputeEnvironments.SingleAsync();
        environment.State = ComputeLifecycleState.Ready; environment.ProviderResourceId = Guid.NewGuid().ToString("D");
        (await f.Broker.Db.ComputeOperations.SingleAsync()).Status = "Completed"; await f.Broker.Db.SaveChangesAsync();
        await ComputeWorkloadTests.GrantAsync(f.Broker, InfrastructureActions.Execute);
        var request = new RequestComputeWorkload(environment.Id, 1, "run", new(new(Guid.NewGuid(), "/bin/echo", "/tmp", ["Hello World"], 10, 1024)));
        var operation = await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization, f.Broker.Installation, request, default);
        var packet = (await f.Authorizer().ClaimAsync(operation.Id, default))!;
        var claim = f.Signing.Claims.Last();
        var verifier = new ComputeDispatchVerifier(new(claim.OrganizationId, claim.NodeId, claim.ProviderId, await f.Signing.GetIdentityAsync(default)),
            new(claim.ProviderId, ["ubuntu-clean"], [InfrastructureActions.Execute], [ComputeNetworkMode.None], false, false),
            new(4, 8192, 40960), new Dictionary<string, ComputeTemplate>(), f.Time);
        Assert.NotNull(verifier.Verify(packet).Workload?.Command);
        Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(packet with { Workload = new(request.Workload.Command! with { Arguments = ["different"] }) }));
        f.Time.Now = f.Time.Now.AddSeconds(61);
        var recovery = (await f.Authorizer().ClaimAsync(operation.Id, default))!;
        Assert.Null(verifier.Verify(recovery).Workload);
        Assert.Equal(ComputeDispatchMode.Observe, verifier.Verify(recovery).Authorization.Mode);
        Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(recovery with { Workload = request.Workload }));
    }

    [Fact]
    public async Task Published_connections_recheck_current_grants_and_exact_node_ownership()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var environment = await ComputeWorkloadTests.ReadyAsync(f);
        await ComputeWorkloadTests.GrantAsync(f, InfrastructureActions.PublishPort);
        await ComputeWorkloadTests.GrantAsync(f, InfrastructureActions.Inbound);
        var operation = await f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, new(environment.Id, 1, "publish", new(PublishPort: 8080)), default);
        (await f.Db.ComputeOperations.SingleAsync(x => x.Id == operation.Id)).Status = "Dispatching"; await f.Db.SaveChangesAsync();
        var placed = await f.Db.ComputeEnvironments.SingleAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new ComputeResultTests.Trust(new("node", f.Organization, placed.ProviderNodeId!.Value, placed.ProviderId!, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var access = new ComputePublicationAccess(f.Db, new(trust, new ComputeBrokerTests.Clock()), f.Broker, new ComputeBrokerTests.Clock());
        var now = new ComputeBrokerTests.Clock().GetUtcNow();
        var proof = new ComputeProviderWorkRequest(f.Organization, placed.ProviderNodeId.Value, placed.ProviderId!, Guid.NewGuid(), now, now.AddMinutes(1), operation.Id);
        string json = JsonSerializer.Serialize(proof, ComputeProtocol.Json);
        var signed = new SignedComputeProviderWorkRequest("node", json, Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
        Assert.True((await access.AuthorizeAsync(signed, default)).Allowed);
        (await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Inbound)).RevokedAt = now;
        await f.Db.SaveChangesAsync();
        Assert.False((await access.AuthorizeAsync(signed, default)).Allowed);
    }
}
