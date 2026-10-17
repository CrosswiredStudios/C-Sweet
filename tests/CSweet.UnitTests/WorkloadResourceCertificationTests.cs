using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkloadResourceCertificationTests
{
    [Theory]
    [InlineData("csweet-hardware-vm-smoke-v15", false)]
    [InlineData("csweet-hardware-vm-smoke-v16", true)]
    [InlineData("csweet-hardware-vm-smoke-v16-ipv4", true)]
    public async Task Coding_requires_memory_pressure_certification_while_ordinary_network_inventory_remains_compatible(string suite, bool codingAllowed)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var node = new ExecutionNode { Id = Guid.NewGuid(), Status = ExecutionNodeStatus.Ready, Architecture = "x64",
            ApprovedAt = now, LastHeartbeatAt = now, CertificateExpiresAt = now.AddHours(1) };
        var provider = new ExecutionNodeProvider { Id = Guid.NewGuid(), ExecutionNode = node, ExecutionNodeId = node.Id,
            IsAvailable = true, ProviderId = "hyperv-gen2", BrokerProtocolVersion = "1.1", SupportsPublicInternet = true,
            GuestImageDigest = "sha256:" + new string('a', 64), CertificationSuiteVersion = suite };
        db.Add(provider); await db.SaveChangesAsync();
        var registry = new FleetGuestImageRegistry(db, TimeProvider.System);
        var request = new GuestImageResolutionRequest("development", null, "linux", "x64", AgentTrustLevel.UntrustedRepository, "1.1",
            EnvironmentProfile: "software-development-polyglot-v1", RequirePublicInternet: true);
        if (codingAllowed) Assert.Equal(provider.GuestImageDigest, (await registry.ResolveAsync(request)).Digest);
        else await Assert.ThrowsAsync<IsolationUnavailableException>(() => registry.ResolveAsync(request));
        Assert.Equal(provider.GuestImageDigest, (await registry.ResolveAsync(request with { EnvironmentProfile = null, RequiredCertificationSuiteVersion = "csweet-hardware-vm-smoke-v14" })).Digest);
    }
}
