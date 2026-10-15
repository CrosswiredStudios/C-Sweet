using System.Text.Json.Nodes;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class AgentDefinitionLifecycleTests
{
    [Fact]
    public async Task RuntimeEligibility_checks_development_profile_before_queue_and_recovers_from_current_inventory()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, false);
        package.PackageDigest = "sha256:" + new string('a', 64);
        package.ArtifactSignature = "signed";
        package.ArtifactArchitecture = "x64";
        var manifest = JsonNode.Parse(package.ManifestJson)!.AsObject();
        manifest["runtime"]!["environmentProfile"] = "software-development-polyglot-v1";
        manifest["runtime"]!["workspaceAccess"] = "ReadWrite";
        package.ManifestJson = manifest.ToJsonString();
        var definition = SeedDefinition(db, package, ActivationMode.OnDemand);
        var organization = Guid.NewGuid();
        var installation = RuntimeInstallation(package, organization.ToString());
        installation.AgentDefinitionId = definition.Id;
        db.AddRange(installation, new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization, DisplayName = "Engineer",
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id,
            CreatedAt = DateTimeOffset.UtcNow, IsActive = true
        });
        await db.SaveChangesAsync();
        var registry = new EnvironmentRegistry();
        var eligibility = new AgentRuntimeEligibilityService(db,
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), guestImages: registry,
            runtimeOptions: Options.Create(new AgentRuntimeManagerOptions()));
        for (var tick = 0; tick < 4; tick++)
        {
            var blocked = await eligibility.EvaluateAsync(installation.Id);
            Assert.False(blocked.IsEligible);
            Assert.Contains("software-development-polyglot-v1", blocked.Reason);
            Assert.Contains("Repair the connected Office", blocked.Reason);
        }
        Assert.Empty(db.AgentRuntimeInstances);
        registry.Compatible = true;
        Assert.True((await eligibility.EvaluateAsync(installation.Id)).IsEligible);
        Assert.Equal("1.1", registry.LastRequest!.BrokerProtocolVersion);
        Assert.Equal("x64", registry.LastRequest.Architecture);
    }

    [Fact]
    public async Task Configured_development_digest_cannot_fall_back_to_an_unpinned_image()
    {
        var package = new AgentPackageVersion { ArtifactArchitecture = "x64",
            ManifestJson = "{\"runtime\":{\"environmentProfile\":\"software-development-polyglot-v1\",\"workspaceAccess\":\"ReadWrite\"}}" };
        var registry = new EnvironmentRegistry();
        var settings = new AgentRuntimeManagerOptions { DevelopmentImages = [new()
            { ProviderId = "hyperv-gen2", Architecture = "x64", Digest = "sha256:" + new string('c', 64) }] };
        await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            AgentRuntimeGuestSelection.ResolveAsync(package, null, settings, registry, default));
        Assert.Equal(settings.DevelopmentImages[0].Digest, registry.LastRequest!.ExpectedDigest);
        Assert.Equal("hyperv-gen2", registry.LastRequest.PreferredProviderId);
        Assert.Equal(1, registry.Calls);
    }

    private sealed class EnvironmentRegistry : IGuestImageRegistry
    {
        public bool Compatible { get; set; }
        public int Calls { get; private set; }
        public GuestImageResolutionRequest? LastRequest { get; private set; }
        public Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; LastRequest = request;
            Assert.Equal("software-development-polyglot-v1", request.EnvironmentProfile);
            Assert.Equal("1.1", request.BrokerProtocolVersion);
            if (!Compatible) throw new IsolationUnavailableException("No connected execution node advertises the required certified guest image variant.");
            return Task.FromResult(new GuestImageReference(request.LogicalImageId, "v15", "sha256:" + new string('d', 64), "linux", "x64"));
        }
    }
}
