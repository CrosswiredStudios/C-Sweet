using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("updated")]
    [InlineData("access-changed")]
    [InlineData("disabled")]
    [InlineData("network-approved")]
    [InlineData("network-changed")]
    public async Task CatalogHire_UsesApprovedDefinitionWithoutPreviewingMovingRepository(string change)
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var owner = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now
        };
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Company", CreatedAt = now, UpdatedAt = now });
        db.CoreOrganizationUsers.Add(owner);
        const string repositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager";
        var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = repositoryUrl };
        var manifest = new PluginManifest
        {
            Id = "com.csweet.product-manager", Name = "Product Manager", Version = "1.0.0",
            Provides = [new()
            {
                Name = "product-management.plan.v1",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
            }],
            Requires = [new() { Name = "tool.approved" }, new() { Name = "tool.unapproved" }],
            Configuration = [new() { Key = "responseTone", Label = "Tone", Type = "string" }]
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = source.Id, PackageSource = source,
            AgentId = manifest.Id, AgentName = manifest.Name, Version = manifest.Version,
            CommitSha = new string('c', 40), ManifestDigest = new string('d', 64),
            ManifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = AgentPackageVersionStatus.Built, PackageDigest = new string('e', 64), ArtifactSignature = "signature"
        };
        var definitions = new RecordingDefinitionService();
        definitions.SeedExisting(package.Id);
        var definition = new AgentDefinition
        {
            Id = definitions.DefinitionId, AgentId = package.AgentId,
            PackageSourceId = source.Id, PackageSource = source, PackageVersionId = package.Id, PackageVersion = package,
            IsAvailableForHire = true, Status = AgentDefinitionStatus.Available,
            DefaultRequiredCapabilitiesJson = "[\"tool.approved\"]",
            DefaultProvidedCapabilitiesJson = "[\"product-management.plan.v1\"]"
        };
        if (change.StartsWith("network-", StringComparison.Ordinal))
            definition.DefaultNetworkAccessJson = "[\"all-public\"]";
        db.AgentDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var agent = new AvailableAgent("first-party:product-manager", package.AgentId,
            AgentCatalogSource.FirstPartyCatalog, [], AgentAvailabilityState.AvailableToInstall, null,
            package.AgentName, "Product outcomes", "C-Sweet", "Product", ["Product Manager"], [],
            ["product-management.plan.v1"], null, null, null, 0, null, repositoryUrl + ".git/", 1m, "First party");
        var import = new RecordingImportPreview(db, repositoryUrl); // Would select another commit with the same version.
        var users = new RecordingOrganizationUserService();
        IAgentHireOrchestrator service = new HiringService(db, users, new TestAuditEventWriter(), import,
            agentCatalog: new RecordingAgentCatalog(agent), agentDefinitions: definitions);

        var preview = await service.PreviewAsync(organizationId, applicationUserId,
            new(agent.AgentReference, "Product Manager", "Avery", owner.Id, "approved-preview"));
        Assert.Empty(import.Requests);
        Assert.Equal(["tool.approved"], preview.RequestedCapabilities);
        Assert.Single(preview.ConfigurationFields);
        var workflow = await db.StaffingActionProposals.SingleAsync();
        using var snapshot = JsonDocument.Parse(workflow.PayloadJson);
        var embedded = snapshot.RootElement.GetProperty("embeddedAgent");
        Assert.Equal(package.Id, embedded.GetProperty("importId").GetGuid());
        Assert.Equal(package.CommitSha, embedded.GetProperty("commitSha").GetString());
        Assert.Equal(definition.Id, embedded.GetProperty("definitionId").GetGuid());

        if (change == "updated") definition.PackageVersionId = Guid.NewGuid();
        if (change == "access-changed") definition.DefaultRequiredCapabilitiesJson = "[]";
        if (change == "network-changed") definition.DefaultNetworkAccessJson = "[]";
        if (change == "disabled") definition.Status = AgentDefinitionStatus.Disabled;
        await db.SaveChangesAsync();
        if (change is "unchanged" or "network-approved")
        {
            var result = await service.ConfirmAsync(organizationId, preview.WorkflowId, applicationUserId, new("approved-confirm"));
            Assert.Equal(AgentHireOperationStatuses.Succeeded, result?.Status);
            Assert.Equal(definition.Id, users.CreatedRequest?.AgentDefinitionId);
            Assert.Equal(1, users.CreateCount);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(
                organizationId, preview.WorkflowId, applicationUserId, new("approved-confirm")));
            Assert.Contains(change is "access-changed" or "network-changed" ? "access changed" : "version changed", error.Message);
            Assert.Equal(0, users.CreateCount);
        }
        Assert.Equal(0, definitions.ImportCount);
        Assert.Empty(import.Requests);
        Assert.Single(await db.AgentPackageVersions.ToListAsync());
    }
}
