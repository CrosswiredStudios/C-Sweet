using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ExecutionPoolAdministrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdministratorCanCreatePolicyAndPromotePoolToBothDefaults()
    {
        await using var db = CreateDb();
        var service = await CreateServiceAsync(db);
        var original = await db.ExecutionPools.SingleAsync();

        var created = await service.CreatePoolAsync(new CreateExecutionPoolRequest(
            "GPU West", 25,
            new Dictionary<string, string> { ["accelerator"] = "gpu", ["region"] = "west" },
            ["business-a"]));
        var pool = await db.ExecutionPools.SingleAsync(x => x.Name == "GPU West");
        var updated = await service.UpdatePoolAsync(pool.Id, new UpdateExecutionPoolRequest(
            "GPU West", true, 40,
            new Dictionary<string, string> { ["accelerator"] = "gpu" },
            ["business-a", "business-b"], true, true));

        await db.Entry(original).ReloadAsync();
        var settings = await db.AgentRuntimeGlobalSettings.SingleAsync();
        Assert.True(created.Succeeded);
        Assert.True(updated.Succeeded);
        Assert.False(original.IsDefaultBuildPool);
        Assert.False(original.IsDefaultRuntimePool);
        Assert.Equal(pool.Id, settings.DefaultBuildExecutionPoolId);
        Assert.Equal(pool.Id, settings.DefaultRuntimeExecutionPoolId);
        Assert.Equal(40, (await db.ExecutionPools.SingleAsync(x => x.Id == pool.Id)).MaximumActiveWorkloads);
    }

    [Fact]
    public async Task DefaultOrBusyPoolCannotBeDisabled()
    {
        await using var db = CreateDb();
        var service = await CreateServiceAsync(db);
        var defaultPool = await db.ExecutionPools.SingleAsync();
        var defaultResult = await service.UpdatePoolAsync(defaultPool.Id, new UpdateExecutionPoolRequest(
            defaultPool.Name, false, 100, new Dictionary<string, string>(), [], false, false));

        await service.CreatePoolAsync(new CreateExecutionPoolRequest(
            "Busy", 10, new Dictionary<string, string>(), []));
        var busy = await db.ExecutionPools.SingleAsync(x => x.Name == "Busy");
        db.ExecutionWorkloadAssignments.Add(new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), ExecutionPoolId = busy.Id, AgentBuildJobId = Guid.NewGuid(),
            WorkloadKind = ExecutionWorkloadKind.Builder, Status = ExecutionAssignmentStatus.Running,
            ProviderId = "provider", GuestImageDigest = "sha256:" + new string('a', 64),
            SpecificationDigest = "sha256:" + new string('b', 64), QueuedAt = Now
        });
        await db.SaveChangesAsync();
        var busyResult = await service.UpdatePoolAsync(busy.Id, new UpdateExecutionPoolRequest(
            busy.Name, false, 10, new Dictionary<string, string>(), [], false, false));

        Assert.Equal("default_pool_required", defaultResult.ErrorCode);
        Assert.Equal("pool_has_active_work", busyResult.ErrorCode);
    }

    [Fact]
    public async Task InstallationOverrideEnforcesBusinessAllowlistAndCanReturnToDefault()
    {
        await using var db = CreateDb();
        var service = await CreateServiceAsync(db);
        await service.CreatePoolAsync(new CreateExecutionPoolRequest(
            "Restricted", 10, new Dictionary<string, string>(), ["business-a"]));
        var pool = await db.ExecutionPools.SingleAsync(x => x.Name == "Restricted");
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), AgentId = "agent", AgentName = "Agent",
            Version = "1.0.0", ImportedAt = Now
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
            PackageVersion = package, BusinessId = "business-b", RevisionStatus = PluginRevisionStatus.Active,
            CreatedAt = Now, UpdatedAt = Now
        };
        db.AgentPackageVersions.Add(package);
        db.AgentInstallations.Add(installation);
        await db.SaveChangesAsync();

        var rejected = await service.SetInstallationPoolAsync(
            installation.Id, new UpdateAgentExecutionPoolRequest(pool.Id));
        pool.AllowedBusinessIdsJson = "[]";
        await db.SaveChangesAsync();
        var accepted = await service.SetInstallationPoolAsync(
            installation.Id, new UpdateAgentExecutionPoolRequest(pool.Id));
        var cleared = await service.SetInstallationPoolAsync(
            installation.Id, new UpdateAgentExecutionPoolRequest(null));

        Assert.Equal("business_not_allowed", rejected.ErrorCode);
        Assert.True(accepted.Succeeded);
        Assert.True(cleared.Succeeded);
        Assert.Null((await db.AgentInstallations.SingleAsync()).ExecutionPoolId);
    }

    [Fact]
    public async Task ReferencedPoolCannotBeDeleted()
    {
        await using var db = CreateDb();
        var service = await CreateServiceAsync(db);
        await service.CreatePoolAsync(new CreateExecutionPoolRequest(
            "Referenced", 10, new Dictionary<string, string>(), []));
        var pool = await db.ExecutionPools.SingleAsync(x => x.Name == "Referenced");
        db.ExecutionNodes.Add(new CSweet.Domain.Setup.ExecutionNode
        {
            Id = Guid.NewGuid(), ExecutionPoolId = pool.Id, Name = "node", MachineName = "machine",
            OperatingSystem = "linux", Architecture = "x64", NodeVersion = "1.0.0",
            CertificateThumbprint = Guid.NewGuid().ToString("N"), CreatedAt = Now, UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var result = await service.DeletePoolAsync(pool.Id);

        Assert.Equal("pool_in_use", result.ErrorCode);
        Assert.True(await db.ExecutionPools.AnyAsync(x => x.Id == pool.Id));
    }

    private static async Task<ExecutionPoolAdministrationService> CreateServiceAsync(CSweetDbContext db)
    {
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        await new ExecutionFleetService(db, new TestAuditEventWriter(), clock,
            Options.Create(new ExecutionFleetOptions { PublicLaunchEnabled = true })).EnsureDefaultPoolAsync();
        return new ExecutionPoolAdministrationService(db, new TestAuditEventWriter(), clock);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
