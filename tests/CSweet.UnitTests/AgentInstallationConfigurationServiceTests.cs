using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentInstallationConfigurationServiceTests
{
    [Fact]
    public async Task SaveAsync_CreatesThenUpdatesOneConfigurationPerInstallation()
    {
        await using var dbContext = CreateDbContext();
        var installation = await SeedInstallationAsync(dbContext, "business-1");
        var service = new AgentInstallationConfigurationService(dbContext, new TestAuditEventWriter());

        var created = await service.SaveAsync(
            installation.Id,
            "1.0",
            new Dictionary<string, JsonElement>
            {
                ["responseTone"] = JsonSerializer.SerializeToElement("balanced")
            });
        var updated = await service.SaveAsync(
            installation.Id,
            "1.1",
            new Dictionary<string, JsonElement>
            {
                ["responseTone"] = JsonSerializer.SerializeToElement("concise")
            });

        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal("1.0", updated.SchemaVersion);
        Assert.Equal("concise", updated.Settings["responseTone"].GetString());
        Assert.Single(await dbContext.AgentInstallationConfigurations.ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_KeepsBusinessInstallationsIsolated()
    {
        await using var dbContext = CreateDbContext();
        var first = await SeedInstallationAsync(dbContext, "business-1");
        var second = await SeedInstallationAsync(dbContext, "business-2");
        var service = new AgentInstallationConfigurationService(dbContext, new TestAuditEventWriter());

        await service.SaveAsync(first.Id, "1.0", Settings("provider-a"));
        await service.SaveAsync(second.Id, "1.0", Settings("provider-b"));

        Assert.Equal("provider-a", (await service.GetAsync(first.Id))!.Settings["llmProviderId"].GetString());
        Assert.Equal("provider-b", (await service.GetAsync(second.Id))!.Settings["llmProviderId"].GetString());
    }

    [Fact]
    public async Task DefinitionDefaults_PropagateOnlyToNonOverriddenFields_WithoutStartingStoppedAgent()
    {
        await using var dbContext = CreateDbContext();
        var installation = await SeedInstallationAsync(dbContext, "business-defaults");
        var definition = await dbContext.AgentDefinitions.Include(x => x.Configuration).SingleAsync();
        var employee = await dbContext.CoreOrganizationUsers.SingleAsync();
        definition.Configuration!.SettingsJson = JsonSerializer.Serialize(new
        {
            responseTone = "balanced",
            llmProviderId = "provider-a"
        });
        installation.Configuration!.SettingsJson = JsonSerializer.Serialize(new { responseTone = "custom" });
        await dbContext.SaveChangesAsync();
        var service = new AgentInstallationConfigurationService(dbContext, new TestAuditEventWriter());

        await service.SaveDefinitionAsync(definition.Id, new PutAgentDefinitionConfigurationRequest(
            "1.0",
            new Dictionary<string, JsonElement>
            {
                ["responseTone"] = JsonSerializer.SerializeToElement("detailed"),
                ["llmProviderId"] = JsonSerializer.SerializeToElement("provider-b")
            },
            ExpectedRevision: 1));
        var view = await service.GetEmployeeAsync(employee.OrganizationId, employee.Id);

        Assert.Equal("custom", view.EffectiveValues["responseTone"].GetString());
        Assert.Equal("provider-b", view.EffectiveValues["llmProviderId"].GetString());
        Assert.Equal(["responseTone"], view.OverriddenKeys);
        Assert.Equal(AgentConfigurationSyncStatus.PendingNextStart.ToString(), view.SynchronizationStatus);
        Assert.Empty(await dbContext.AgentRuntimeInstances.ToListAsync());
        await Assert.ThrowsAsync<AgentConfigurationConflictException>(() =>
            service.SaveDefinitionAsync(definition.Id, new PutAgentDefinitionConfigurationRequest(
                "1.0", view.DefaultValues, ExpectedRevision: 1)));

        var restored = await service.RestoreEmployeeOverrideAsync(
            employee.OrganizationId, employee.Id, "responseTone", view.ExpectedRevision);
        Assert.Equal("detailed", restored.EffectiveValues["responseTone"].GetString());
        Assert.Empty(restored.OverriddenKeys);
        Assert.Empty(await dbContext.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task SaveDefinitionAsync_ReviewingUnchangedValidDefaults_MakesBuiltSignedDefinitionHireable()
    {
        await using var dbContext = CreateDbContext();
        await SeedInstallationAsync(dbContext, "business-review");
        var definition = await dbContext.AgentDefinitions
            .Include(x => x.Configuration)
            .Include(x => x.PackageVersion)
            .SingleAsync();
        definition.Status = AgentDefinitionStatus.NeedsConfiguration;
        definition.IsAvailableForHire = false;
        definition.PackageVersion!.Status = AgentPackageVersionStatus.Built;
        definition.PackageVersion.PackageDigest = new string('c', 64);
        definition.PackageVersion.ArtifactSignature = "test-signature";
        await dbContext.SaveChangesAsync();
        var service = new AgentInstallationConfigurationService(dbContext, new TestAuditEventWriter());

        var view = await service.SaveDefinitionAsync(definition.Id, new PutAgentDefinitionConfigurationRequest(
            "1.0", new Dictionary<string, JsonElement>(), ExpectedRevision: 1));

        Assert.True(definition.IsAvailableForHire);
        Assert.Equal(AgentDefinitionStatus.Available, definition.Status);
        Assert.Equal(1, view.ExpectedRevision);
        Assert.Empty(await dbContext.AgentRuntimeInstances.ToListAsync());
    }

    private static IReadOnlyDictionary<string, JsonElement> Settings(string providerId) =>
        new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId)
        };

    private static async Task<AgentInstallation> SeedInstallationAsync(
        CSweetDbContext dbContext,
        string businessLabel)
    {
        var organizationId = Guid.NewGuid();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            CommitSha = new string('a', 40),
            ManifestDigest = new string('b', 64),
            ManifestJson = JsonSerializer.Serialize(new
            {
                configuration = new[]
                {
                    new { key = "responseTone", type = "text", label = "Response tone", required = false, secret = false },
                    new { key = "llmProviderId", type = "text", label = "Provider", required = false, secret = false }
                }
            }),
            AgentId = "com.example.agent",
            AgentName = "Example Agent",
            Version = "1.0.0",
            PublisherId = "example",
            PublisherName = "Example",
            RuntimeType = "dotnet-project",
            WarningsJson = "[]",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            PackageVersion = package,
            BusinessId = organizationId.ToString("D"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            PackageSourceId = package.PackageSourceId,
            AgentId = package.AgentId,
            PackageVersionId = package.Id,
            PackageVersion = package,
            Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = definition.Id,
            SchemaVersion = "1.0",
            SettingsJson = "{}",
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.AgentDefinitionId = definition.Id;
        installation.AgentDefinition = definition;
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            SchemaVersion = "1.0",
            SettingsJson = "{}",
            Revision = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var organization = new Organization
        {
            Id = organizationId,
            Name = businessLabel,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var employee = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            DisplayName = businessLabel,
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            AgentInstallationId = installation.Id,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.AddRange(organization, definition, installation, employee);
        await dbContext.SaveChangesAsync();
        return installation;
    }

    private static CSweetDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
