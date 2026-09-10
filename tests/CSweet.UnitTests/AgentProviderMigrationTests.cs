using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentProviderMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_PreservesOtherSettings_AndHonorsInstanceOption(bool includeInstances)
    {
        await using var db = CreateDb();
        var definition = Seed(db);
        var other = Seed(db);
        var provider = new LlmProviderProfile { Id = Guid.NewGuid(), IsEnabled = true, DefaultChatModel = "new-model" };
        db.Add(provider);
        await db.SaveChangesAsync();
        var service = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());

        var candidates = await service.ListMigrationCandidatesAsync();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, x => Assert.Equal(2, x.InstanceCount));
        var result = await service.MigrateProvidersAsync(new(provider.Id, "new-model",
            [new(definition.Id, 1)], includeInstances));

        Assert.Equal(1, result.AgentCount);
        Assert.Equal(includeInstances ? 2 : 0, result.InstanceCount);
        var defaults = await service.GetDefinitionAsync(definition.Id);
        Assert.Equal(provider.Id.ToString("D"), defaults.DefaultValues["provider"].GetString());
        Assert.Equal("new-model", defaults.DefaultValues["model"].GetString());
        Assert.Equal("brief", defaults.DefaultValues["tone"].GetString());
        var instances = definition.Installations.ToArray();
        for (var index = 0; index < instances.Length; index++)
        {
            var effective = await service.ResolveInstallationAsync(instances[index].Id);
            Assert.Equal(includeInstances ? "new-model" : index == 0 ? "old-model" : "custom-model",
                effective.Settings["model"].GetString());
            Assert.Equal("custom-tone", effective.Settings["tone"].GetString());
            Assert.Equal(includeInstances ? 1 : 0, instances[index].DesiredConfigurationRevision);
        }
        Assert.Equal(1, other.Configuration!.Revision);
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("unsupported-model")]
    public async Task InvalidBatch_DoesNotModifyAnyConfiguration(string failure)
    {
        await using var db = CreateDb();
        var first = Seed(db);
        var second = Seed(db);
        var provider = new LlmProviderProfile { Id = Guid.NewGuid(), IsEnabled = failure != "disabled", DefaultChatModel = "new-model" };
        db.Add(provider);
        await db.SaveChangesAsync();
        var service = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());
        var request = new MigrateAgentProvidersRequest(provider.Id,
            failure == "unsupported-model" ? "unknown" : "new-model",
            [new(first.Id, 1), new(failure == "missing" ? Guid.NewGuid() : second.Id, failure == "stale" ? 0 : 1)], true);
        if (failure == "stale")
            await Assert.ThrowsAsync<AgentConfigurationConflictException>(() => service.MigrateProvidersAsync(request));
        else
            await Assert.ThrowsAsync<AgentInstallationException>(() => service.MigrateProvidersAsync(request));
        Assert.Equal(1, first.Configuration!.Revision);
        Assert.Equal(1, second.Configuration!.Revision);
        Assert.All(first.Installations, x => Assert.Equal(0, x.DesiredConfigurationRevision));
        db.ChangeTracker.Clear();
        Assert.All(await db.AgentDefinitionConfigurations.ToListAsync(), x => Assert.Equal(1, x.Revision));
    }

    [Fact]
    public async Task Migration_UpdatesEverySelectedDefinitionAndBusiness()
    {
        await using var db = CreateDb();
        var first = Seed(db);
        var second = Seed(db);
        var provider = new LlmProviderProfile { Id = Guid.NewGuid(), IsEnabled = true, DefaultChatModel = "new-model" };
        db.Add(provider);
        await db.SaveChangesAsync();
        var service = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());
        var result = await service.MigrateProvidersAsync(new(provider.Id, "new-model",
            [new(first.Id, 1), new(second.Id, 1)], true));
        Assert.Equal(new MigrateAgentProvidersResponse(2, 4), result);
        foreach (var installation in first.Installations.Concat(second.Installations))
        {
            var effective = await service.ResolveInstallationAsync(installation.Id);
            Assert.Equal(provider.Id.ToString("D"), effective.Settings["provider"].GetString());
            Assert.Equal("new-model", effective.Settings["model"].GetString());
        }
    }
    [Fact]
    public async Task Candidates_ExcludeAgentsWithoutBothFieldTypes()
    {
        await using var db = CreateDb();
        var definition = Seed(db);
        definition.PackageVersion!.ManifestJson = """{"configuration":[{"key":"provider","type":"text","label":"Provider"}]}""";
        await db.SaveChangesAsync();
        Assert.Empty(await new AgentInstallationConfigurationService(db, new TestAuditEventWriter()).ListMigrationCandidatesAsync());
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AgentDefinition Seed(CSweetDbContext db)
    {
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), AgentId = Guid.NewGuid().ToString(),
            PackageVersion = new AgentPackageVersion
            {
                Id = Guid.NewGuid(), Version = "1.0",
                ManifestJson = """
                {"configuration":[
                  {"key":"provider","type":"llmProvider","label":"Provider"},
                  {"key":"model","type":"llmModel","label":"Model","dependsOnFieldKey":"provider"},
                  {"key":"tone","type":"text","label":"Tone"}]}
                """
            },
            Configuration = new AgentDefinitionConfiguration
            {
                Id = Guid.NewGuid(), SchemaVersion = "1", Revision = 1,
                SettingsJson = """{"provider":"old-provider","model":"old-model","tone":"brief"}"""
            }
        };
        for (var i = 0; i < 2; i++)
            definition.Installations.Add(new AgentInstallation
            {
                Id = Guid.NewGuid(), BusinessId = Guid.NewGuid().ToString(),
                Configuration = new AgentInstallationConfiguration
                {
                    Id = Guid.NewGuid(), SchemaVersion = "1",
                    SettingsJson = i == 0 ? """{"tone":"custom-tone"}""" : """{"model":"custom-model","tone":"custom-tone"}"""
                }
            });
        db.Add(definition);
        return definition;
    }
}
