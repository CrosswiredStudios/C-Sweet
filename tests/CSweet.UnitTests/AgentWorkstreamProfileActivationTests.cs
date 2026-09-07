using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentWorkstreamProfileActivationTests
{
    [Fact]
    public async Task LaterPackageReleaseCanActivateTheSameImmutableProfile()
    {
        await using var db = CreateDb();
        var profile = Profile();
        db.Add(profile);
        await db.SaveChangesAsync();
        Assert.Equal(1, await AgentWorkstreamProfileActivation.ActivateAsync(db, Manifest(), default));
        Assert.Equal("Active", profile.Status);
        Assert.Equal("1.5.0", profile.ProviderPackageVersion);
        Assert.Equal(0, await AgentWorkstreamProfileActivation.ActivateAsync(db, Manifest(), default));
    }

    [Theory]
    [InlineData(true, true, "Previewed", "Active")]
    [InlineData(false, true, "Previewed", "Previewed")]
    [InlineData(true, false, "Previewed", "Previewed")]
    [InlineData(true, true, "Retired", "Retired")]
    public async Task ReconciliationRepairsOnlyApprovedInstalledProfiles(bool enabled, bool signed,
        string status, string expected)
    {
        await using var db = CreateDb();
        var profile = Profile();
        profile.Status = status;
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), AgentId = "com.example.director", Version = "1.6.2",
            ManifestJson = JsonSerializer.Serialize(Manifest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = AgentPackageVersionStatus.Built, PackageDigest = "sha256:verified",
            ArtifactSignature = signed ? "signature" : null
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), PackageVersionId = package.Id, PackageVersion = package,
            BusinessId = Guid.NewGuid().ToString(), IsEnabled = enabled, RevisionStatus = PluginRevisionStatus.Active,
            Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), ApprovedAt = DateTimeOffset.UtcNow }
        };
        db.AddRange(profile, installation);
        await db.SaveChangesAsync();
        await new AgentDefinitionInstallationSynchronizer(db, new TestAuditEventWriter()).SynchronizeAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(expected, (await db.WorkstreamProfileDefinitions.SingleAsync()).Status);
    }

    [Fact]
    public async Task AnotherProviderCannotActivateTheImportedProfile()
    {
        await using var db = CreateDb();
        db.Add(Profile());
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<CSweet.Application.Setup.AgentInstallationException>(() =>
            AgentWorkstreamProfileActivation.ActivateAsync(db, Manifest() with { Id = "com.other.agent" }, default));
        Assert.Equal("Previewed", (await db.WorkstreamProfileDefinitions.SingleAsync()).Status);
    }

    private static PluginManifest Manifest() => new()
    {
        Id = "com.example.director", Version = "1.6.2",
        WorkstreamProfiles = new() { Provides = [new() { Key = "game-production", Version = 4 }] }
    };
    private static WorkstreamProfileDefinitionRecord Profile() => new()
    {
        Id = Guid.NewGuid(), Key = "game-production", Version = 4, Status = "Previewed",
        ProviderPackageId = "com.example.director", ProviderPackageVersion = "1.5.0"
    };
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}