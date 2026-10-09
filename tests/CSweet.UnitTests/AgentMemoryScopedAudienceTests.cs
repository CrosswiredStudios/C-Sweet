using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Memory;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record ScopedAudienceFixture(Guid User, MemoryEpisode Episode, Guid Conversation, Guid? Item);

    private static async Task<ScopedAudienceFixture> SeedScopedAudienceAsync(DurabilityFixture fixture, string kind)
    {
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(ScopedMemoryAudienceAuthority.AuthorityTriggers);
        await db.Database.ExecuteSqlRawAsync(ScopedCaseMemoryAudienceAuthority.AuthorityTriggers);
        await using (var identityMigration = await db.Database.BeginTransactionAsync())
        {
            // ExecuteSqlRaw formats braces; regex quantifiers in migration SQL are literals.
            await db.Database.ExecuteSqlRawAsync(ScopedMemoryIdentityHistory.IdentityHistory.Replace("{", "{{").Replace("}", "}}"));
            await identityMigration.CommitAsync();
        }
        var conversation = await db.CoreConversations.Select(x => x.Id).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        db.ConversationParticipants.AddRange(new ConversationParticipant { Id=Guid.NewGuid(), ConversationId=conversation,
            OrganizationUserId=fixture.EmployeeId, JoinedAt=now.AddMinutes(-1) },
            new ConversationParticipant { Id=Guid.NewGuid(), ConversationId=conversation,
                OrganizationUserId=fixture.HumanId, JoinedAt=now.AddMinutes(-1) });
        Guid? itemId=null;
        if (kind == "Case")
        {
            var board = new WorkBoard { Id=Guid.NewGuid(), OrganizationId=fixture.OrganizationId, Name="Case board", Key="case" };
            itemId=Guid.NewGuid(); db.WorkBoards.Add(board);
            db.CoreWorkTasks.Add(new WorkTask { Id=itemId.Value, OrganizationId=fixture.OrganizationId, BoardId=board.Id,
                AssignedEmployeeId=fixture.EmployeeId, AssignedAgentInstallationId=fixture.InstallationId, Title="Case",
                CreatedAt=now, UpdatedAt=now });
            foreach (var action in new[] { WorkItemActions.Read, WorkItemActions.ReadComments })
                foreach (var subject in new[] { (GrantSubjectKind.AgentInstallation, fixture.InstallationId),
                    (GrantSubjectKind.OrganizationUser, fixture.HumanId) })
                    db.ScopedActionGrants.Add(new ScopedActionGrant { Id=Guid.NewGuid(), OrganizationId=fixture.OrganizationId,
                        SubjectKind=subject.Item1, SubjectId=subject.Item2, Action=action, ScopeKind=GrantScopeKind.WorkItem,
                        ScopeId=itemId, GrantedAt=now.AddMinutes(-1) });
        }
        await db.SaveChangesAsync();
        var partition = kind == "Case" ? EmployeeMemoryNamespaces.Case(fixture.OrganizationId.ToString("D"), itemId!.Value.ToString("D"), "csweet").Partition :
            new MemoryPartition(fixture.OrganizationId.ToString("D"), "csweet", ConversationId:conversation.ToString("D"));
        var (user, episode) = await SeedJoblessProposalAsync(fixture, x => x with { Partition=partition,
            Scope=kind == "Case" ? MemoryScope.Custom : MemoryScope.Conversation });
        return new(user, episode, conversation, itemId);
    }

    [MemoryPostgresTheory]
    [InlineData("Case", false)]
    [InlineData("Conversation", false)]
    [InlineData("Case", true)]
    [InlineData("Conversation", true)]
    public async Task ScopedAudienceBrokerReadIsBoundToCurrentConsumerAndStopsAfterRevocation(string kind, bool restore)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var scoped=await SeedScopedAudienceAsync(fixture,kind);
        var turn=await SeedRecallTurnAsync(fixture);
        var (session, work)=await SeedBrokerReadLeaseAsync(fixture);
        await using var db=fixture.Context();
        (await db.AgentWorkItems.SingleAsync(x=>x.Id==work.Id)).SourceType="chat-turn";
        (await db.AgentWorkItems.SingleAsync(x=>x.Id==work.Id)).SourceId=turn.Id.ToString("D");
        await db.SaveChangesAsync();
        var request=ReadRequest("search",new MemorySearchRequest(scoped.Episode.Partition,scoped.Episode.Scope,"Alice"));
        var result=await ReadHandler(fixture,db).HandleAsync(session,request,default);
        Assert.True(result.Succeeded,result.Error);
        Assert.Contains("Alice",result.Payload.ToStringUtf8());
        var receipt=await db.AgentMemoryReadReceipts.SingleAsync();
        Assert.Contains("scopedAuthorityHash",receipt.EvidenceJson);
        Assert.DoesNotContain("Alice",receipt.EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session,work.Id,default);
        var exported = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("export", scoped.Episode.Partition, CSweetMemoryCapabilities.Export), default);
        Assert.True(exported.Succeeded, exported.Error);
        Assert.Contains("Alice", exported.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        if(kind=="Conversation")
        {
            // Read-position changes must not reset an otherwise authorized runtime.
            await db.ConversationParticipants.Where(x=>x.ConversationId==scoped.Conversation)
                .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.LastReadMessageSequence,10));
            await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session,work.Id,default);
            await db.ConversationParticipants.Where(x=>x.ConversationId==scoped.Conversation && x.OrganizationUserId==fixture.HumanId)
                .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.LeftAt,DateTimeOffset.UtcNow));
        }
        else await db.ScopedActionGrants.Where(x=>x.SubjectId==fixture.HumanId)
            .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RevokedAt,DateTimeOffset.UtcNow));
        if (restore && kind == "Conversation") await db.ConversationParticipants
            .Where(x => x.ConversationId == scoped.Conversation && x.OrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, (DateTimeOffset?)null));
        else if (restore) await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, (DateTimeOffset?)null));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(()=>new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session,work.Id,default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceChangeAndRestoreDoesNotReviveHumanReviewPreview(string kind)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var scoped=await SeedScopedAudienceAsync(fixture,kind);
        await using var db=fixture.Context();
        var service=new AgentMemoryReviewService(db,fixture.Store,TimeProvider.System);
        var preview=await service.GetSuppressionAsync(fixture.OrganizationId,fixture.EmployeeId,scoped.Episode.Id,scoped.User);
        if(kind=="Conversation")
        {
            await db.ConversationParticipants.Where(x=>x.OrganizationUserId==fixture.HumanId)
                .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.LeftAt,DateTimeOffset.UtcNow));
            await db.ConversationParticipants.Where(x=>x.OrganizationUserId==fixture.HumanId)
                .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.LeftAt,(DateTimeOffset?)null));
        }
        else
        {
            await db.ScopedActionGrants.Where(x=>x.SubjectId==fixture.HumanId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RevokedAt,DateTimeOffset.UtcNow));
            await db.ScopedActionGrants.Where(x=>x.SubjectId==fixture.HumanId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RevokedAt,(DateTimeOffset?)null));
        }
        var refreshed=await service.GetSuppressionAsync(fixture.OrganizationId,fixture.EmployeeId,scoped.Episode.Id,scoped.User);
        Assert.NotEqual(preview.EvidenceToken,refreshed.EvidenceToken);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>service.SuppressSourceAsync(fixture.OrganizationId,
            fixture.EmployeeId,scoped.Episode.Id,scoped.User,new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken)));
    }

    [MemoryPostgresTheory]
    [InlineData("Case", "Claim")]
    [InlineData("Conversation", "Claim")]
    [InlineData("Case", "Procedure")]
    [InlineData("Conversation", "Procedure")]
    [InlineData("Case", "Core")]
    [InlineData("Conversation", "Core")]
    public async Task ScopedAudienceHumanCorrectionAndCleanupPreserveSourceAndRequireCurrentRights(string kind, string recordKind)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var scoped=await SeedScopedAudienceAsync(fixture,kind);
        var correction=await WriteOrganizationCorrectionAsync(fixture,
            new OrganizationCorrectionFixture(scoped.User,fixture.EmployeeId,scoped.Episode,scoped.Episode),recordKind);
        var correctedSource = (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition, correction))!;
        Assert.Equal(scoped.Episode.Scope, correctedSource.Scope);
        await using var db=fixture.Context();
        var service=ErasureService(fixture,db);
        var preview=await service.GetErasureImpactAsync(fixture.OrganizationId,fixture.EmployeeId,correction,scoped.User);
        Assert.Null(preview.ApplyBlockedReason);
        var request=new EraseMemorySourceRequest(Guid.NewGuid(),preview.EvidenceToken!);
        Assert.Equal("completed",(await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,correction,scoped.User,request)).Status);
        Assert.NotNull(await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition,scoped.Episode.Id));
        Assert.True((await service.EraseSourceAsync(fixture.OrganizationId,fixture.EmployeeId,correction,scoped.User,request)).WasReplay);
        if(kind=="Conversation") await db.ConversationParticipants.Where(x=>x.OrganizationUserId==fixture.HumanId)
            .ExecuteUpdateAsync(x=>x.SetProperty(p=>p.LeftAt,DateTimeOffset.UtcNow));
        else await db.ScopedActionGrants.Where(x=>x.SubjectId==fixture.HumanId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RevokedAt,DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetErasureStatusAsync(fixture.OrganizationId,
            fixture.EmployeeId,request.OperationId,scoped.User));
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceRecoveryDiscoveryAndQueuedExtractionUseCurrentAuthority(string kind)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var scoped=await SeedScopedAudienceAsync(fixture,kind);
        await using var db=fixture.Context();
        var service=IngestionRecovery(fixture,db);
        Assert.Equal(kind,Assert.Single((await service.ListAsync(fixture.OrganizationId,fixture.EmployeeId,scoped.User)).Items).Audience);
        var preview=await service.PreviewAsync(fixture.OrganizationId,fixture.EmployeeId,scoped.Episode.Id,scoped.User);
        Assert.True(preview.CanQueue);
        await service.RecoverAsync(fixture.OrganizationId,fixture.EmployeeId,scoped.Episode.Id,scoped.User,
            new(Guid.NewGuid(),preview.Revision,preview.EvidenceToken));
        var provider=new ScriptedProviderFactory((_,_)=>Task.CompletedTask);
        Assert.Equal(1,await fixture.Service(db,provider).ProcessPendingAsync());
        Assert.Equal(1,provider.Calls);
        Assert.Single((await fixture.Store.ExportAsync(scoped.Episode.Partition)).Claims);
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceArchiveStopsRecallButPreservesAuthorizedRetainedReview(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        if (kind == "Case") await db.CoreWorkTasks.Where(x => x.Id == scoped.Item)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        else await db.CoreConversations.Where(x => x.Id == scoped.Conversation)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MemoryScopedAudienceAuthorization.RequireAsync(db,
            fixture.OrganizationId, fixture.EmployeeId, fixture.HumanId, scoped.Episode.Partition, default));
        await MemoryScopedAudienceAuthorization.RequireAsync(db, fixture.OrganizationId, fixture.EmployeeId,
            fixture.HumanId, scoped.Episode.Partition, default, retained: true);
        var preview = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, scoped.Episode.Id, scoped.User);
        Assert.False(string.IsNullOrWhiteSpace(preview.EvidenceToken));
        var history = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", scoped.Episode.Id, scoped.User);
        Assert.NotEmpty(history.Items);
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceNamespaceRejectsForeignAndAmbiguousShapesAndBrokerManagement(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture);
        session.MemoryTenantId = fixture.OrganizationId.ToString("D");
        session.MemoryEmployeeId = fixture.EmployeeId.ToString("D");
        var partition = scoped.Episode.Partition;
        Assert.Equal(partition, PlatformMemoryNamespacePolicy.Resolve(session, partition, PlatformMemoryAction.Read).Partition);
        Assert.Equal(partition, PlatformMemoryNamespacePolicy.Resolve(session, partition, PlatformMemoryAction.Propose).Partition);
        Assert.Throws<UnauthorizedAccessException>(() => PlatformMemoryNamespacePolicy.Resolve(session, partition, PlatformMemoryAction.Manage));
        foreach (var invalid in new[] { partition with { TenantId = Guid.NewGuid().ToString("D") },
            partition with { ApplicationId = fixture.InstallationId.ToString("D") },
            partition with { AgentId = fixture.EmployeeId.ToString("D") },
            partition with { UserId = fixture.HumanId.ToString("D") },
            partition with { CustomNamespace = "unrecognized" } })
            Assert.Throws<UnauthorizedAccessException>(() => PlatformMemoryNamespacePolicy.Resolve(session, invalid, PlatformMemoryAction.Read));
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseRequiresCommentAuthorityEvenWhenWorkItemIsReadable()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var db = fixture.Context();
        await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId && x.Action == WorkItemActions.ReadComments)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MemoryScopedAudienceAuthorization.RequireAsync(db,
            fixture.OrganizationId, fixture.EmployeeId, fixture.HumanId, scoped.Episode.Partition, default));
        Assert.DoesNotContain(await MemoryScopedAudienceAuthorization.ReadableAsync(db, fixture.OrganizationId,
            fixture.EmployeeId, fixture.HumanId, default), x => x == scoped.Episode.Partition);
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceQueuedExtractionRevocationStopsBeforeProvider(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        var recovery = IngestionRecovery(fixture, db);
        var preview = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, scoped.Episode.Id, scoped.User);
        await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, scoped.Episode.Id, scoped.User,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken));
        if (kind == "Conversation") await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow));
        else await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.InstallationId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync());
        Assert.Equal(0, provider.Calls);
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Empty((await fixture.Store.ExportAsync(scoped.Episode.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseReadCannotLeakToAnotherConversationRecipientWithoutCaseGrants()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var turn = await SeedRecallTurnAsync(fixture);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        var recipient = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            EmployeeType = EmployeeType.Human, CreatedAt = DateTimeOffset.UtcNow };
        db.CoreOrganizationUsers.Add(recipient);
        db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = scoped.Conversation,
            OrganizationUserId = recipient.Id, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var currentWork = await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        currentWork.SourceType = "chat-turn"; currentWork.SourceId = turn.Id.ToString("D");
        await db.SaveChangesAsync();
        var result = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("Alice", result.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceTransferPreparationRemainsExplicitlyUnsupported(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, "Organization", Guid.Empty);
        await using var db = fixture.Context();
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId,
            scoped.User, new(Guid.NewGuid(), target, kind, [new("Episode", scoped.Episode.Id)], "Scoped handoff")));
        Assert.Empty(await db.MemoryTransferReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceChangingAuthorityRequiresRefreshInsteadOfReadingLockedState(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        await using var writer = fixture.Context();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        if (kind == "Conversation") await writer.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow));
        else await writer.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
        await using var reviewer = fixture.Context();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new AgentMemoryReviewService(reviewer,
            fixture.Store, TimeProvider.System).GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId,
            scoped.Episode.Id, scoped.User));
        await transaction.RollbackAsync();
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceMigrationUpDownPreservesConversationAndEpochCannotBeRewound()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        Assert.False(db.Database.HasPendingModelChanges());
        var migration = new ScopedMemoryAudienceAuthority();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var conversation = await db.CoreConversations.AsNoTracking().SingleAsync();
        Assert.Equal(1, conversation.MemoryAudienceRevision);
        db.ConversationParticipants.Add(new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversation.Id,
            OrganizationUserId = fixture.HumanId, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        Assert.Equal(2, await Revision());
        await db.ConversationParticipants.ExecuteUpdateAsync(x => x.SetProperty(p => p.LastReadMessageSequence, 15));
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(p => p.Title, "Renamed"));
        Assert.Equal(2, await Revision());
        await db.ConversationParticipants.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow));
        await db.ConversationParticipants.ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, (DateTimeOffset?)null));
        Assert.Equal(4, await Revision());
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(p => p.MemoryAudienceRevision, 1));
        Assert.Equal(5, await Revision());
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, (DateTimeOffset?)null));
        Assert.Equal(7, await Revision());
        var downgrade = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("discard revocation history", downgrade.MessageText);
        Assert.Equal(conversation.Id, (await db.CoreConversations.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(7, await Revision());
        Task<long> Revision() => db.CoreConversations.AsNoTracking().Select(x => x.MemoryAudienceRevision).SingleAsync();
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseAuthoritySurvivesRoutineProgressButNotArchiveAndRestore()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var db = fixture.Context();
        var original = await Authority();
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x
            .SetProperty(p => p.Title, "Updated task").SetProperty(p => p.Status, WorkTaskStatus.Running));
        await db.WorkBoards.ExecuteUpdateAsync(x => x.SetProperty(p => p.Name, "Renamed board"));
        Assert.Equal(original, await Authority());
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, (DateTimeOffset?)null));
        var restoredCase = await Authority();
        Assert.NotEqual(original, restoredCase);
        await db.WorkBoards.ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        await db.WorkBoards.ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, (DateTimeOffset?)null));
        Assert.NotEqual(restoredCase, await Authority());
        Task<string?> Authority() => MemoryScopedAudienceAuthorization.AuthorityHashAsync(db, fixture.OrganizationId,
            fixture.EmployeeId, fixture.HumanId, [scoped.Episode.Partition], default);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseMigrationPreservesRecordsAndRefusesUsedAuthorityDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var db = fixture.Context();
        var migration = new ScopedCaseMemoryAudienceAuthority();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("Case", (await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == scoped.Item)).Title);
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow));
        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("discard revocation history", failure.MessageText);
        Assert.Equal(2L, await db.CoreWorkTasks.Where(x => x.Id == scoped.Item)
            .Select(x => EF.Property<long>(x, "MemoryAudienceRevision")).SingleAsync());
    }
}
