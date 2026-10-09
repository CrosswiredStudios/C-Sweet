using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team", "human")] [InlineData("Role", "human")]
    [InlineData("Team", "definition")] [InlineData("Role", "definition")]
    public async Task MemoryAuthorityGenerationFencesRestoredRecipientAndDefinition(string kind, string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context();
        AssertScopedRouteRecords(await ReadHandler(fixture, db).HandleAsync(session,
            ScopedRouteRequest(source.Episode, records, "episode"), default), source.Episode, records, "episode");
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        if (change == "human" && kind == "Team")
        {
            await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAt, DateTimeOffset.UtcNow));
            await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAt, (DateTimeOffset?)null));
        }
        else if (change == "human")
        {
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.RoleId, (Guid?)null));
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.RoleId, source.Audience));
        }
        else if (kind == "Team")
        {
            await db.OrganizationTeams.Where(x => x.Id == source.Audience).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
            await db.OrganizationTeams.Where(x => x.Id == source.Audience).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, (DateTimeOffset?)null));
        }
        else
        {
            var original = await db.CoreRoles.Where(x => x.Id == source.Audience).Select(x => x.AuthorityLevel).SingleAsync();
            await db.CoreRoles.Where(x => x.Id == source.Audience).ExecuteUpdateAsync(x => x.SetProperty(p => p.AuthorityLevel, (AuthorityLevel)((int)original + 1)));
            await db.CoreRoles.Where(x => x.Id == source.Audience).ExecuteUpdateAsync(x => x.SetProperty(p => p.AuthorityLevel, original));
        }
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task MemoryAuthorityGenerationFencesRestoredGrantAndDeletedMembership()
    {
        foreach (var change in new[] { "grant", "membership" })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var source = await SeedReadAudienceAsync(fixture, "Team"); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
            var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
            await using var db = fixture.Context();
            AssertScopedRouteRecords(await ReadHandler(fixture, db).HandleAsync(session,
                ScopedRouteRequest(source.Episode, records, "episode"), default), source.Episode, records, "episode");
            await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
            if (change == "grant")
            {
                var required = await db.AgentInstallationGrants.Select(x => x.RequiredCapabilitiesJson).SingleAsync();
                await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(p => p.RequiredCapabilitiesJson, "[]"));
                await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(p => p.RequiredCapabilitiesJson, required));
            }
            else
            {
                var membership = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.OrganizationUserId == fixture.EmployeeId);
                await db.TeamMemberships.Where(x => x.Id == membership.Id).ExecuteDeleteAsync();
                db.TeamMemberships.Add(membership); await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
            Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        }
    }

    [MemoryPostgresFact]
    public async Task MemoryAuthorityGenerationKeepsOrdinaryProgressAndRolledBackRevocationValid()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, "Team"); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context();
        AssertScopedRouteRecords(await ReadHandler(fixture, db).HandleAsync(session,
            ScopedRouteRequest(source.Episode, records, "episode"), default), source.Episode, records, "episode");
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.DisplayName, "Updated name"));
        await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(p => p.ConfigurationSyncLastAttemptAt, DateTimeOffset.UtcNow));
        await db.OrganizationTeams.ExecuteUpdateAsync(x => x.SetProperty(p => p.Description, "Updated description"));
        await db.TeamMemberships.ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceType, "updated provenance label"));
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(p => p.Title, "Updated title"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled, false));
            await transaction.RollbackAsync();
        }
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("UPDATE \"MemoryAccessAuthority\" SET \"Revision\"=1")]
    [InlineData("DELETE FROM \"MemoryAccessAuthority\"")]
    [InlineData("TRUNCATE \"MemoryAccessAuthority\"")]
    public async Task MemoryAuthorityGenerationRejectsDirectHistoryTampering(string sql)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled, false));
        var revision = await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryAccessAuthority\" WHERE \"Kind\"='installation' AND \"Id\"={fixture.InstallationId}").SingleAsync();
        Assert.Equal(2, revision);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        Assert.Equal(revision, await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryAccessAuthority\" WHERE \"Kind\"='installation' AND \"Id\"={fixture.InstallationId}").SingleAsync());
    }

    [MemoryPostgresFact]
    public async Task MemoryAuthorityMigrationBackfillsPopulatedAuthorityAndRejectsLegacyReceiptAndRollback()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var migration = new MemoryAccessAuthority(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var source = await SeedReadAudienceAsync(fixture, "Relationship");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var stored = await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Episode.Partition, source.Episode.Id);
        var captured = await new MemoryRecallDispatchEvidence(db).CaptureReadAsync(stored, source.Episode.Partition, default);
        var users = new[] { fixture.EmployeeId, fixture.HumanId };
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == fixture.OrganizationId && users.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ApplicationUserId, x.AgentInstallationId, x.IsActive, x.ArchivedAt, x.EmployeeType,
                x.RoleId, x.ReportsToOrganizationUserId, x.PermissionLevel, x.Revision }).ToArrayAsync();
        var oldAuthority = MemoryRecallDispatchEvidence.Hash(JsonSerializer.Serialize(new { people, memberships = Array.Empty<object>(), definitions = Array.Empty<object>() }, ReadEvidenceJson));
        db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            EmployeeId = fixture.EmployeeId, InstallationId = fixture.InstallationId, RuntimeId = Guid.Parse(session.RuntimeInstanceId),
            WorkId = work.Id, Attempt = 1, GrantRevision = 1, Capability = CSweetMemoryCapabilities.Query,
            EvidenceJson = captured!.EvidenceJson, AuthorityHash = oldAuthority, ReceiptHash = new string('A', 64), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
            await transaction.CommitAsync();
        }
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"MemoryAccessAuthority\"").SingleAsync());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        var blocked = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("discard revocation history", blocked.MessageText);
        Assert.Equal(oldAuthority, (await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).AuthorityHash);
    }
}
