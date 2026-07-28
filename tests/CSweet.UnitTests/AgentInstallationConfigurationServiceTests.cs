using System.Text.Json;
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
        Assert.Equal("1.1", updated.SchemaVersion);
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

    private static IReadOnlyDictionary<string, JsonElement> Settings(string providerId) =>
        new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId)
        };

    private static async Task<AgentInstallation> SeedInstallationAsync(
        CSweetDbContext dbContext,
        string businessId)
    {
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
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.AgentInstallations.Add(installation);
        await dbContext.SaveChangesAsync();
        return installation;
    }

    private static CSweetDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
