using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.AgentHost.Broker;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task SharedTransferRetryAndRetentionReplaysRequireCurrentMembership(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context();
        var copy = await ApplySharedAsync(new(db, fixture.Store, TimeProvider.System), fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice handoff", SourceAudienceId: audience));
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.Attempts, 10));
        var job = await db.MemoryEpisodeEnrichmentJobs.Select(x => x.Id).SingleAsync();
        var recovery = EpisodeRecovery(fixture, db); var retry = new RetryMemoryEnrichmentRequest(Guid.NewGuid(), 0);
        Assert.False((await recovery.RetryAsync(fixture.OrganizationId, target, job, user, retry)).Replayed);
        Assert.True((await recovery.RetryAsync(fixture.OrganizationId, target, job, user, retry)).Replayed);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.RetryAsync(fixture.OrganizationId, target, job, user, retry));
        if (kind == "Team") await db.TeamMemberships.Where(x => x.TeamId == audience && x.OrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, (DateTimeOffset?)null));
        else await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, audience));
        var review = ErasureService(fixture, db); var id = copy.AppliedEpisodeId!.Value;
        var held = await review.GetHoldAsync(fixture.OrganizationId, target, id, user);
        var place = new ReviewMemoryHoldRequest(Guid.NewGuid(), held.Revision, held.EvidenceToken, true);
        Assert.True((await review.ReviewHoldAsync(fixture.OrganizationId, target, id, user, place)).LegalHold);
        Assert.Equal("memory_legal_hold_prevents_deletion", (await review.GetErasureImpactAsync(fixture.OrganizationId, target, id, user)).ApplyBlockedReason);
        var suppressed = await review.GetSuppressionAsync(fixture.OrganizationId, target, id, user);
        var suppress = new SuppressMemorySourceRequest(Guid.NewGuid(), suppressed.Revision, suppressed.EvidenceToken);
        await review.SuppressSourceAsync(fixture.OrganizationId, target, id, user, suppress);
        Assert.True((await review.SuppressSourceAsync(fixture.OrganizationId, target, id, user, suppress)).WasReplay);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RetryAsync(fixture.OrganizationId, target, job, user, retry));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewHoldAsync(fixture.OrganizationId, target, id, user, place));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.SuppressSourceAsync(fixture.OrganizationId, target, id, user, suppress));
        Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public Task SharedTransferErasureKeepsSourceAndReauthorizesRetainedReceipt(string kind) => VerifySharedNotesOnlyErasureAsync(kind, null);

    [MemoryPostgresTheory]
    [InlineData("Team", "suppressed")]
    [InlineData("Role", "suppressed")]
    [InlineData("Team", "expired")]
    [InlineData("Role", "expired")]
    [InlineData("Team", "revoked")]
    [InlineData("Role", "revoked")]
    public Task SharedNotesOnlyErasureAfterLifecycleChangePreservesMembershipAndNestedClosure(string kind, string state) =>
        VerifySharedNotesOnlyErasureAsync(kind, state);

    private static async Task VerifySharedNotesOnlyErasureAsync(string kind, string? state)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context();
        // This fixture seeds an unrelated synthetic conversation extraction. Keep its
        // outbox content-free so the forgetting scan does not encounter legacy lineage.
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.AcceptedExtractionJson, (string?)null)
            .SetProperty(j => j.ExtractionAcceptedAt, (DateTimeOffset?)null).SetProperty(j => j.LastError, (string?)null)
            .SetProperty(j => j.Status, MemoryCaptureStatus.Completed));
        var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var copy = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [], "Alice handoff", SourceAudienceId: audience));
        db.ChangeTracker.Clear();
        var id = copy.AppliedEpisodeId!.Value; var review = ErasureService(fixture, db);
        Guid? nestedId = null; Guid? third = null;
        if (state is not null)
        {
            third = await AddSharedRecipientAsync(fixture, kind, audience);
            var nested = await ApplySharedAsync(transfers, fixture, target, user,
                new(Guid.NewGuid(), third.Value, "Employee", [new("Episode", id)], "Nested Alice handoff"));
            nestedId = nested.AppliedEpisodeId; db.ChangeTracker.Clear();
            var held = await review.GetHoldAsync(fixture.OrganizationId, target, id, user);
            await review.ReviewHoldAsync(fixture.OrganizationId, target, id, user, new(Guid.NewGuid(), held.Revision, held.EvidenceToken, true));
            if (state == "suppressed")
            {
                var suppress = await review.GetSuppressionAsync(fixture.OrganizationId, target, id, user);
                await review.SuppressSourceAsync(fixture.OrganizationId, target, id, user, new(Guid.NewGuid(), suppress.Revision, suppress.EvidenceToken));
            }
            else if (state == "expired") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET expires_at='2000-01-01',payload=jsonb_set(payload,ARRAY['expiresAt'],'\"2000-01-01T00:00:00Z\"'::jsonb) WHERE id={id}");
            else
            {
                var transferReview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, copy.PackageId, user);
                await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, copy.PackageId, user, new(Guid.NewGuid(), transferReview.ReviewToken, "reject"));
            }
            Assert.Equal("memory_legal_hold_prevents_deletion", (await review.GetErasureImpactAsync(fixture.OrganizationId, target, id, user)).ApplyBlockedReason);
            held = await review.GetHoldAsync(fixture.OrganizationId, target, id, user); Assert.True(held.CanRelease);
            await review.ReviewHoldAsync(fixture.OrganizationId, target, id, user, new(Guid.NewGuid(), held.Revision, held.EvidenceToken, false));
            db.ChangeTracker.Clear();
        }
        var preview = await review.GetErasureImpactAsync(fixture.OrganizationId, target, id, user);
        Assert.Null(preview.ApplyBlockedReason);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        Assert.Equal("completed", (await review.EraseSourceAsync(fixture.OrganizationId, target, id, user, request)).Status);
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(source.Partition, source.Id));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(TransferTarget(fixture, target), id));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        if (third is not null) Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(TransferTarget(fixture, third.Value), nestedId!.Value));
        await review.EraseSourceAsync(fixture.OrganizationId, target, id, user, request);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.EraseSourceAsync(fixture.OrganizationId, target, id, user, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetErasureStatusAsync(fixture.OrganizationId, target, request.OperationId, user));
    }

    private static async Task<Guid> AddSharedRecipientAsync(DurabilityFixture fixture, string kind, Guid audience)
    {
        await using var db = fixture.Context();
        var original = await db.AgentInstallations.SingleAsync(x => x.Id == fixture.InstallationId);
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), BusinessId = fixture.OrganizationId.ToString("D"),
            PackageVersionId = original.PackageVersionId, IsEnabled = true, SetupState = PluginSetupState.Ready };
        var target = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Agent,
            AgentInstallation = installation, AgentInstallationId = installation.Id, ReportsToOrganizationUserId = fixture.HumanId,
            RoleId = kind == "Role" ? audience : null };
        db.CoreOrganizationUsers.Add(target);
        db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            AgentOrganizationUserId = target.Id, InitiatedByOrganizationUserId = fixture.HumanId, Kind = ConversationKind.DirectHumanAgent });
        if (kind == "Team") db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            TeamId = audience, OrganizationUserId = target.Id, ExclusiveAgentEmployeeId = target.Id, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        return target.Id;
    }

    private static async Task<MemoryTransferResult> ApplySharedAsync(AgentMemoryTransferService transfers, DurabilityFixture fixture,
        Guid owner, Guid user, PrepareMemoryTransferRequest request)
    {
        var draft = await transfers.PrepareAsync(fixture.OrganizationId, owner, user, request);
        Assert.True((await transfers.PrepareAsync(fixture.OrganizationId, owner, user, request)).WasReplay);
        var preview = await transfers.GetAsync(fixture.OrganizationId, owner, draft.PackageId, user);
        Assert.True(preview.CanApprove);
        await transfers.TransitionAsync(fixture.OrganizationId, owner, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfers.GetAsync(fixture.OrganizationId, owner, draft.PackageId, user);
        Assert.True(preview.CanApply);
        return await transfers.TransitionAsync(fixture.OrganizationId, owner, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
    }

    [MemoryPostgresTheory]
    [InlineData("Team", false)]
    [InlineData("Team", true)]
    [InlineData("Role", false)]
    [InlineData("Role", true)]
    public async Task SharedTransferAndNestedCopyRetainRestrictionsAcrossInspectionReviewRecallAndReplay(string kind, bool notesOnly)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience);
        var third = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context();
        var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        Assert.Contains(await transfers.ListAudiencesAsync(fixture.OrganizationId, fixture.EmployeeId, target, user), x => x.Scope == kind && x.AudienceId == audience);
        var request = new PrepareMemoryTransferRequest(Guid.NewGuid(), target, kind,
            notesOnly ? [] : [new("Episode", source.Id)], "Alice uses concise replies.", SourceAudienceId: audience);
        var applied = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user, request);
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), target.ToString("D"), "csweet").Partition;
        var copy = (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(partition, applied.AppliedEpisodeId!.Value))!;
        Assert.Equal(partition, copy.Partition); Assert.Equal(source.Partition, Assert.Single(copy.TransferEvidence!.RequiredSharedPartitions!));
        var nestedRequest = new PrepareMemoryTransferRequest(Guid.NewGuid(), third, "Employee", [new("Episode", copy.Id)], "Alice handoff");
        var nested = await ApplySharedAsync(transfers, fixture, target, user, nestedRequest);
        var thirdPartition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), third.ToString("D"), "csweet").Partition;
        Assert.Equal(source.Partition, Assert.Single((await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(thirdPartition, nested.AppliedEpisodeId!.Value))!.TransferEvidence!.RequiredSharedPartitions!));
        var service = fixture.Service(db, new UsageProviderFactory());
        Assert.Equal(audience, (await service.GetItemAsync(fixture.OrganizationId, fixture.EmployeeId, source.Id, applicationUserId: user))!.AudienceId);
        Assert.NotNull(await service.GetItemAsync(fixture.OrganizationId, target, copy.Id, applicationUserId: user));
        Assert.Null(await service.GetItemAsync(fixture.OrganizationId, target, copy.Id));
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        Assert.Equal(kind, (await review.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", source.Id, user)).Scope);
        Assert.NotEmpty((await review.ReadHistoryAsync(fixture.OrganizationId, target, "Episode", copy.Id, user)).Items);
        var hold = await review.GetHoldAsync(fixture.OrganizationId, target, copy.Id, user); Assert.True(hold.IsTransferred);
        var read = await new MemoryRecallDispatchEvidence(db).CaptureReadAsync(copy, partition, default);
        Assert.Contains(source.Partition, read!.Partitions);
        var conversation = await db.CoreConversations.SingleAsync(x => x.AgentOrganizationUserId == target);
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderOrganizationUserId = fixture.HumanId,
            Content = "What does Alice use?", Role = ConversationRole.User, CreatedAt = DateTimeOffset.UtcNow };
        db.CoreConversationMessages.Add(message);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id, UserMessageId = message.Id,
            TargetAgentOrganizationUserId = target, Attempt = 1, Status = ChatTurnStatus.RecallingMemory, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChatTurns.Add(turn); await db.SaveChangesAsync();
        var recall = await service.PrepareTurnRecallAsync(turn.Id); Assert.Contains("Alice", recall.Context);
        var installation = (await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == target)).AgentInstallationId!.Value;
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId.ToString("D"), AgentInstallationId = installation,
            SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), PayloadHash = "shared-work",
            MemoryRecallReceiptJson = MemoryRecallDispatchEvidence.BindPayload(recall.ReceiptJson, "shared-work", fixture.OrganizationId.ToString("D"), installation, "chat-turn", turn.Id.ToString("D")) };
        var guard = new MemoryRecallDispatchEvidence(db); await guard.AuthorizeWorkAsync(work, target.ToString("D"), default);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, target.ToString("D"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.PrepareAsync(fixture.OrganizationId, target, user, nestedRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReadHistoryAsync(fixture.OrganizationId, target, "Episode", copy.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, target, copy.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetSuppressionAsync(fixture.OrganizationId, target, copy.Id, user));
        Assert.Null(await service.GetItemAsync(fixture.OrganizationId, target, copy.Id, applicationUserId: user));
        Assert.Null(await service.GetItemAsync(fixture.OrganizationId, third, nested.AppliedEpisodeId.Value, applicationUserId: user));
        Assert.Null((await service.PrepareTurnRecallAsync(turn.Id)).Context);
        Assert.Equal(2, await db.MemoryEpisodeEnrichmentJobs.CountAsync()); Assert.Equal(6, await db.MemoryTransferReceipts.CountAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "approve")]
    [InlineData("Role", "approve")]
    [InlineData("Team", "apply")]
    [InlineData("Role", "apply")]
    [InlineData("Team", "hold")]
    [InlineData("Role", "hold")]
    public async Task SharedTransferPreviewsRejectChangedAndRestoredMembership(string kind, string action)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind); var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice handoff", SourceAudienceId: audience));
        var preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        if (action != "approve")
        {
            await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
            preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        }
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System); MemoryHoldPreview? held = null;
        if (action == "hold")
        {
            var copy = await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
            held = await review.GetHoldAsync(fixture.OrganizationId, target, copy.AppliedEpisodeId!.Value, user);
        }
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        if (kind == "Team") await db.TeamMemberships.Where(x => x.TeamId == audience && x.OrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, (DateTimeOffset?)null));
        else await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, audience));
        if (held is not null) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewHoldAsync(fixture.OrganizationId, target, held.EpisodeId, user,
            new(Guid.NewGuid(), held.Revision, held.EvidenceToken, true)));
        else await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user,
            new(Guid.NewGuid(), preview.ReviewToken, action)));
    }

    [MemoryPostgresTheory]
    [InlineData("Team", false)]
    [InlineData("Role", false)]
    [InlineData("Team", true)]
    [InlineData("Role", true)]
    public async Task SharedTransferWorkerRechecksMembershipBeforeDispatchAndCommit(string kind, bool duringExtraction)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind); var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var copy = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice handoff", SourceAudienceId: audience));
        async Task Revoke() { await using var changed = fixture.Context(); await RevokeOperatorAudienceAsync(fixture, changed, audience, kind == "Team" ? "actor-ended" : "actor-role"); }
        if (!duringExtraction) await Revoke();
        var provider = new ScriptedProviderFactory(async (_, _) => { if (duringExtraction) await Revoke(); });
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync());
        Assert.Equal(duringExtraction ? 1 : 0, provider.Calls);
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(duringExtraction ? 1 : 0, await db.MemoryEpisodeExtractionReceipts.CountAsync());
        Assert.Empty((await fixture.Store.ExportAsync(EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), target.ToString("D"), "csweet").Partition)).Claims);
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "missing")]
    [InlineData("Role", "missing")]
    [InlineData("Team", "foreign")]
    [InlineData("Role", "foreign")]
    [InlineData("Team", "native-extra")]
    [InlineData("Role", "native-extra")]
    [InlineData("Team", "recipient")]
    [InlineData("Role", "recipient")]
    public async Task SharedTransferRejectsAmbiguousAudienceOrUnauthorizedRecipientWithoutSideEffects(string kind, string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind); var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var request = new PrepareMemoryTransferRequest(Guid.NewGuid(), target, defect == "native-extra" ? "Employee" : kind,
            [new("Episode", source.Id)], "notes", SourceAudienceId: defect == "missing" ? null : defect == "foreign" ? Guid.NewGuid() : audience);
        if (defect == "recipient")
        {
            if (kind == "Team") await db.TeamMemberships.Where(x => x.OrganizationUserId == target).ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, DateTimeOffset.UtcNow));
            else await db.CoreOrganizationUsers.Where(x => x.Id == target).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, (Guid?)null));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, request));
            Assert.Empty(await transfers.ListAudiencesAsync(fixture.OrganizationId, fixture.EmployeeId, target, user));
        }
        else if (defect == "foreign") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, request));
        else await Assert.ThrowsAsync<ArgumentException>(() => transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, request));
        Assert.Empty(await db.MemoryTransferReceipts.ToArrayAsync()); Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToArrayAsync());
        Assert.Empty(await db.AuditOutbox.Where(x => x.SourceEntityType == "MemoryTransfer").ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task SharedCopyDerivativesRequireCurrentAudienceForReviewHistoryAndInspection(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind); var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var applied = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice handoff", SourceAudienceId: audience));
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), target.ToString("D"), "csweet").Partition;
        var now = DateTimeOffset.UtcNow; var copy = applied.AppliedEpisodeId!.Value;
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now) { SourceEpisodeIds = [copy], Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(entity);
        var sharedEntity = entity with { Id = Guid.NewGuid(), Partition = source.Partition, SourceEpisodeIds = [source.Id] };
        await fixture.Store.UpsertEntityAsync(sharedEntity);
        var inspector = fixture.Service(db, new UsageProviderFactory());
        Assert.Contains((await inspector.GetGraphAsync(fixture.OrganizationId, target, null, null, applicationUserId: user))!.Nodes,
            x => x.Id == sharedEntity.Id && x.Scope == kind && x.AudienceId == audience);
        var claim = new MemoryClaim(Guid.NewGuid(), partition, copy, entity.Id, "prefers", null, "concise replies",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now) { SourceEpisodeIds = [copy] };
        await fixture.Store.WriteClaimAsync(claim);
        var block = new MemoryBlock(Guid.NewGuid(), partition, "Preferences", "Alice prefers concise replies", 1, 100, true, MemoryTrustTier.AgentInference, now)
            { SourceEpisodeIds = [copy], Sensitivity = MemorySensitivity.Personal, Confirmation = MemoryConfirmationState.Pending };
        await fixture.Store.WriteBlockAsync(block);
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, target, claim.Id, user); Assert.True(preview.CanConfirm); Assert.False(preview.CanCorrect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ReviewClaimAsync(fixture.OrganizationId, target, claim.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "new value")));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        await review.ReviewClaimAsync(fixture.OrganizationId, target, claim.Id, user, request);
        var core = await review.GetCoreAsync(fixture.OrganizationId, target, block.Id, user); Assert.False(core.CanCorrect);
        Assert.NotEmpty((await review.ReadHistoryAsync(fixture.OrganizationId, target, "Claim", claim.Id, user)).Items);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetClaimAsync(fixture.OrganizationId, target, claim.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetCoreAsync(fixture.OrganizationId, target, block.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewClaimAsync(fixture.OrganizationId, target, claim.Id, user, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReadHistoryAsync(fixture.OrganizationId, target, "Claim", claim.Id, user));
        Assert.Null(await fixture.Service(db, new UsageProviderFactory()).GetItemAsync(fixture.OrganizationId, target, claim.Id, applicationUserId: user));
        Assert.Empty((await inspector.GetGraphAsync(fixture.OrganizationId, target, null, null, applicationUserId: user))!.Nodes);
        Assert.Single(await db.MemoryReviewReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team", true)]
    [InlineData("Role", true)]
    [InlineData("Team", false)]
    [InlineData("Role", false)]
    public async Task SharedBrokerCopyReadRechecksCurrentAgentAndChatHumanBeforeDeliveryAndDispatch(string kind, bool human)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind); var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var first = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [], "Alice handoff", SourceAudienceId: audience));
        var back = await ApplySharedAsync(transfers, fixture, target, user,
            new(Guid.NewGuid(), fixture.EmployeeId, "Employee", [new("Episode", first.AppliedEpisodeId!.Value)], "Alice nested handoff"));
        var (session, lease) = await SeedBrokerReadLeaseAsync(fixture);
        if (human)
        {
            var conversation = await db.CoreConversations.SingleAsync(x => x.AgentOrganizationUserId == fixture.EmployeeId);
            await db.CoreConversations.Where(x => x.Id == conversation.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.Kind, ConversationKind.DirectHumanAgent));
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderOrganizationUserId = fixture.HumanId, Role = ConversationRole.User,
                Content = "Alice preferences?", CreatedAt = DateTimeOffset.UtcNow };
            var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id, UserMessageId = message.Id,
                TargetAgentOrganizationUserId = fixture.EmployeeId, Attempt = 1, Status = ChatTurnStatus.RecallingMemory, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.CoreConversationMessages.Add(message); db.ChatTurns.Add(turn); await db.SaveChangesAsync();
            await db.AgentWorkItems.Where(x => x.Id == lease.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.SourceType, "chat-turn").SetProperty(j => j.SourceId, turn.Id.ToString("D")));
        }
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"), "csweet").Partition;
        var request = ReadRequest("search", new MemorySearchRequest(partition, MemoryScope.Agent, "Alice"));
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session, request, default)).Succeeded);
        Assert.Contains(kind.ToLowerInvariant() + ":" + audience.ToString("D"), (await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, lease.Id, default);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind == "Team" ? human ? "actor-ended" : "employee-ended" : human ? "actor-role" : "employee-role");
        Assert.False((await ReadHandler(fixture, db).HandleAsync(session, request, default)).Succeeded);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, lease.Id, default));
    }
}
