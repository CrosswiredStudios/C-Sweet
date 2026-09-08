using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class DeliveryBuildRecoveryTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("unknown-property")]
    [InlineData("wrong-enum")]
    public async Task RetryResumesPersistedBuildAfterImageServiceFailureWithoutDuplicatingBuildOrEvent(string configuration)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var caller = Guid.NewGuid();
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, AccountableManagerOrganizationUserId = actor };
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.toolchain", Version = "1.0.0",
            PackageDigest = new string('c',64), ArtifactSignature = "test-signature", ProjectPath = "Toolchain.csproj" };
        var provider = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = org.ToString("D"), PackageVersion = package,
            PackageVersionId = package.Id, IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active,
            SetupState = PluginSetupState.Ready, ExecutionPoolId = Guid.NewGuid() };
        var definition = new ToolchainAdapterDefinitionRecord { Id = Guid.NewGuid(), ProviderPackageId = package.AgentId,
            ProviderPackageVersion = package.Version, DefinitionDigest = new string('d',64), DefinitionJson = """
            {"recipes":[{"key":"web","operations":["build"],"targetKeys":["browser"],"configurationSchema":{"type":"object","required":["quality"],"additionalProperties":false,"properties":{"quality":{"type":"string","enum":["release"]}}},
            "requiredEnvironmentProfileKeys":["linux-x64"],"certificationFixtures":[]}],"requiredExecutableVersions":{},
            "outputPolicy":{"maximumTotalBytes":1024},"supportedContentTypes":["text/html"],"previewModes":["web-static"]}
            """ };
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = org,
            Status = SourceControlRepositoryStatus.Ready, CloneUrl = "https://example.test/game.git",
            Connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = org, Status = SourceControlConnectionStatus.Connected } };
        db.AddRange(stream, provider, definition, repository,
            new ToolchainInstallationEligibilityRecord { Id = Guid.NewGuid(), OrganizationId = org,
                ToolchainDefinitionId = definition.Id, ProviderInstallationId = provider.Id, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                EnvironmentProfileKey = "linux-x64", EnvironmentImageDigest = "sha256:" + new string('a',64) });
        await db.SaveChangesAsync();
        var images = new Images();
        var handler = new DeliveryEvidenceCapabilityHandler(db, TimeProvider.System,
            new ExecutionWorkloadOrchestrator(db, TimeProvider.System), images, null);
        var request = new RequestBuildV2Request(stream.Id, null, definition.Id, provider.Id, repository.Id,
            new string('b',40), "web", "browser", JsonSerializer.SerializeToElement(new { quality = "release" }), 3, "build-once");
        if (configuration != "valid")
        {
            request = request with { Configuration = JsonSerializer.Deserialize<JsonElement>(configuration switch
            {
                "missing" => "{}", "wrong-type" => "{\"quality\":42}",
                "unknown-property" => "{\"quality\":\"release\",\"arbitrary\":true}",
                _ => "{\"quality\":\"debug\"}"
            }) };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.RequestBuildAsync(org, caller, actor, request, default));
            Assert.Contains("JSON Schema validation failed", error.Message);
            Assert.Empty(await db.DeliveryBuilds.ToListAsync());
            Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());
            Assert.Empty(await db.ExecutionWorkloadAssignments.ToListAsync());
            Assert.Equal(0, images.Calls);
            return;
        }
        await Assert.ThrowsAsync<IOException>(() => handler.RequestBuildAsync(org, caller, actor, request, default));
        db.ChangeTracker.Clear();
        var saved = Assert.Single(await db.DeliveryBuilds.ToListAsync());
        Assert.Empty(await db.ExecutionWorkloadAssignments.ToListAsync());
        var resumed = await handler.RequestBuildAsync(org, caller, actor, request, default);
        Assert.Equal(saved.Id, resumed.Id);
        db.ChangeTracker.Clear();
        var assignment = Assert.Single(await db.ExecutionWorkloadAssignments.ToListAsync());
        Assert.Equal(saved.Id, assignment.DeliveryBuildId);
        var workload = JsonSerializer.Deserialize<ToolchainBuildWorkloadSpecification>(assignment.SpecificationJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(saved.Id, workload.DeliveryBuildId);
        Assert.Contains(request.SourceRevision, assignment.SpecificationJson);
        await handler.RequestBuildAsync(org, caller, actor, request, default);
        Assert.Equal(2, images.Calls);
        Assert.Single(await db.DeliveryBuilds.ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.ToListAsync());
        Assert.Single(await db.ExecutionWorkloadAssignments.ToListAsync());
        foreach (var changed in new[] { request with { SourceRevision = new string('e',40) },
            request with { Configuration = JsonSerializer.SerializeToElement(new { quality = "debug" }) },
            request with { RepositoryId = Guid.NewGuid() }, request with { MaximumAttempts = 4 },
            request with { ProviderInstallationId = Guid.NewGuid() }, request with { TargetKey = "desktop" } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.RequestBuildAsync(org, caller, actor, changed, default));
    }

    private sealed class Images : IGuestImageRegistry
    {
        public int Calls { get; private set; }
        public Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default)
        {
            if (++Calls == 1) throw new IOException("Temporary image registry outage");
            return Task.FromResult(new GuestImageReference(request.LogicalImageId, "1.0", request.ExpectedDigest!, request.OperatingSystem, request.Architecture));
        }
    }
}
