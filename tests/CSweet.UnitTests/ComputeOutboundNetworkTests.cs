using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeOutboundNetworkTests
{
    [Theory]
    [InlineData("approved", true)]
    [InlineData("missing", false)]
    [InlineData("registry-only", false)]
    public async Task Normal_network_VM_requires_independent_general_outbound_authority(string condition, bool allowed)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        foreach (var grant in await f.Db.ScopedActionGrants.ToArrayAsync())
        {
            var limits = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
            grant.ConstraintsJson = JsonSerializer.Serialize(limits with { AllowOutbound = true }, ComputeProtocol.Json);
        }
        if (condition != "missing")
        {
            await ComputeWorkloadTests.GrantAsync(f, InfrastructureActions.Outbound);
            var grant = await f.Db.ScopedActionGrants.SingleAsync(g => g.Action == InfrastructureActions.Outbound);
            var limits = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
            grant.ConstraintsJson = JsonSerializer.Serialize(limits with { AllowOutbound = true,
                AllowedDependencyRegistries = condition == "registry-only" ? ["registry.npmjs.org"] : null }, ComputeProtocol.Json);
        }
        await f.Db.SaveChangesAsync();
        var request = f.Request with { Specification = f.Request.Specification with { Network = new(ComputeNetworkMode.OutboundOnly, true) } };
        if (allowed)
        {
            await f.Send(request);
            var authority = JsonSerializer.Deserialize<ComputeActionAuthorization[]>((await f.Db.ComputeOperations.SingleAsync()).AuthorityJson, ComputeProtocol.Json)!;
            Assert.Contains(authority, a => a.Action == InfrastructureActions.Outbound);
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Send(request));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Ordinary_network_workloads_revalidate_separate_outbound_and_preview_grants(bool revoked, bool publish)
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var db = f.Broker.Db; var environment = await db.ComputeEnvironments.SingleAsync();
        environment.State = ComputeLifecycleState.Ready; environment.ProviderResourceId = Guid.NewGuid().ToString("D");
        var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!;
        environment.SpecificationJson = JsonSerializer.Serialize(spec with { Network = new(ComputeNetworkMode.OutboundOnly, true) }, ComputeProtocol.Json);
        (await db.ComputeOperations.SingleAsync()).Status = "Completed";
        foreach (var action in publish ? new[] { InfrastructureActions.Inbound, InfrastructureActions.PublishPort, InfrastructureActions.Outbound } :
            new[] { InfrastructureActions.Execute, InfrastructureActions.Outbound })
        {
            await ComputeWorkloadTests.GrantAsync(f.Broker, action);
            var grant = await db.ScopedActionGrants.SingleAsync(g => g.Action == action);
            var limits = JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson, ComputeProtocol.Json)!;
            grant.ConstraintsJson = JsonSerializer.Serialize(limits with { AllowOutbound = true,
                AllowedPublishedPorts = action == InfrastructureActions.Outbound ? null : limits.AllowedPublishedPorts }, ComputeProtocol.Json);
        }
        await db.SaveChangesAsync();
        var operation = await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization, f.Broker.Installation,
            new(environment.Id, environment.Generation, "normal-network-command", publish ? new(PublishPort: 8080) :
                new(new(Guid.NewGuid(), "/bin/echo", "/tmp", ["npm ci"], 30, 8192))), default);
        if (revoked) { (await db.ScopedActionGrants.SingleAsync(g => g.Action == InfrastructureActions.Outbound)).RevokedAt = f.Time.Now; await db.SaveChangesAsync(); }
        var dispatch = await f.Authorizer().ClaimAsync(operation.Id, default);
        if (revoked) Assert.Null(dispatch);
        else { Assert.NotNull(dispatch); Assert.Contains(f.Signing.Claims.Last().Grants, g => g.Action == InfrastructureActions.Outbound); }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Instance_preview_grants_count_only_their_instance_and_keep_outbound_quota(bool boundPreview, bool restrictOutbound, bool allowed)
    {
        await using var f = new ComputeDispatchTests.Fixture(); await f.InitializeAsync();
        var db=f.Broker.Db;var environment=await db.ComputeEnvironments.SingleAsync();
        environment.State=ComputeLifecycleState.Ready;environment.ProviderResourceId=Guid.NewGuid().ToString("D");
        var spec=JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson,ComputeProtocol.Json)!;
        environment.SpecificationJson=JsonSerializer.Serialize(spec with {Network=new(ComputeNetworkMode.OutboundOnly,true)},ComputeProtocol.Json);
        (await db.ComputeOperations.SingleAsync()).Status="Completed";
        db.ComputeEnvironments.Add(new(){Id=Guid.NewGuid(),OrganizationId=environment.OrganizationId,InstallationId=environment.InstallationId,
            WorkstreamId=environment.WorkstreamId,ProviderId=environment.ProviderId,ProviderNodeId=environment.ProviderNodeId,
            DesiredEnvironmentKey="retained-old-workspace",State=ComputeLifecycleState.Ready});
        foreach(var action in new[]{InfrastructureActions.Inbound,InfrastructureActions.PublishPort,InfrastructureActions.Outbound}) {
            await ComputeWorkloadTests.GrantAsync(f.Broker,action);
            var grant=await db.ScopedActionGrants.SingleAsync(g=>g.Action==action);
            var limits=JsonSerializer.Deserialize<ComputeGrantConstraints>(grant.ConstraintsJson,ComputeProtocol.Json)!;
            grant.ConstraintsJson=JsonSerializer.Serialize(limits with {AllowOutbound=true,
                MaximumConcurrentEnvironments=action==InfrastructureActions.Outbound&&!restrictOutbound?2:1,
                EnvironmentId=action!=InfrastructureActions.Outbound&&boundPreview?environment.Id:null},ComputeProtocol.Json);
        }
        await db.SaveChangesAsync();
        var operation=await f.Broker.Broker.SubmitWorkloadAsync(f.Broker.Organization,f.Broker.Installation,
            new(environment.Id,environment.Generation,"two-workspace-preview",new(PublishPort:8080)),default);
        Assert.Equal(allowed,(await f.Authorizer().ClaimAsync(operation.Id,default)) is not null);
    }
}
