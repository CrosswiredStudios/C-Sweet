using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using CSweet.AI.Providers;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed class InstructionCaptureProvider : ILlmProviderFactory
    {
        internal string Context = "";
        public Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken token = default) => CreateChatClientAsync(id, null, token);
        public Task<IChatClient> CreateChatClientAsync(Guid id, string? model, CancellationToken token = default) =>
            Task.FromResult<IChatClient>(new InstructionCaptureChatClient(this));
    }
    private sealed class InstructionCaptureChatClient(InstructionCaptureProvider owner) : DelegatingChatClient(new UsageChatClient())
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var current = messages.ToArray(); owner.Context = string.Join("\n", current.Select(x => x.Text));
            return base.GetResponseAsync(current, options, cancellationToken);
        }
    }
    private static WorkItemCollaborationService InstructionComments(CSweet.Infrastructure.Persistence.CSweetDbContext db) =>
        new(db, new ScopedActionAuthorizationService(db), new TestAuditEventWriter());

    private static async Task<CSweet.Contracts.WorkManagement.WorkInstructionPublicationResponse> PublishInstructionAsync(
        DurabilityFixture fixture, InstructionSetup setup)
    {
        await using var db = fixture.Context();
        var service = InstructionService(db, fixture.Store);
        var preview = await service.PreviewAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        return await service.PublishAsync(fixture.OrganizationId, setup.Board, setup.Item, setup.User,
            new(Guid.NewGuid(), setup.Selection, preview.ReviewToken));
    }

    private static async Task GrantInstructionEditsAsync(DurabilityFixture fixture, InstructionSetup setup)
    {
        await using var db = fixture.Context();
        foreach (var action in new[] { WorkItemActions.UpdateComment, WorkItemActions.DeleteComment })
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = fixture.HumanId,
                ScopeKind = GrantScopeKind.Board, ScopeId = setup.Board, Action = action });
        await db.SaveChangesAsync();
    }

    [MemoryPostgresFact]
    public async Task InstructionCapturePublishesOnlySelectedCaseContextAndDurableEnrichmentAcrossRestart()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        var episode = await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(published.CommentId, 1));
        Assert.NotNull(episode); Assert.Equal(SharedInstruction, episode.Content); Assert.True(MemorySourceIntegrity.IsVerified(episode));
        Assert.Equal(new MemorySource("work-instruction", episode.Id.ToString("D"), f.HumanId.ToString("D")), episode.Source);
        var serialized = System.Text.Json.JsonSerializer.Serialize(episode);
        Assert.DoesNotContain(PrivateInstructionContext, serialized); Assert.DoesNotContain(setup.Selection.ConversationId.ToString("D"), serialized);
        Assert.DoesNotContain(f.MessageId.ToString("D"), serialized);
        await using var db = f.Context();
        var job = await db.MemoryEpisodeEnrichmentJobs.SingleAsync();
        Assert.Equal(episode.Id, job.EpisodeId); Assert.Equal(f.EmployeeId, job.EmployeeId);
        Assert.Equal(setup.User, job.ReviewerApplicationUserId); Assert.Equal(MemoryCaptureStatus.Pending, job.Status);
        Assert.DoesNotContain(PrivateInstructionContext, job.SourceJson);
        var found = await f.Store.SearchAsync(new(ns.Partition, ns.Scope, SharedInstruction));
        Assert.Contains(found, x => x.Id == episode.Id);
        // This is the existing durable worker/provider path, not a second instruction extractor.
        var provider = new InstructionCaptureProvider();
        Assert.Equal(1, await f.Service(db, provider).ProcessPendingAsync());
        Assert.Contains(SharedInstruction, provider.Context); Assert.DoesNotContain(PrivateInstructionContext, provider.Context);
        Assert.DoesNotContain("Unrelated personal information", provider.Context);
        Assert.Equal(MemoryCaptureStatus.Completed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.NotEmpty(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        var claim = Assert.Single((await f.Store.ExportAsync(ns.Partition)).Claims);
        Assert.Contains(episode.Id, claim.SourceEpisodeIds!); Assert.Equal(MemorySensitivity.Internal, claim.Sensitivity);
        Assert.Equal(MemoryTrustTier.AgentInference, claim.Trust);
    }

    [MemoryPostgresFact]
    public async Task InstructionCaptureEditAtomicallySuppressesOldMemoryAndStagesCurrentRevision()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        await GrantInstructionEditsAsync(f, setup);
        await using var db = f.Context();
        const string changed = "Use the revised approved budget of $1250.";
        var response = await InstructionComments(db).UpdateCommentAsync(f.OrganizationId, setup.Board, setup.Item,
            published.CommentId, setup.User, new(changed, 1, "instruction-edit"));
        Assert.NotNull(response);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        Assert.True((await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(published.CommentId, 1)))!.IsSuppressed);
        var current = await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(published.CommentId, 2));
        Assert.NotNull(current); Assert.Equal(changed, current.Content); Assert.False(current.IsSuppressed);
        Assert.Contains(await f.Store.SearchAsync(new(ns.Partition, ns.Scope, "1250")), x => x.Id == current.Id);
        Assert.DoesNotContain(await f.Store.SearchAsync(new(ns.Partition, ns.Scope, SharedInstruction)), x => x.Id == WorkInstructionMemorySource.EpisodeId(published.CommentId, 1));
        await using var reconnect = f.Context();
        await InstructionComments(reconnect).UpdateCommentAsync(f.OrganizationId, setup.Board, setup.Item, published.CommentId,
            setup.User, new(changed, 1, "instruction-edit"));
        Assert.Equal(2, await reconnect.MemoryEpisodeEnrichmentJobs.CountAsync());
        var audit = Assert.Single(await reconnect.AuditOutbox.Where(x => x.RequestJson.Contains("work.instruction.changed.v1")).ToListAsync());
        Assert.DoesNotContain(changed, audit.RequestJson); Assert.DoesNotContain(PrivateInstructionContext, audit.RequestJson);
        Assert.Equal(1, await f.Service(reconnect, new UsageProviderFactory()).ProcessPendingAsync());
        Assert.Equal(MemoryCaptureStatus.Failed, (await reconnect.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.EpisodeId == WorkInstructionMemorySource.EpisodeId(published.CommentId, 1))).Status);
        Assert.Equal(MemoryCaptureStatus.Completed, (await reconnect.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.EpisodeId == current.Id)).Status);
    }

    [MemoryPostgresTheory]
    [InlineData("edit")]
    [InlineData("withdraw")]
    [InlineData("hard-delete")]
    public async Task InstructionCaptureCurrentSourceChangeFencesPreviouslyReadContext(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        await GrantInstructionEditsAsync(f, setup);
        var scoped = new ScopedAudienceFixture(setup.User, default!, setup.Selection.ConversationId, setup.Item);
        // Use the actual current canonical work-item coordination consumer and all its recipients.
        var (session, work) = await SeedBrokerReadLeaseAsync(f);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        var source = await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(published.CommentId, 1));
        scoped = scoped with { Episode = source! };
        await SeedScopedCoordinationAsync(f, scoped, work.Id);
        await using var db = f.Context();
        var read = await ReadHandler(f, db).HandleAsync(session, ReadRequest("search", new MemorySearchRequest(ns.Partition, ns.Scope, SharedInstruction)), default);
        Assert.True(read.Succeeded, read.Error); Assert.Contains(SharedInstruction, read.Payload.ToStringUtf8());
        if (change == "edit") await InstructionComments(db).UpdateCommentAsync(f.OrganizationId, setup.Board, setup.Item, published.CommentId,
            setup.User, new("Use the new instructions instead.", 1, "instruction-replace"));
        else if (change == "withdraw") await InstructionComments(db).DeleteCommentAsync(f.OrganizationId, setup.Board, setup.Item,
            published.CommentId, setup.User, new(1, "instruction-withdraw"));
        else await db.WorkItemComments.Where(x => x.Id == published.CommentId).ExecuteDeleteAsync();
        await using var dispatch = f.Context();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(dispatch).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await dispatch.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.DoesNotContain(await f.Store.SearchAsync(new(ns.Partition, ns.Scope, SharedInstruction)), x => x.Id == source!.Id);
    }

    [MemoryPostgresFact]
    public async Task InstructionCaptureFailureRollsBackCommentMemoryJobsAndAllWakes()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f);
        await using var db = f.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION instruction_test_fail_job() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test job failure'; END $$;
            CREATE TRIGGER instruction_test_fail_job BEFORE INSERT ON "MemoryEpisodeEnrichmentJobs"
                FOR EACH ROW EXECUTE FUNCTION instruction_test_fail_job();
            """);
        var service = InstructionService(db, f.Store);
        var preview = await service.PreviewAsync(f.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.PublishAsync(f.OrganizationId, setup.Board, setup.Item, setup.User,
            new(Guid.NewGuid(), setup.Selection, preview.ReviewToken)));
        await using var check = f.Context();
        Assert.Empty(await check.WorkItemComments.ToListAsync()); Assert.Empty(await check.WorkInstructionPublications.ToListAsync());
        Assert.Empty(await check.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await check.AgentPlatformEventOutbox.ToListAsync());
        Assert.Empty(await check.ApplicationRealtimeOutbox.Where(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged).ToListAsync());
        var count = await check.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM csweet_memory_episodes WHERE payload->'source'->>'type'='work-instruction'").SingleAsync();
        Assert.Equal(0, count);
    }

    [MemoryPostgresTheory]
    [InlineData("edit")]
    [InlineData("withdraw")]
    [InlineData("revoke")]
    public async Task InstructionCaptureInFlightEnrichmentRejectsChangedSourceOrAuthority(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        await GrantInstructionEditsAsync(f, setup);
        await using var worker = f.Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProviderFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        var run = f.Service(worker, provider).ProcessPendingAsync(limit: 1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var writer = f.Context();
            if (change == "edit") await InstructionComments(writer).UpdateCommentAsync(f.OrganizationId, setup.Board, setup.Item,
                published.CommentId, setup.User, new("Only the revised direction applies.", 1, "in-flight-edit"));
            else if (change == "withdraw") await InstructionComments(writer).DeleteCommentAsync(f.OrganizationId, setup.Board, setup.Item,
                published.CommentId, setup.User, new(1, "in-flight-withdraw"));
            else await writer.ScopedActionGrants.Where(x => x.SubjectId == f.InstallationId && x.ScopeId == setup.Item &&
                x.Action == WorkItemActions.ReadComments).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await run); Assert.Equal(1, provider.Calls);
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        Assert.Empty((await f.Store.ExportAsync(ns.Partition)).Claims);
        var oldJob = await worker.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.EpisodeId == WorkInstructionMemorySource.EpisodeId(published.CommentId, 1));
        Assert.Equal(MemoryCaptureStatus.Failed, oldJob.Status); Assert.Null(oldJob.AcceptedExtractionJson);
        Assert.Single(await worker.MemoryEpisodeExtractionReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InstructionCaptureForgettingClearsJobAndRecallWithoutRecreatingPublishedSource(bool completed, bool withdrawn)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f);
        await using var db = f.Context(); var service = InstructionService(db, f.Store);
        var preview = await service.PreviewAsync(f.OrganizationId, setup.Board, setup.Item, setup.User, setup.Selection);
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        var published = await service.PublishAsync(f.OrganizationId, setup.Board, setup.Item, setup.User, request);
        var id = WorkInstructionMemorySource.EpisodeId(published.CommentId, 1);
        if (completed) Assert.Equal(1, await f.Service(db, new UsageProviderFactory()).ProcessPendingAsync());
        if (withdrawn)
        {
            await GrantInstructionEditsAsync(f, setup);
            await InstructionComments(db).DeleteCommentAsync(f.OrganizationId, setup.Board, setup.Item, published.CommentId,
                setup.User, new(1, "withdraw-before-forgetting"));
        }
        db.ChangeTracker.Clear();
        var review = ErasureService(f, db);
        var impact = await review.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, id, setup.User);
        Assert.Null(impact.ApplyBlockedReason); Assert.Equal(1, impact.Execution!.ExtractionJobs);
        var erased = await review.EraseSourceAsync(f.OrganizationId, f.EmployeeId, id, setup.User,
            new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(impact.EvidenceToken)));
        Assert.Equal("completed", erased.Status);
        await using var reconnect = f.Context();
        Assert.Empty(await reconnect.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await reconnect.MemoryEpisodeExtractionReceipts.ToListAsync());
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        Assert.Null(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, id));
        Assert.True((await InstructionService(reconnect, f.Store).PublishAsync(f.OrganizationId, setup.Board, setup.Item, setup.User, request)).Replayed);
        Assert.Equal(0, await f.Service(reconnect, new UsageProviderFactory()).ProcessPendingAsync());
        Assert.Null(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, id));
        Assert.Equal(SharedInstruction, (await reconnect.WorkItemComments.SingleAsync()).Body);
        Assert.Equal(withdrawn, (await reconnect.WorkItemComments.SingleAsync()).DeletedAt is not null);
        Assert.Single(await reconnect.WorkInstructionPublications.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("revision")]
    [InlineData("restore")]
    [InlineData("truncate")]
    [InlineData("downgrade")]
    public async Task InstructionCaptureLifecycleGuardsPreventUnobservedRestoration(string mutation)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        await using var db = f.Context();
        if (mutation == "restore") await db.WorkItemComments.Where(x => x.Id == published.CommentId).ExecuteUpdateAsync(x =>
            x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow).SetProperty(c => c.Revision, 2));
        var sql = mutation switch
        {
            "revision" => $"UPDATE \"WorkItemComments\" SET \"Revision\"=0 WHERE \"Id\"='{published.CommentId:D}'",
            "restore" => $"UPDATE \"WorkItemComments\" SET \"DeletedAt\"=NULL,\"Revision\"=3 WHERE \"Id\"='{published.CommentId:D}'",
            "truncate" => "TRUNCATE \"WorkItemComments\"",
            _ => string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(db.GetService<IMigrationsAssembly>().CreateMigration(
                db.GetService<IMigrationsAssembly>().Migrations.Single(x => x.Key.EndsWith("_WorkInstructionMemoryLifecycle", StringComparison.Ordinal)).Value,
                db.Database.ProviderName!).DownOperations).Select(x => x.CommandText))
        };
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Single(await db.WorkInstructionPublications.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [MemoryPostgresFact]
    public async Task InstructionCaptureEmptyLifecycleMigrationCanRoundTripBeforePublication()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f);
        await using var db = f.Context();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(x =>
            x.Key.EndsWith("_WorkInstructionMemoryLifecycle", StringComparison.Ordinal)).Value, db.Database.ProviderName!);
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await transaction.CommitAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(setup.Item, (await db.CoreWorkTasks.SingleAsync()).Id);
    }

    [MemoryPostgresFact]
    public async Task InstructionCaptureFailedEditRollsBackSuppressionCommentAndNotificationOutboxes()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(f); var published = await PublishInstructionAsync(f, setup);
        await GrantInstructionEditsAsync(f, setup);
        await using var db = f.Context();
        var beforeAudit = await db.AuditOutbox.CountAsync(); var beforeHuman = await db.ApplicationRealtimeOutbox.CountAsync();
        var beforeAgent = await db.AgentPlatformEventOutbox.CountAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION instruction_test_fail_edit_job() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test edit job failure'; END $$;
            CREATE TRIGGER instruction_test_fail_edit_job BEFORE INSERT ON "MemoryEpisodeEnrichmentJobs"
                FOR EACH ROW EXECUTE FUNCTION instruction_test_fail_edit_job();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => InstructionComments(db).UpdateCommentAsync(f.OrganizationId, setup.Board, setup.Item,
            published.CommentId, setup.User, new("This must roll back.", 1, "failed-instruction-edit")));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var current = f.Context();
        var comment = await current.WorkItemComments.SingleAsync(); Assert.Equal(1, comment.Revision); Assert.Equal(SharedInstruction, comment.Body);
        Assert.Single(await current.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Equal(beforeAudit, await current.AuditOutbox.CountAsync());
        Assert.Equal(beforeHuman, await current.ApplicationRealtimeOutbox.CountAsync()); Assert.Equal(beforeAgent, await current.AgentPlatformEventOutbox.CountAsync());
        var ns = EmployeeMemoryNamespaces.Case(f.OrganizationId.ToString("D"), setup.Item.ToString("D"), "csweet");
        Assert.False((await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(comment.Id, 1)))!.IsSuppressed);
        Assert.Null(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(ns.Partition, WorkInstructionMemorySource.EpisodeId(comment.Id, 2)));
    }
}

