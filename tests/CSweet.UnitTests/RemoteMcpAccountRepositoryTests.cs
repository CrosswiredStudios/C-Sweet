using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.RemoteMcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;
public sealed class RemoteMcpAccountRepositoryTests
{
    [Fact] public async Task RelationalRevisionFenceRejectsStaleTrackedWriter()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(connection).Options;
        await using var first = new CSweetDbContext(options);
        await first.Database.ExecuteSqlRawAsync("""
            CREATE TABLE PluginOperationalStates (
                Id TEXT PRIMARY KEY, OrganizationId TEXT NOT NULL, AgentInstallationId TEXT NOT NULL,
                Kind TEXT NOT NULL, ExternalKey TEXT NOT NULL, PayloadJson TEXT NOT NULL, Revision INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, AvailableAt TEXT NULL);
            -- Provenance lookup has no enterprise consumer in this isolated repository fixture.
            CREATE TABLE AgentInstallations (Id TEXT PRIMARY KEY, BusinessId TEXT NOT NULL);
            """);
        var scope = new AccountScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var protection = new EphemeralDataProtectionProvider();
        var vault = new AccountVault(new RemoteMcpAccountRepository(first, scope), protection);
        var account = await vault.ConnectAsync(scope, "synthetic-relational-key-0001");
        await using var second = new CSweetDbContext(options);
        var stale = await second.PluginOperationalStates.SingleAsync();
        var revoked = await vault.RevokeAsync(scope, account);
        var writer = new RemoteMcpAccountRepository(second, scope);
        Assert.False(await writer.CompareExchangeAsync(account.Id, account.Generation,
            new(account.Id, account.Generation + 1, stale.PayloadJson), default));
        var info = await new AccountVault(writer, protection).InspectAsync(scope, account.Id);
        Assert.True(info.Revoked); Assert.Equal(revoked.Generation, info.Reference.Generation);
    }

    [Fact] public async Task DurableVaultSurvivesContextReplacementAndRejectsCrossScope()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var scope = new AccountScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var protection = new EphemeralDataProtectionProvider();
        AccountReference account;
        await using (var db = new CSweetDbContext(options))
        {
            account = await new AccountVault(new RemoteMcpAccountRepository(db, scope), protection).ConnectAsync(scope, "synthetic-account-key-0001");
            Assert.DoesNotContain("synthetic-account-key-0001", (await db.PluginOperationalStates.SingleAsync()).PayloadJson);
        }
        await using (var db = new CSweetDbContext(options))
        {
            var repository = new RemoteMcpAccountRepository(db, scope);
            Assert.NotNull(await repository.ReadAsync(account.Id, default));
            var granted = await new AccountVault(repository, protection).GrantAsync(scope, account, Guid.NewGuid());
            Assert.Equal(2, granted.Generation);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AccountVault(repository, protection).RevokeAsync(scope, account));
        }
        await using (var db = new CSweetDbContext(options))
        {
            Assert.Null(await new RemoteMcpAccountRepository(db, scope with { Owner = Guid.NewGuid() }).ReadAsync(account.Id, default));
            Assert.Null(await new RemoteMcpAccountRepository(db, scope with { Business = Guid.NewGuid() }).ReadAsync(account.Id, default));
        }
    }
    [Fact] public void ReviewedImportAcceptsOnlyTheVersionedClosedProfile()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "realtyleads-plugin.json"));
        var reader = new PluginManifestReader();
        var envelope = reader.Read(System.Text.Encoding.UTF8.GetBytes(json), "csweet-plugin.json");
        Assert.Equal("remote-mcp", envelope.Kind);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<CSweet.Contracts.Plugins.PluginManifest>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        AgentImportPreviewService.ValidateManifest(manifest);
        foreach (var invalid in new[] { json.Replace("https://realtyleads.ai/mcp", "https://evil.example/mcp"),
            json.Replace("\"minimumVersion\": \"2.5\"", "\"minimumVersion\": \"2.0\""),
            json.Replace("X-Api-Key", "Authorization"), json.Replace("\"kind\": \"remote-mcp\"", "\"kind\": \"connector\"") })
            Assert.Throws<System.Text.Json.JsonException>(() => reader.Read(System.Text.Encoding.UTF8.GetBytes(invalid), "csweet-plugin.json"));
    }
}
