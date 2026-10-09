using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using CSweet.AgentHost.Broker;
using CSweet.Agent.SDK;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private const string SharedInstruction = "Change the player paddle to blue.";
    private const string PrivateInstructionContext = "Private: my bank balance is 123. ";
    private static WorkInstructionPublicationService InstructionService(CSweetDbContext db, IMemoryStore memory) => new(db, new ScopedActionAuthorizationService(db), memory);
    private sealed record InstructionSetup(Guid User, Guid Board, Guid Item, SelectWorkInstructionRequest Selection);

    private static async Task<InstructionSetup> PrepareInstructionAsync(DurabilityFixture fixture, string prefix = PrivateInstructionContext)
    {
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        await using var db = fixture.Context();
        await using var guards = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(WorkInstructionPublicationConsent.InstallGuards);
        await db.Database.ExecuteSqlRawAsync(WorkInstructionMemoryLifecycle.InstallGuards);
        await guards.CommitAsync();
        var message = await db.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId);
        message.Content = prefix + SharedInstruction + " Unrelated personal information.";
        message.SenderOrganizationUserId = fixture.HumanId;
        db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = fixture.HumanId,
            Action = WorkItemActions.Comment, ScopeKind = GrantScopeKind.WorkItem, ScopeId = scoped.Item });
        await db.SaveChangesAsync();
        var item = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == scoped.Item);
        return new(scoped.User, item.BoardId!.Value, item.Id, new(scoped.Conversation, fixture.MessageId, prefix.Length, SharedInstruction.Length));
    }

    [MemoryPostgresTheory]
    [InlineData("allowed")]
    [InlineData("missing-read")]
    [InlineData("missing-comments")]
    [InlineData("inactive")]
    [InlineData("archived")]
    public async Task InstructionPublicationRealtimeUsesCurrentHumanCommentAuthority(string scenario)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var human = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = human, OrganizationId = fixture.OrganizationId, DisplayName = "Case reviewer",
            EmployeeType = EmployeeType.Human, IsActive = scenario != "inactive", ArchivedAt = scenario == "archived" ? DateTimeOffset.UtcNow : null });
        foreach (var action in new[] { WorkItemActions.Read, WorkItemActions.ReadComments })
            if (!(scenario == "missing-read" && action == WorkItemActions.Read || scenario == "missing-comments" && action == WorkItemActions.ReadComments))
                db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                    SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = human, Action = action, ScopeKind = GrantScopeKind.WorkItem, ScopeId = setup.Item });
        await db.SaveChangesAsync();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, new(Guid.NewGuid(), setup.Selection, preview.ReviewToken));
        var wake = Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        var recipients = JsonSerializer.Deserialize<Guid[]>(wake.RecipientOrganizationUserIdsJson)!;
        Assert.Contains(fixture.HumanId, recipients);
        Assert.Equal(scenario == "allowed", recipients.Contains(human));
        Assert.DoesNotContain(SharedInstruction, wake.DataJson); Assert.DoesNotContain(PrivateInstructionContext, wake.DataJson);
        Assert.DoesNotContain(setup.Selection.ConversationId.ToString("D"), wake.DataJson);
    }

    [MemoryPostgresTheory]
    [InlineData(PrivateInstructionContext)]
    [InlineData("Private 😀: ")]
    public async Task InstructionPublicationSelectsOnlyReviewedTextAndRecoversWithoutDuplicateEffects(string prefix)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture, prefix);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        Assert.Equal(SharedInstruction, preview.Instruction);
        Assert.DoesNotContain(prefix, JsonSerializer.Serialize(preview));
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        var accepted = await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request);
        Assert.False(accepted.Replayed); Assert.Equal("Published", accepted.Status);
        await using var restarted = fixture.Context();
        var replay = await InstructionService(restarted, fixture.Store).PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request);
        Assert.True(replay.Replayed); Assert.Equal(accepted.Id, replay.Id);
        var comment = await restarted.WorkItemComments.SingleAsync();
        Assert.Equal(SharedInstruction, comment.Body); Assert.Equal(fixture.HumanId, comment.AuthorSubjectId);
        Assert.Equal(WorkInstructionPublicationService.CommentKind, comment.Kind);
        Assert.Single(await restarted.WorkInstructionPublications.ToListAsync());
        Assert.Single(await restarted.WorkItemActivities.Where(x => x.EventType == "instruction.published").ToListAsync());
        Assert.Single(await restarted.AuditOutbox.Where(x => x.SourceEntityType == "WorkInstructionPublication").ToListAsync());
        var wake = Assert.Single(await restarted.AgentPlatformEventOutbox.Where(x => x.EventType == WorkItemDiscussion.Changed).ToListAsync());
        Assert.Equal(fixture.InstallationId, wake.TargetInstallationId);
        Assert.DoesNotContain(prefix, wake.DataJson); Assert.DoesNotContain(SharedInstruction, wake.DataJson);
        Assert.DoesNotContain(setup.Selection.ConversationId.ToString("D"), wake.DataJson);
        var uiWake = Assert.Single(await restarted.ApplicationRealtimeOutbox.Where(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        Assert.Contains(fixture.HumanId, JsonSerializer.Deserialize<Guid[]>(uiWake.RecipientOrganizationUserIdsJson)!);
        Assert.DoesNotContain(prefix, uiWake.DataJson); Assert.DoesNotContain(SharedInstruction, uiWake.DataJson);
        Assert.DoesNotContain(setup.Selection.ConversationId.ToString("D"), uiWake.DataJson);
        Assert.DoesNotContain(prefix, JsonSerializer.Serialize(await restarted.WorkItemActivities.ToListAsync()));
        var page = await InstructionService(restarted, fixture.Store).ListAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User);
        Assert.Equal(accepted.Id, Assert.Single(page.Items).Id);
        Assert.DoesNotContain(fixture.MessageId.ToString("D"), JsonSerializer.Serialize(page));
        Assert.DoesNotContain(prefix, JsonSerializer.Serialize(page));
        Assert.Equal(prefix + SharedInstruction + " Unrelated personal information.",
            (await restarted.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId)).Content);
    }

    [MemoryPostgresTheory]
    [InlineData("foreign-human")]
    [InlineData("assistant")]
    [InlineData("missing-author")]
    [InlineData("archived-conversation")]
    [InlineData("merged-conversation")]
    [InlineData("archived-board")]
    [InlineData("archived-item")]
    [InlineData("no-comment-grant")]
    [InlineData("no-read-grant")]
    [InlineData("participant-left")]
    [InlineData("source-erased")]
    public async Task InstructionPublicationRejectsUnauthorizedSourceOrTargetWithoutEffects(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var now = DateTimeOffset.UtcNow;
        switch (change)
        {
            case "foreign-human": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.SenderOrganizationUserId, fixture.EmployeeId)); break;
            case "assistant": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, ConversationRole.Assistant)); break;
            case "missing-author": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.SenderOrganizationUserId, (Guid?)null)); break;
            case "archived-conversation": await db.CoreConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "merged-conversation":
                var replacement = new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                    InitiatedByOrganizationUserId = fixture.HumanId, Kind = ConversationKind.Team };
                db.CoreConversations.Add(replacement); await db.SaveChangesAsync();
                await db.CoreConversations.Where(x => x.Id == setup.Selection.ConversationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MergedIntoConversationId, replacement.Id)); break;
            case "archived-board": await db.WorkBoards.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "archived-item": await db.CoreWorkTasks.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, now)); break;
            case "no-comment-grant": await db.ScopedActionGrants.Where(x => x.Action == WorkItemActions.Comment).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now)); break;
            case "no-read-grant": await db.ScopedActionGrants.Where(x => x.Action == WorkItemActions.Read).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now)); break;
            case "participant-left": await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftAt, now)); break;
            case "source-erased": db.MemoryCaptureExclusions.Add(new() { SourceMessageId = fixture.MessageId, ExcludedAt = now }); await db.SaveChangesAsync(); break;
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InstructionService(db, fixture.Store).PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection));
        Assert.Empty(await db.WorkInstructionPublications.ToListAsync()); Assert.Empty(await db.WorkItemComments.ToListAsync());
        Assert.Empty(await db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkItemDiscussion.Changed).ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("message")]
    [InlineData("planning")]
    [InlineData("comment-grant-restored")]
    [InlineData("participant-restored")]
    [InlineData("person-restored")]
    [InlineData("installation-restored")]
    public async Task InstructionPublicationCannotUseStaleReviewAfterChangesAndRestoration(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        switch (change)
        {
            case "message": await db.CoreConversationMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, x => x.Content + " changed")); break;
            case "planning": await db.CoreWorkTasks.ExecuteUpdateAsync(s => s.SetProperty(x => x.PlanningRevision, x => x.PlanningRevision + 1)); break;
            case "comment-grant-restored":
                await db.ScopedActionGrants.Where(x => x.Action == WorkItemActions.Comment).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
                await db.ScopedActionGrants.Where(x => x.Action == WorkItemActions.Comment).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)null)); break;
            case "participant-restored":
                await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftAt, DateTimeOffset.UtcNow));
                await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftAt, (DateTimeOffset?)null)); break;
            case "person-restored":
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true)); break;
            case "installation-restored":
                await db.AgentInstallations.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
                await db.AgentInstallations.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true)); break;
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User,
            new(Guid.NewGuid(), setup.Selection, preview.ReviewToken)));
        Assert.Empty(await db.WorkInstructionPublications.ToListAsync()); Assert.Empty(await db.WorkItemComments.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationAuditFailureRollsBackCommentReceiptAndWake()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_instruction_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                IF NEW."SourceEntityType"='WorkInstructionPublication' THEN RAISE EXCEPTION 'injected'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_instruction_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW EXECUTE FUNCTION reject_instruction_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request));
        Assert.Empty(await db.WorkItemComments.ToListAsync()); Assert.Empty(await db.WorkInstructionPublications.ToListAsync());
        Assert.Empty(await db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkItemDiscussion.Changed).ToListAsync());
        Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_instruction_audit ON \"ComputeAuditOutbox\"; DROP FUNCTION reject_instruction_audit();");
        Assert.False((await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request)).Replayed);
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationReceiptIsImmutableAndReplayDoesNotRestoreWithdrawnInstruction()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        var accepted = await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"WorkInstructionPublications\" SET \"SelectionOffset\"=0"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"WorkInstructionPublications\""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE \"WorkInstructionPublications\""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"WorkItemComments\" SET \"CausationId\"='changed'"));
        await db.WorkItemComments.ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow).SetProperty(x => x.Revision, x => x.Revision + 1));
        var replay = await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request);
        Assert.Equal(accepted.Id, replay.Id); Assert.True(replay.Replayed); Assert.Equal("Withdrawn", replay.Status);
        Assert.Single(await db.WorkItemComments.ToListAsync());
        await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId && x.Action == WorkItemActions.ReadComments)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User));
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationMigrationPreservesExistingWorkAndRefusesUsedConsentDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var migration = new WorkInstructionPublicationConsent(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(setup.Item, (await db.CoreWorkTasks.SingleAsync()).Id);
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, new(Guid.NewGuid(), setup.Selection, preview.ReviewToken));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(generator.Generate(migration.DownOperations, db.Model)[0].CommandText));
        Assert.Single(await db.WorkInstructionPublications.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationReachesAuthorizedAgentCommentsButNotAReaderWithRevokedCaseAccess()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, new(Guid.NewGuid(), setup.Selection, preview.ReviewToken));
        var handler = new WorkManagementCapabilityHandler(db, new ScopedActionAuthorizationService(db), new TestAuditEventWriter(),
            new WorkOrchestrationService(db, TimeProvider.System));
        var session = new AgentSession("instruction-reader", "memory.test", fixture.InstallationId.ToString("D"),
            fixture.OrganizationId.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { WorkItemActions.ReadComments }, 1));
        var request = new RequestCapability { RequestId = "read-instruction", Capability = WorkItemActions.ReadComments,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { boardId = setup.Board, itemId = setup.Item })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var granted = Assert.Single(results); Assert.True(granted.Succeeded, granted.Error);
        Assert.Contains(SharedInstruction, granted.Payload.ToStringUtf8());
        Assert.DoesNotContain(PrivateInstructionContext, granted.Payload.ToStringUtf8());
        Assert.DoesNotContain(setup.Selection.ConversationId.ToString("D"), granted.Payload.ToStringUtf8());
        await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.InstallationId && x.Action == WorkItemActions.Read)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
        results.Clear();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        var denied = Assert.Single(results); Assert.False(denied.Succeeded);
        Assert.DoesNotContain(SharedInstruction, denied.Payload.ToStringUtf8());
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationConcurrentReplayHasOneReceiptAndWakeAndDifferentRequestCannotReuseIdentity()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context(); await using var other = fixture.Context();
        var preview = await InstructionService(db, fixture.Store).PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        async Task Publish(CSweetDbContext context)
        {
            try { await InstructionService(context, fixture.Store).PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request); }
            catch (DbUpdateConcurrencyException) { }
        }
        await Task.WhenAll(Publish(db), Publish(other));
        Assert.Single(await db.WorkInstructionPublications.AsNoTracking().ToListAsync());
        Assert.Single(await db.WorkItemComments.AsNoTracking().ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkItemDiscussion.Changed).ToListAsync());
        Assert.True((await InstructionService(other, fixture.Store).PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, request)).Replayed);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => InstructionService(other, fixture.Store).PublishAsync(fixture.OrganizationId, setup.Board,
            setup.Item, setup.User, request with { Selection = request.Selection with { Length = request.Selection.Length - 1 } }));
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationDiscoveryPagesOnlyOwnOperationsAndRejectsForeignCursor()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        for (var i = 0; i < 21; i++)
            await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, new(Guid.NewGuid(), setup.Selection, preview.ReviewToken));
        var first = await service.ListAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User);
        Assert.Equal(20, first.Items.Count); Assert.NotNull(first.NextCursor);
        var last = await service.ListAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, first.NextCursor);
        Assert.Single(last.Items); Assert.Null(last.NextCursor);
        Assert.Equal(21, first.Items.Concat(last.Items).Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(fixture.MessageId.ToString("D"), JsonSerializer.Serialize(first));
        Assert.DoesNotContain(PrivateInstructionContext, JsonSerializer.Serialize(first));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(fixture.OrganizationId, setup.Board, setup.Item, Guid.NewGuid()));
    }
}

