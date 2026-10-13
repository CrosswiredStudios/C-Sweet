using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class RuntimeInternetGrantTests
{
    [Theory]
    [InlineData("csweet-hardware-vm-smoke-v15", true, true)]
    [InlineData("csweet-hardware-vm-smoke-v15-ipv4", true, false)]
    [InlineData("csweet-hardware-vm-smoke-v15", false, false)]
    public void OfficeAdministrationPreservesCertifiedInternetInventory(string suite, bool supported, bool ipv6)
    {
        var node = new ExecutionNode { Id = Guid.NewGuid() };
        node.Providers.Add(new ExecutionNodeProvider { SupportsPublicInternet = supported,
            CertificationSuiteVersion = suite });

        var provider = Assert.Single(CSweet.Api.Setup.ExecutionFleetEndpoints.Map(node).Providers);

        Assert.Equal(supported, provider.SupportsPublicInternet);
        Assert.Equal(ipv6, provider.SupportsPublicInternetIpv6);
    }

    [Theory]
    [InlineData("public-internet-ipv4", "csweet-hardware-vm-smoke-v15-ipv4", true)]
    [InlineData("public-internet", "csweet-hardware-vm-smoke-v15-ipv4", false)]
    [InlineData("public-internet", "csweet-hardware-vm-smoke-v15", true)]
    [InlineData("public-internet-ipv4", "csweet-hardware-vm-smoke-v15", true)]
    [InlineData("public-internet-ipv4", "unknown", false)]
    public void PlacementNeverUpgradesIpv4EvidenceIntoIpv6Authority(string mode, string suite, bool allowed)
    {
        var provider = new ExecutionNodeProvider { BrokerProtocolVersion = "1.1", SupportsPublicInternet = true,
            CertificationSuiteVersion = suite };
        var specification = JsonSerializer.Serialize(new { NetworkPolicy = new RuntimeNetworkPolicy(mode, 1) });
        Assert.Equal(allowed, ExecutionWorkloadOrchestrator.NetworkPlacementAllows(provider, specification));
        provider.SupportsPublicInternet = false;
        Assert.False(ExecutionWorkloadOrchestrator.NetworkPlacementAllows(provider, specification));
    }

    [Fact]
    public void PermissionRequiresBothManifestAndGrantAndNeverInheritsWebAccess()
    {
        const string manifest = """{"requires":[{"name":"runtime.internet.public.v1","modelVisible":false}]}""";
        var grant = new AgentInstallationGrant { RequiredCapabilitiesJson = "[]", NetworkAccessJson = "[\"all-public\"]" };
        Assert.False(RuntimeInternetGrant.IsGranted(manifest, grant));
        grant.RequiredCapabilitiesJson = "[\"runtime.internet.public.v1\"]";
        Assert.True(RuntimeInternetGrant.IsGranted(manifest, grant));
        Assert.False(RuntimeInternetGrant.IsGranted("{}", grant));
        Assert.False(RuntimeInternetGrant.IsGranted(manifest, null));
        Assert.True(RuntimePermissions.IsExecutionPermission(RuntimePermissions.PublicInternet));
        Assert.False(RuntimePermissions.IsExecutionPermission("platform.web.fetch.v1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineApprovalAndRevocationPersistAuditAndWakeRecordsWithoutReplayDuplicates(bool approved)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), BusinessId = Guid.NewGuid().ToString(),
            PackageVersion = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "developer",
                ManifestJson = """{"requires":[{"name":"runtime.internet.public.v1","modelVisible":false}]}""" },
            Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), GrantRevision = 1,
                RequiredCapabilitiesJson = approved ? "[\"runtime.internet.public.v1\"]" : "[]" }
        };
        installation.PackageVersionId = installation.PackageVersion.Id;
        installation.Grant.AgentInstallationId = installation.Id;
        db.AgentInstallations.Add(installation);
        await db.SaveChangesAsync();
        Assert.Contains(await db.AuditOutbox.ToListAsync(), x => x.RequestJson.Contains("runtime.internet.grant-changed"));
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.Subject == $"installations/{installation.Id:D}");
        var audits = await db.AuditOutbox.CountAsync();
        var wakes = await db.ApplicationRealtimeOutbox.CountAsync();
        installation.Grant.RequiredCapabilitiesJson = "[]";
        installation.Grant.GrantRevision++;
        await db.SaveChangesAsync();
        Assert.Equal(audits + 1, await db.AuditOutbox.CountAsync());
        Assert.Equal(wakes + 1, await db.ApplicationRealtimeOutbox.CountAsync());
        await db.SaveChangesAsync();
        Assert.Equal(audits + 1, await db.AuditOutbox.CountAsync());
        Assert.Equal(wakes + 1, await db.ApplicationRealtimeOutbox.CountAsync());
    }

    [Fact]
    public async Task GrantRevisionIsRecheckedAndRevocationFencesTheCurrentAssignmentAtomically()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), BusinessId = Guid.NewGuid().ToString(),
            PackageVersion = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "developer",
                ManifestJson = """{"requires":[{"name":"runtime.internet.public.v1","modelVisible":false}]}""" },
            Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), GrantRevision = 1,
                RequiredCapabilitiesJson = "[\"runtime.internet.public.v1\"]" }
        };
        installation.PackageVersionId = installation.PackageVersion.Id;
        installation.Grant.AgentInstallationId = installation.Id;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallation = installation,
            AgentInstallationId = installation.Id, TickId = Guid.NewGuid() };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
        var digest = "sha256:" + new string('a', 64);
        var spec = new RuntimeWorkloadSpecification(runtime.Id,
            new("dev", "1", digest, "linux", "x64"), new(1, 100, 512, 512, 128, 1024, TimeSpan.FromMinutes(1)),
            new(Guid.NewGuid(), "1.1", "not-a-production-token", digest, digest, DateTimeOffset.UtcNow.AddMinutes(1)),
            new(digest, "signature", "1", "linux", "x64"),
            new(installation.Id, installation.BusinessId, runtime.TickId), ["agent"])
        { NetworkPolicy = new(RuntimeNetworkPolicy.PublicInternet, 1) };
        var assignment = new ExecutionWorkloadAssignment { Id = Guid.NewGuid(), WorkloadKind = ExecutionWorkloadKind.Runtime,
            AgentRuntimeInstance = runtime, AgentRuntimeInstanceId = runtime.Id, Status = ExecutionAssignmentStatus.Running,
            FencingEpoch = 3, SpecificationJson = JsonSerializer.Serialize(spec), LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        db.AgentInstallations.Add(installation); db.ExecutionWorkloadAssignments.Add(assignment);
        await db.SaveChangesAsync();
        Assert.True(await RuntimeInternetGrant.IsCurrentAsync(db, assignment.SpecificationJson, default));
        Assert.False(await RuntimeInternetGrant.IsCurrentAsync(db,
            JsonSerializer.Serialize(spec with { NetworkPolicy = new(RuntimeNetworkPolicy.PublicInternet, 2) }), default));
        Assert.False(await RuntimeInternetGrant.IsCurrentAsync(db,
            JsonSerializer.Serialize(spec with { Identity = spec.Identity with { BusinessId = Guid.NewGuid().ToString() } }), default));
        installation.Grant.RequiredCapabilitiesJson = "[]"; installation.Grant.GrantRevision++;
        await db.SaveChangesAsync();
        Assert.Equal(ExecutionAssignmentStatus.Fenced, assignment.Status);
        Assert.Equal(4, assignment.FencingEpoch);
        Assert.Null(assignment.LeaseExpiresAt);
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        Assert.False(await RuntimeInternetGrant.IsCurrentAsync(db, assignment.SpecificationJson, default));
        var auditCount = await db.AuditOutbox.CountAsync();
        await db.SaveChangesAsync();
        Assert.Equal(4, assignment.FencingEpoch);
        Assert.Equal(auditCount, await db.AuditOutbox.CountAsync());
    }
}
