using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeWorkloadTests
{
    [Fact]
    public async Task Commands_are_authorized_deduplicated_and_atomically_wake_the_provider()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var environment = await ReadyAsync(f);
        var input = new RequestComputeWorkload(environment.Id, 1, "command", new(new(Guid.NewGuid(), "/bin/echo", "/tmp", ["Hello World"], 20, 1024)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, input, default));
        await GrantAsync(f, InfrastructureActions.Execute);
        var accepted = await f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, input, default);
        Assert.Equal(2, accepted.Generation);
        Assert.Equal(accepted.Id, (await f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, input, default)).Id);
        Assert.Equal(2, await f.Db.ComputeOperations.CountAsync());
        Assert.Single(await f.Db.ComputeProviderWakes.Where(x => x.OperationId == accepted.Id).ToListAsync());
        Assert.Single(await f.Db.AuditOutbox.Where(x => x.SourceEntityType == null).Where(x => x.RequestJson.Contains("compute.execute.v1")).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation,
            input with { Workload = new(input.Workload.Command! with { Arguments = ["Changed"] }) }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.ReadOperationAsync(f.Organization, Guid.NewGuid(), accepted.Id, default));
        Assert.Equal(accepted.Id, (await f.Broker.ReadOperationAsync(f.Organization, f.Installation, accepted.Id, default)).Id);
    }

    [Fact]
    public async Task Publishing_requires_both_network_grants_and_the_exact_guest_port()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var environment = await ReadyAsync(f);
        await GrantAsync(f, InfrastructureActions.PublishPort, 8080);
        var input = new RequestComputeWorkload(environment.Id, 1, "publish", new(PublishPort: 8080));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, input, default));
        await GrantAsync(f, InfrastructureActions.Inbound, 8080);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation,
            input with { Workload = new(PublishPort: 9090) }, default));
        var accepted = await f.Broker.SubmitWorkloadAsync(f.Organization, f.Installation, input, default);
        var operation = await f.Db.ComputeOperations.SingleAsync(x => x.Id == accepted.Id);
        Assert.Contains(InfrastructureActions.PublishPort, operation.AuthorityJson);
        Assert.Contains(InfrastructureActions.Inbound, operation.AuthorityJson);
        Assert.DoesNotContain(InfrastructureActions.Provision, operation.AuthorityJson);
    }

    [Theory]
    [InlineData("undispatched",true)] [InlineData("attempted",false)]
    [InlineData("completed",false)] [InlineData("changed-generation",false)]
    public async Task Exact_retry_reconsiders_only_authority_denial_before_any_dispatch(string condition,bool reconsidered)
    {
        await using var f=new ComputeBrokerTests.Fixture();await f.SeedAsync();
        var environment=await ReadyAsync(f);await GrantAsync(f,InfrastructureActions.Execute);
        var request=new RequestComputeWorkload(environment.Id,1,"retry-before-dispatch",new(new(Guid.NewGuid(),"/bin/echo","/tmp",["safe"],20,1024)));
        var accepted=await f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,request,default);
        var operation=await f.Db.ComputeOperations.SingleAsync(o=>o.Id==accepted.Id);
        operation.Status=condition=="completed"?"Completed":"Blocked";operation.FailureCode="dispatch-authority-denied";
        if(condition=="attempted")operation.Attempts=1;
        if(condition=="changed-generation")(await f.Db.ComputeEnvironments.SingleAsync()).Generation++;
        var wake=await f.Db.ComputeProviderWakes.SingleAsync(w=>w.Id==accepted.Id);wake.PublishedAt=DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        var replay=await f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,request,default);
        Assert.Equal(accepted.Id,replay.Id);Assert.Equal(accepted.Generation,replay.Generation);
        Assert.Equal(reconsidered,replay.Status=="Pending");
        Assert.Equal(reconsidered,(await f.Db.ComputeProviderWakes.SingleAsync(w=>w.Id==accepted.Id)).PublishedAt is null);
        Assert.Equal(2,await f.Db.ComputeOperations.CountAsync());
        if(reconsidered) {
            (await f.Db.ScopedActionGrants.SingleAsync(g=>g.Action==InfrastructureActions.Execute)).RevokedAt=DateTimeOffset.UtcNow.AddYears(-100);
            await f.Db.SaveChangesAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Broker.SubmitWorkloadAsync(f.Organization,f.Installation,request,default));
        }
    }

    [Fact]
    public void Workload_shape_and_limits_are_closed()
    {
        var command = new ComputeGuestCommand(Guid.NewGuid(), "/bin/echo", "/tmp", [], 30, 8192);
        Assert.Throws<ArgumentException>(() => new ComputeWorkload().Validate("linux"));
        Assert.Throws<ArgumentException>(() => new ComputeWorkload(command, 8080).Validate("linux"));
        Assert.Throws<ArgumentException>(() => new ComputeWorkload(PublishPort: 22).Validate("linux"));
        Assert.Throws<ArgumentException>(() => new ComputeWorkload(command with { MaximumOutputBytes = 8193 }).Validate("linux"));
        Assert.Throws<ArgumentException>(() => new ComputeWorkload(command with { TimeoutSeconds = 31 }).Validate("linux"));
    }

    internal static async Task<ComputeEnvironmentView> ReadyAsync(ComputeBrokerTests.Fixture f)
    {
        var view = await f.Send();
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        environment.State = ComputeLifecycleState.Ready; environment.ProviderResourceId = Guid.NewGuid().ToString("D");
        var provision = await f.Db.ComputeOperations.SingleAsync(); provision.Status = "Completed";
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        return view;
    }

    internal static async Task GrantAsync(ComputeBrokerTests.Fixture f, string action, int port = 8080)
    {
        var installation = await f.Db.AgentInstallations.Include(x => x.Grant).SingleAsync();
        var capabilities = JsonSerializer.Deserialize<List<string>>(installation.Grant!.RequiredCapabilitiesJson)!;
        capabilities.Add(action); installation.Grant.RequiredCapabilitiesJson = JsonSerializer.Serialize(capabilities);
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = f.Installation, ScopeKind = GrantScopeKind.Workstream,
            ScopeId = f.Workstream, Action = action, GrantedAt = new ComputeBrokerTests.Clock().GetUtcNow().AddDays(-1),
            ExpiresAt = new ComputeBrokerTests.Clock().GetUtcNow().AddHours(2),
            ConstraintsJson = JsonSerializer.Serialize(new ComputeGrantConstraints(1, new(2, 4096, 20480), 1, 600,
                ["linux"], ["x64"], ["ubuntu-clean"], AllowedPublishedPorts: [port]), ComputeProtocol.Json) });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
    }
}
