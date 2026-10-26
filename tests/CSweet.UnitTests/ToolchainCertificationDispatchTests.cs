using System.Text.Json;
using CSweet.Api.Setup;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ToolchainCertificationDispatchTests
{
    [Fact]
    public async Task FourFixturesUseSeparateBuildRuntimesAndReplayDoesNotDuplicateDispatch()
    {
        await using var db = CreateDb();
        var (request, provider, definition, run) = await SeedAsync(db);
        var scheduler = new ExecutionWorkloadOrchestrator(db, TimeProvider.System);
        var images = new Images();
        for (var i = 0; i < 4; i++)
        {
            var build = Build(request, run, definition);
            db.DeliveryBuilds.Add(build); await db.SaveChangesAsync();
            await ToolchainCertificationEndpoints.QueueCertificationBuildAsync(request, build, definition, provider, scheduler, images, db, default);
            await ToolchainCertificationEndpoints.QueueCertificationBuildAsync(request, build, definition, provider, scheduler, images, db, default);
        }
        var assignments = await db.ExecutionWorkloadAssignments.ToArrayAsync();
        var runtimes = await db.AgentRuntimeInstances.ToArrayAsync();
        Assert.Equal(4, assignments.Length); Assert.Equal(4, runtimes.Length); Assert.Equal(4, images.Calls);
        Assert.Equal(4, runtimes.Select(x => x.DeliveryBuildId).Distinct().Count());
        foreach (var assignment in assignments)
        {
            Assert.Equal(assignment.DeliveryBuildId, runtimes.Single(x => x.Id == assignment.AgentRuntimeInstanceId).DeliveryBuildId);
            var workload = JsonSerializer.Deserialize<ToolchainBuildWorkloadSpecification>(assignment.SpecificationJson)!;
            Assert.Equal(assignment.DeliveryBuildId, workload.DeliveryBuildId);
            Assert.Equal(run.EnvironmentImageDigest, workload.GuestImage.Digest);
        }
        Assert.Empty(await db.ToolchainInstallationEligibilities.ToArrayAsync());
    }

    [Theory]
    [InlineData("valid")] [InlineData("resource")] [InlineData("image")] [InlineData("provider")]
    [InlineData("organization")] [InlineData("revoked")] [InlineData("repository")] [InlineData("definition")]
    public async Task PackagedFixtureRequiresExactCurrentCertificationAuthority(string scenario)
    {
        await using var db = CreateDb();
        var (request, provider, definition, run) = await SeedAsync(db);
        var build = Build(request, run, definition); db.DeliveryBuilds.Add(build); await db.SaveChangesAsync();
        await ToolchainCertificationEndpoints.QueueCertificationBuildAsync(request, build, definition, provider,
            new ExecutionWorkloadOrchestrator(db, TimeProvider.System), new Images(), db, default);
        var workload = JsonSerializer.Deserialize<ToolchainBuildWorkloadSpecification>((await db.ExecutionWorkloadAssignments.SingleAsync()).SpecificationJson)!;
        switch (scenario)
        {
            case "resource": build.CertificationFixtureResource = "fixtures/arbitrary"; break;
            case "image": run.EnvironmentImageDigest = "sha256:other"; break;
            case "provider": run.ProviderInstallationId = Guid.NewGuid(); break;
            case "organization": run.OrganizationId = Guid.NewGuid(); break;
            case "revoked": run.Status = W.ToolchainCertificationStatuses.Revoked; break;
            case "repository": build.RepositoryId = Guid.NewGuid(); break;
            case "definition": definition.DefinitionDigest = "changed"; break;
        }
        await db.SaveChangesAsync();
        if (scenario == "valid")
            Assert.True(await ExecutionBrokerSessionRunner.ValidateCertificationFixtureAsync(db, build, workload, default));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExecutionBrokerSessionRunner.ValidateCertificationFixtureAsync(db, build, workload, default));
        build.CertificationRunId = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => ExecutionBrokerSessionRunner.ValidateCertificationFixtureAsync(db, build, workload, default));
    }

    [Theory]
    [InlineData("stale")] [InlineData("revoked")] [InlineData("changed-artifact")] [InlineData("recover-dispatch")] [InlineData("executed-fixture")]
    public async Task ResumePreservesAuthorityAndNeverRetriesExecutedFixtureFailures(string scenario)
    {
        await using var db = CreateDb();
        var (request, provider, definition, run) = await SeedAsync(db);
        var build = Build(request, run, definition);
        build.Status = W.DeliveryBuildStatuses.Exhausted; build.FailureCode = "headquarters-broker-rejected";
        if (scenario == "executed-fixture") build.Attempt = 1;
        if (scenario == "revoked") run.Status = W.ToolchainCertificationStatuses.Revoked;
        if (scenario == "changed-artifact") provider.PackageVersion!.PackageDigest = new string('f', 64);
        db.DeliveryBuilds.Add(build); await db.SaveChangesAsync();
        var result = await ToolchainCertificationEndpoints.ResumeAsync(run.Id,
            new ResumeToolchainCertificationRequest(scenario == "stale" ? run.Revision + 1 : run.Revision), db,
            new ExecutionWorkloadOrchestrator(db, TimeProvider.System), new Images(), default);
        if (scenario is "stale" or "revoked" or "changed-artifact")
            Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(scenario == "recover-dispatch" ? 1 : 0, await db.ExecutionWorkloadAssignments.CountAsync());
        Assert.Equal(scenario == "recover-dispatch" ? W.DeliveryBuildStatuses.Queued : W.DeliveryBuildStatuses.Exhausted, build.Status);
        Assert.Empty(await db.ToolchainInstallationEligibilities.ToArrayAsync());
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<(StartToolchainCertificationRequest, AgentInstallation, ToolchainAdapterDefinitionRecord, ToolchainCertificationRunRecord)> SeedAsync(CSweetDbContext db)
    {
        var org = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.node", Version = "1.0.1", PluginKind = PluginKind.Service,
            Status = AgentPackageVersionStatus.Built, PackageDigest = new string('c',64), ArtifactSignature = "signature", ProjectPath = "Toolchain.csproj" };
        var provider = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = org.ToString(), PackageVersion = package, PackageVersionId = package.Id,
            ExecutionPoolId = Guid.NewGuid(), Scope = PluginInstallationScope.Organization };
        var definition = new ToolchainAdapterDefinitionRecord { Id = Guid.NewGuid(), ProviderPackageId = package.AgentId,
            ProviderPackageVersion = package.Version, DefinitionDigest = new string('d',64), DefinitionJson = """
            {"recipes":[{"key":"web","targetKeys":["browser"],"requiredEnvironmentProfileKeys":["office.linux-x64.node.v1"],
            "certificationFixtures":[{"key":"fixture","resource":"fixtures/web"}]}],"outputPolicy":{"maximumTotalBytes":1024}}
            """ };
        var run = new ToolchainCertificationRunRecord { Id = Guid.NewGuid(), OrganizationId = org, ProviderInstallationId = provider.Id,
            ToolchainDefinitionId = definition.Id, ProviderPackageDigest = "sha256:" + package.PackageDigest, DefinitionDigest = definition.DefinitionDigest,
            EnvironmentProfileKey = "office.linux-x64.node.v1", EnvironmentImageDigest = "sha256:" + new string('a',64),
            Status = W.ToolchainCertificationStatuses.Running, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        var node = new ExecutionNode { Id = Guid.NewGuid(), Name = "Office", Status = ExecutionNodeStatus.Ready, LastHeartbeatAt = DateTimeOffset.UtcNow };
        node.Providers.Add(new() { Id = Guid.NewGuid(), ExecutionNodeId = node.Id, IsAvailable = true, SupportsToolchainBuildWorkloads = true, GuestImageDigest = run.EnvironmentImageDigest });
        db.AddRange(provider, definition, run, node); await db.SaveChangesAsync();
        return (new(org, definition.Id, provider.Id, run.EnvironmentProfileKey, run.EnvironmentImageDigest), provider, definition, run);
    }
    private static DeliveryBuildRecord Build(StartToolchainCertificationRequest request, ToolchainCertificationRunRecord run, ToolchainAdapterDefinitionRecord definition) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = request.OrganizationId, WorkstreamId = Guid.NewGuid(), ToolchainDefinitionId = definition.Id,
        ProviderInstallationId = request.ProviderInstallationId, CertificationRunId = run.Id, RepositoryId = Guid.Empty,
        CertificationFixtureKey = "fixture", CertificationFixtureResource = "fixtures/web", SourceRevision = new string('b',40),
        RecipeKey = "web", TargetKey = "browser", DefinitionDigest = definition.DefinitionDigest, ConfigurationJson = "{}", Status = W.DeliveryBuildStatuses.Queued
    };
    private sealed class Images : IGuestImageRegistry
    {
        public int Calls;
        public Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new GuestImageReference(request.LogicalImageId, "1.0", request.ExpectedDigest!, request.OperatingSystem, request.Architecture)); }
    }
}
