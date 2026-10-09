using System.Text.Json;
using AgentCoordinationEvents = CSweet.Agent.SDK.AgentCoordinationEvents;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record ScopedCoordinationFixture(Guid Session, Guid Conversation, Guid Partner, Guid PartnerInstallation);
    private static async Task<ScopedCoordinationFixture> SeedScopedCoordinationAsync(DurabilityFixture fixture,
        ScopedAudienceFixture scoped, Guid workId)
    {
        var stage = await SeedScopedStageConsumerAsync(fixture, scoped, workId);
        await using var db = fixture.Context();
        var now = DateTimeOffset.UtcNow;
        var partner = Guid.NewGuid(); var installation = Guid.NewGuid(); var chat = Guid.NewGuid(); var coordination = Guid.NewGuid();
        var existing = await db.AgentInstallations.AsNoTracking().SingleAsync(x => x.Id == fixture.InstallationId);
        db.AgentInstallations.Add(new() { Id = installation, InstallationKey = Guid.NewGuid(), BusinessId = existing.BusinessId,
            PackageVersionId = existing.PackageVersionId, IsEnabled = true });
        db.CoreOrganizationUsers.Add(new() { Id = partner, OrganizationId = fixture.OrganizationId,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation, DisplayName = "Producer" });
        db.CoreConversations.Add(new() { Id = chat, OrganizationId = fixture.OrganizationId, Kind = ConversationKind.Team,
            Title = "Case collaboration", CreatedAt = now, UpdatedAt = now });
        foreach (var person in new[] { partner, fixture.EmployeeId, fixture.HumanId })
            db.ConversationParticipants.Add(new() { Id = Guid.NewGuid(), ConversationId = chat, OrganizationUserId = person, JoinedAt = now.AddMinutes(-1) });
        foreach (var action in new[] { WorkItemActions.Read, WorkItemActions.ReadComments })
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installation, ScopeKind = GrantScopeKind.WorkItem, ScopeId = scoped.Item, Action = action });
        var board = await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).Select(x => x.BoardId!.Value).SingleAsync();
        db.AgentCoordinationSessions.Add(new() { Id = coordination, OrganizationId = fixture.OrganizationId,
            SourceKind = "WorkItem", SourceBoardId = board, SourceWorkItemId = scoped.Item, SourceSprintExecutionId = stage.Sprint,
            SourceStageExecutionId = stage.Stage, SourceAssignmentRevision = 0,
            ConversationId = chat, InitiatorOrganizationUserId = fixture.EmployeeId, InitiatorInstallationId = fixture.InstallationId,
            TargetOrganizationUserId = partner, TargetInstallationId = installation, CurrentOrganizationUserId = fixture.EmployeeId,
            CurrentAgentWorkItemId = workId, Status = AgentCoordinationStatus.Active, IdempotencyKey = coordination.ToString("N"),
            Subject = "Carry the authorized case instruction", Objective = "Refine the shared work", CreatedAt = now, UpdatedAt = now });
        var work = await db.AgentWorkItems.SingleAsync(x => x.Id == workId);
        work.Kind = AgentWorkKind.Event; work.Name = AgentCoordinationEvents.TurnRequested;
        work.SourceType = "agent-coordination"; work.SourceId = Guid.NewGuid().ToString("D"); work.CorrelationId = coordination.ToString("D");
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(ScopedCoordinationMemoryConsumerAuthority.InstallGuards);
        await transaction.CommitAsync();
        return new(coordination, chat, partner, installation);
    }

    [MemoryPostgresFact]
    public async Task ScopedCaseCoordinationReadsCurrentCaseAndPreservesRoutineProgressAcrossReconnect()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (broker, work) = await SeedBrokerReadLeaseAsync(f);
        var coordination = await SeedScopedCoordinationAsync(f,scoped,work.Id);
        await using var db = f.Context();
        var read = await ReadHandler(f,db).HandleAsync(broker,ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice")),default);
        Assert.True(read.Succeeded,read.Error); Assert.Contains("Alice",read.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(broker,work.Id,default);
        await db.AgentCoordinationSessions.Where(x => x.Id == coordination.Session).ExecuteUpdateAsync(x => x.SetProperty(p => p.UpdatedAt,DateTimeOffset.UtcNow)
            .SetProperty(p => p.Subject,"Reworded title"));
        await using var reconnect = f.Context();
        await new PlatformMemoryReadEvidence(reconnect).AuthorizeDispatchAsync(broker,work.Id,default);
        Assert.Null((await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("private-chat")]
    [InlineData("board")]
    [InlineData("wrong-case")]
    [InlineData("wrong-board")]
    [InlineData("old-turn")]
    [InlineData("wrong-speaker")]
    [InlineData("wrong-installation")]
    [InlineData("wrong-event")]
    [InlineData("closed")]
    [InlineData("partner-left")]
    [InlineData("partner-grant")]
    [InlineData("observer-grant")]
    [InlineData("disabled-partner")]
    [InlineData("archived-chat")]
    public async Task ScopedCaseCoordinationDeniesUnboundOrUnauthorizedAudienceBeforeReturningContent(string scenario)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (broker, work) = await SeedBrokerReadLeaseAsync(f);
        var coordination = await SeedScopedCoordinationAsync(f,scoped,work.Id);
        await using var db = f.Context();
        await ChangeScopedCoordinationAsync(db, f,coordination,scenario);
        var read = await ReadHandler(f,db).HandleAsync(broker,ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice")),default);
        Assert.False(read.Succeeded); Assert.DoesNotContain("Alice",read.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("speaker")]
    [InlineData("status")]
    [InlineData("ordinal")]
    [InlineData("partner-grant")]
    [InlineData("partner-membership")]
    [InlineData("installation")]
    public async Task ScopedCaseCoordinationChangeAndRestoreCannotReviveDeliveredRead(string scenario)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (broker, work) = await SeedBrokerReadLeaseAsync(f);
        var c = await SeedScopedCoordinationAsync(f,scoped,work.Id); await using var db = f.Context();
        Assert.True((await ReadHandler(f,db).HandleAsync(broker,ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice")),default)).Succeeded);
        var sessions = db.AgentCoordinationSessions.Where(x => x.Id == c.Session);
        if (scenario == "speaker") { await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentOrganizationUserId,c.Partner)); await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentOrganizationUserId,f.EmployeeId)); }
        if (scenario == "status") { await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status,AgentCoordinationStatus.Failed)); await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status,AgentCoordinationStatus.Active)); }
        if (scenario == "ordinal") { await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.NextTurnOrdinal,2)); await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.NextTurnOrdinal,1)); }
        if (scenario == "partner-grant") { var grants = db.ScopedActionGrants.Where(x => x.SubjectId == c.PartnerInstallation); await grants.ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt,DateTimeOffset.UtcNow)); await grants.ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt,(DateTimeOffset?)null)); }
        if (scenario == "partner-membership") { var members = db.ConversationParticipants.Where(x => x.ConversationId == c.Conversation && x.OrganizationUserId == c.Partner); await members.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt,DateTimeOffset.UtcNow)); await members.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt,(DateTimeOffset?)null)); }
        if (scenario == "installation") { var installs = db.AgentInstallations.Where(x => x.Id == c.PartnerInstallation); await installs.ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled,false)); await installs.ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled,true)); }
        await using var reconnect = f.Context();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(reconnect).AuthorizeDispatchAsync(broker,work.Id,default));
        Assert.NotNull((await reconnect.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    private static async Task ChangeScopedCoordinationAsync(CSweet.Infrastructure.Persistence.CSweetDbContext db,
        DurabilityFixture f, ScopedCoordinationFixture c, string scenario)
    {
        var sessions = db.AgentCoordinationSessions.Where(x => x.Id == c.Session);
        switch (scenario)
        {
            case "private-chat": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceKind,"Chat")); break;
            case "board": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceKind,"Board")); break;
            case "wrong-case": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceWorkItemId,(Guid?)null)); break;
            case "wrong-board": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceBoardId,(Guid?)null)); break;
            case "old-turn": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAgentWorkItemId,(Guid?)null)); break;
            case "wrong-speaker": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentOrganizationUserId,c.Partner)); break;
            case "wrong-installation": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.InitiatorInstallationId,c.PartnerInstallation)); break;
            case "wrong-event": var workId = await sessions.Select(x => x.CurrentAgentWorkItemId).SingleAsync(); await db.AgentWorkItems.Where(x => x.Id == workId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Name,"forged-event")); break;
            case "closed": await sessions.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status,AgentCoordinationStatus.Completed)); break;
            case "partner-left": await db.ConversationParticipants.Where(x => x.ConversationId == c.Conversation && x.OrganizationUserId == c.Partner).ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt,DateTimeOffset.UtcNow)); break;
            case "partner-grant": await db.ScopedActionGrants.Where(x => x.SubjectId == c.PartnerInstallation).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt,DateTimeOffset.UtcNow)); break;
            case "observer-grant": await db.ScopedActionGrants.Where(x => x.SubjectId == f.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt,DateTimeOffset.UtcNow)); break;
            case "disabled-partner": await db.AgentInstallations.Where(x => x.Id == c.PartnerInstallation).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled,false)); break;
            case "archived-chat": await db.CoreConversations.Where(x => x.Id == c.Conversation).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt,DateTimeOffset.UtcNow)); break;
        }
    }

    [MemoryPostgresFact]
    public async Task ScopedCaseCoordinationMigrationPreservesSessionAndProtectsPersistentHistory()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (_,work) = await SeedBrokerReadLeaseAsync(f);
        var c = await SeedScopedCoordinationAsync(f,scoped,work.Id); await using var db = f.Context();
        Assert.Equal(1,await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryCoordinationConsumerAuthority\" WHERE \"Id\"={c.Session}").SingleAsync());
        Assert.Equal("Carry the authorized case instruction",(await db.AgentCoordinationSessions.AsNoTracking().SingleAsync()).Subject);
        foreach (var sql in new[] { "UPDATE \"MemoryCoordinationConsumerAuthority\" SET \"Revision\"=1", "DELETE FROM \"MemoryCoordinationConsumerAuthority\"", "TRUNCATE \"MemoryCoordinationConsumerAuthority\"", "TRUNCATE \"AgentCoordinationSessions\" CASCADE" })
            Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        var migration = new ScopedCoordinationMemoryConsumerAuthority(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(command.CommandText));
        await db.AgentCoordinationSessions.Where(x => x.Id == c.Session).ExecuteDeleteAsync();
        Assert.Equal(2,await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryCoordinationConsumerAuthority\" WHERE \"Id\"={c.Session}").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [MemoryPostgresFact]
    public async Task ScopedCaseCoordinationDeletedAndRecreatedSessionCannotReviveOldEvidence()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (broker,work) = await SeedBrokerReadLeaseAsync(f);
        var c = await SeedScopedCoordinationAsync(f,scoped,work.Id); await using var db = f.Context();
        Assert.True((await ReadHandler(f,db).HandleAsync(broker,ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice")),default)).Succeeded);
        var original = await db.AgentCoordinationSessions.AsNoTracking().SingleAsync(x => x.Id == c.Session);
        await db.AgentCoordinationSessions.Where(x => x.Id == c.Session).ExecuteDeleteAsync();
        db.AgentCoordinationSessions.Add(original); await db.SaveChangesAsync();
        Assert.Equal(3,await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryCoordinationConsumerAuthority\" WHERE \"Id\"={c.Session}").SingleAsync());
        await using var reconnect = f.Context();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(reconnect).AuthorizeDispatchAsync(broker,work.Id,default));
    }

    [MemoryPostgresFact]
    public async Task ScopedCaseCoordinationEmptyMigrationRoundTripPreservesModelAndExistingWork()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true); await using var db = f.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var migration = new ScopedCoordinationMemoryConsumerAuthority(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal("My name is Alice.",(await db.CoreConversationMessages.AsNoTracking().SingleAsync()).Content);
        Assert.False(db.Database.HasPendingModelChanges());
        await transaction.CommitAsync();
    }

    [MemoryPostgresFact]
    public async Task ScopedCaseCoordinationContentionDoesNotResetStillAuthorizedRuntime()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres:true);
        var scoped = await SeedScopedAudienceAsync(f,"Case"); var (broker,work) = await SeedBrokerReadLeaseAsync(f);
        var c = await SeedScopedCoordinationAsync(f,scoped,work.Id); await using var db = f.Context();
        Assert.True((await ReadHandler(f,db).HandleAsync(broker,ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice")),default)).Succeeded);
        await using var writer = f.Context(); await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.AgentCoordinationSessions.Where(x => x.Id == c.Session).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status,AgentCoordinationStatus.Failed));
        await using (var read = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(broker,work.Id,default));
            await read.RollbackAsync();
        }
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await transaction.RollbackAsync();
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(broker,work.Id,default);
    }
}
