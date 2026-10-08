using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<MemoryEpisode> PrepareGenericProposalAsync(DurabilityFixture fixture)
    {
        await fixture.Store.InitializeAsync();
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Completed)
            .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow));
        db.AgentInstallationGrants.Add(new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId,
            RequiredCapabilitiesJson = "[\"platform.memory.write.v1\"]" });
        await db.LlmProviderProfiles.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultChatModel, "test-model"));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(DurableEpisodeMemoryEnrichment.InstallTriggers);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        return new(id, fixture.Partition, MemoryScope.User, "My name is Alice.", "text/plain",
            new("agent-proposal", id.ToString("D"), fixture.EmployeeId.ToString("D")),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("My name is Alice."))).ToLowerInvariant(), now, now,
            "agent-proposal:test:" + id.ToString("D"), Metadata: new Dictionary<string,string> {
                ["installationId"] = fixture.InstallationId.ToString("D"), ["employeeId"] = fixture.EmployeeId.ToString("D") },
            Sensitivity: MemorySensitivity.Personal);
    }

    private static Task<MemoryWriteResult> AcceptGenericAsync(DurabilityFixture fixture, Infrastructure.Persistence.CSweetDbContext db,
        MemoryEpisode episode) => fixture.Service(db, new UsageProviderFactory()).AcceptProposalAsync(fixture.OrganizationId,
            fixture.EmployeeId, fixture.InstallationId, episode);

    [MemoryPostgresFact]
    public async Task GenericProposalAcceptanceQueuesAndEnrichesOnceWithPendingEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        var first = await AcceptGenericAsync(fixture, db, episode);
        var replay = await AcceptGenericAsync(fixture, db, episode with { RecordedAt = DateTimeOffset.UtcNow });
        Assert.True(first.Created); Assert.False(replay.Created);
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        await AcceptGenericAsync(fixture, db, episode);
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        var export = await fixture.Store.ExportAsync(fixture.Partition);
        var claim = Assert.Single(export.Claims);
        Assert.Equal(MemoryTrustTier.AgentInference, claim.Trust);
        Assert.Equal(MemoryConfirmationState.Pending, claim.Confirmation);
        Assert.Equal(MemorySensitivity.Personal, claim.Sensitivity);
        Assert.Equal(episode.Id, Assert.Single(claim.SourceEpisodeIds));
        Assert.Single(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Equal(MemoryCaptureStatus.Completed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.All(await db.AgentRunLogs.ToListAsync(), x => { Assert.Equal(fixture.OrganizationId, x.OrganizationId); Assert.Equal(fixture.InstallationId, x.AgentInstallationId); });
        // Episode idempotency keys keep the first input, including the broker's existing
        // changed-content retry behavior. Neither source nor completed job is replaced.
        Assert.False((await AcceptGenericAsync(fixture, db, episode with { Content = "different", Checksum = "different" })).Created);
        Assert.Equal(episode.Content, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).Content);
    }

    [MemoryPostgresFact]
    public async Task GenericAcceptanceFailureRollsBackEpisodeAndHistoryAndCanRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_generic_job() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_generic_job BEFORE INSERT ON "MemoryEpisodeEnrichmentJobs" FOR EACH ROW EXECUTE FUNCTION fail_generic_job();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => AcceptGenericAsync(fixture, db, episode));
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM csweet_memory_revisions").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_generic_job ON \"MemoryEpisodeEnrichmentJobs\"; DROP FUNCTION fail_generic_job();");
        Assert.True((await AcceptGenericAsync(fixture, db, episode)).Created);
    }

    [MemoryPostgresFact]
    public async Task GenericAcceptedOutputReplaysAfterAtomicApplicationRollbackAndRestart()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_generic_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_generic_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION fail_generic_claim();
            """);
        var factory = new ScriptedProviderFactory((call, _) => call == 1 ? Task.CompletedTask : throw new InvalidOperationException("Do not extract twice"));
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.NotNull(job.AcceptedExtractionJson); Assert.Equal(MemoryCaptureStatus.Pending, job.Status);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Entities);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_generic_claim ON csweet_memory_claims; DROP FUNCTION fail_generic_claim();");
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await using var restarted = fixture.Context();
        Assert.Equal(1, await fixture.Service(restarted, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        Assert.Equal(job.AcceptedExtractionJson, (await restarted.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [MemoryPostgresTheory]
    [InlineData("installation")]
    [InlineData("employee")]
    [InlineData("relationship")]
    [InlineData("grant")]
    [InlineData("suppressed")]
    public async Task GenericIngestionRechecksCurrentAuthorityAndSourceBeforeDispatch(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        switch (change)
        {
            case "installation": await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(j => j.IsEnabled, false)); break;
            case "employee": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.IsActive, false)); break;
            case "relationship": await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(j => j.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "grant": await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]")); break;
            case "suppressed": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{isSuppressed}}','true') WHERE id={episode.Id}"); break;
        }
        var factory = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("No provider dispatch"));
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(0, factory.Calls);
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task GenericLeaseFencesConcurrentAndExpiredWorkersBeforeOutputAcceptance()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        await AcceptGenericAsync(fixture, first, episode);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (call, ct) => { if (call == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); } });
        var obsolete = fixture.Service(first, factory).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, await fixture.Service(second, factory).ProcessPendingAsync());
            await second.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            Assert.Equal(1, await fixture.Service(second, factory).ProcessPendingAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await obsolete);
        Assert.Equal(2, factory.Calls);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Equal(2, await second.MemoryEpisodeExtractionReceipts.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task GenericInFlightRevocationRetainsInputReceiptButRejectsLateResult()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        await AcceptGenericAsync(fixture, first, episode);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        var processing = fixture.Service(first, factory).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await second.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]"));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await processing);
        var job = await second.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Null(job.AcceptedExtractionJson); Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        Assert.Single(await second.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        await Assert.ThrowsAsync<PostgresException>(() => second.Database.ExecuteSqlRawAsync("UPDATE \"MemoryEpisodeExtractionReceipts\" SET \"SourceHash\"='changed'"));
        var replacement = "{}";
        await Assert.ThrowsAsync<PostgresException>(() => second.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MemoryEpisodeEnrichmentJobs\" SET \"SourceJson\"={replacement}"));
    }

    [MemoryPostgresFact]
    public async Task AppliedTransferQueuesAndEnrichesWithoutChangingItsCertificateOrApproval()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Completed)
            .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow));
        await db.LlmProviderProfiles.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultChatModel, "test-model"));
        await db.Database.ExecuteSqlRawAsync(DurableEpisodeMemoryEnrichment.InstallTriggers);
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        var request = new CSweet.Contracts.Memory.TransitionMemoryTransferRequest(Guid.NewGuid(), preview.ReviewToken, "apply");
        var applied = await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request);
        var copy = await RetainedEpisodeAsync(fixture, applied.AppliedEpisodeId!.Value);
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        var erase = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor);
        Assert.Equal("memory_erasure_source_review_required", erase.ApplyBlockedReason);
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(JsonSerializer.Serialize(copy), JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, copy.Id)));
        var targetClaim = Assert.Single((await fixture.Store.ExportAsync(TransferTarget(fixture, target))).Claims);
        Assert.Equal(copy.Id, targetClaim.EpisodeId); Assert.Equal(MemoryConfirmationState.Pending, targetClaim.Confirmation);
        Assert.True((await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, request)).WasReplay);
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        erase = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor);
        Assert.Equal("memory_erasure_source_review_required", erase.ApplyBlockedReason);
    }

    [MemoryPostgresTheory]
    [InlineData("origin-manager")]
    [InlineData("source-review")]
    [InlineData("target-human")]
    [InlineData("origin-installation")]
    public async Task GenericTransferEnrichmentRechecksOriginalEvidenceAndEveryAudience(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Completed));
        await db.Database.ExecuteSqlRawAsync(DurableEpisodeMemoryEnrichment.InstallTriggers);
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
        switch (change)
        {
            case "origin-manager": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null)); break;
            case "source-review": await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected); break;
            case "target-human": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.IsActive, false)); break;
            case "origin-installation": await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(x => x.SetProperty(j => j.IsEnabled, false)); break;
        }
        var factory = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("No dispatch"));
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(0, factory.Calls);
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Empty((await fixture.Store.ExportAsync(TransferTarget(fixture, target))).Claims);
    }

    [MemoryPostgresFact]
    public async Task GenericMigrationPreservesPopulatedConversationStateAndRefusesNonemptyDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await fixture.Service(db, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
        var original = JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, fixture.MessageId));
        var generator = db.GetService<IMigrationsSqlGenerator>();
        var reconciliation = new ReviewedEpisodeReconciliation();
        var reextraction = new ReviewedEpisodeReextraction();
        await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        await db.Database.ExecuteSqlRawAsync(CSweet.Infrastructure.Persistence.EpisodeReextractionGuards.Install);
        foreach (var command in generator.Generate(reextraction.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(reconciliation.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var recovery = new EpisodeMemoryEnrichmentRecovery();
        await db.Database.ExecuteSqlRawAsync(EpisodeMemoryEnrichmentRecovery.InstallTriggers);
        foreach (var command in generator.Generate(recovery.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE "MemoryEpisodeExtractionReceipts";
            DROP TABLE "MemoryEpisodeEnrichmentJobs";
            DROP FUNCTION csweet_episode_receipt_guard();
            DROP FUNCTION csweet_episode_job_guard();
            """);
        var migration = new DurableEpisodeMemoryEnrichment();
        foreach (var command in generator.Generate(migration.UpOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(recovery.UpOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(reconciliation.UpOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(reextraction.UpOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(original, JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, fixture.MessageId)));
        Assert.Equal(MemoryCaptureStatus.Completed, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()).Status);
        await AcceptGenericAsync(fixture, db, episode);
        var down = generator.Generate(migration.DownOperations, db.Model);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(down[0].CommandText));
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        await db.MemoryEpisodeEnrichmentJobs.ExecuteDeleteAsync();
        foreach (var command in generator.Generate(reextraction.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(reconciliation.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(recovery.DownOperations, db.Model)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in down) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(original, JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, fixture.MessageId)));
    }

    [MemoryPostgresFact]
    public async Task GenericProviderReservationDefersConversationWithoutBurningItsAttempt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        await AcceptGenericAsync(fixture, first, episode);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        var processing = fixture.Service(first, factory).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await second.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Pending));
            Assert.Equal(0, await fixture.Service(second, factory).ProcessPendingAsync());
            var deferred = await second.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
            Assert.Equal("memory_provider_busy", deferred.LastError); Assert.Equal(0, deferred.Attempts);
            Assert.Single(await second.MemoryEpisodeExtractionReceipts.ToListAsync());
            Assert.Equal(1, factory.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await processing);
    }

    [MemoryPostgresTheory]
    [InlineData("Employee", false)]
    [InlineData("Organization", false)]
    [InlineData("Role", false)]
    [InlineData("Team", false)]
    [InlineData("Role", true)]
    [InlineData("Team", true)]
    public async Task GenericAudienceProcessingPreservesClassificationAndRejectsRevokedMembership(string kind, bool revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        var audienceId = Guid.NewGuid(); var tenant = fixture.OrganizationId.ToString("D");
        var audience = kind switch
        {
            "Employee" => EmployeeMemoryNamespaces.Employee(tenant, fixture.EmployeeId.ToString("D"), "csweet"),
            "Organization" => EmployeeMemoryNamespaces.Organization(tenant, "csweet"),
            "Role" => EmployeeMemoryNamespaces.Role(tenant, audienceId.ToString("D"), "csweet"),
            _ => EmployeeMemoryNamespaces.Team(tenant, audienceId.ToString("D"), "csweet")
        };
        if (kind == "Role")
        {
            db.CoreRoles.Add(new Role { Id = audienceId, OrganizationId = fixture.OrganizationId, Name = "Designer" });
            await db.SaveChangesAsync();
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, audienceId));
        }
        if (kind == "Team")
        {
            db.OrganizationTeams.Add(new OrganizationTeam { Id = audienceId, OrganizationId = fixture.OrganizationId, Name = "Design", TeamKey = "design",
                NormalizedName = "DESIGN", LeadOrganizationUserId = fixture.HumanId });
            db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, TeamId = audienceId,
                OrganizationUserId = fixture.EmployeeId, ExclusiveAgentEmployeeId = fixture.EmployeeId, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        episode = episode with { Partition = audience.Partition, Scope = audience.Scope };
        await AcceptGenericAsync(fixture, db, episode);
        if (revoke && kind == "Role") await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, (Guid?)null));
        if (revoke && kind == "Team") await db.TeamMemberships.ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, DateTimeOffset.UtcNow));
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(revoke ? 0 : 1, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(revoke ? 0 : 1, factory.Calls);
        var claims = (await fixture.Store.ExportAsync(audience.Partition)).Claims;
        if (revoke) Assert.Empty(claims);
        else
        {
            var claim = Assert.Single(claims);
            Assert.Equal(MemorySensitivity.Personal, claim.Sensitivity);
            Assert.Equal(MemoryConfirmationState.Pending, claim.Confirmation);
            if (kind != "Employee") Assert.DoesNotContain(await fixture.Store.SearchAsync(new(audience.Partition, audience.Scope, "Alice")),
                x => MemoryRecallPolicy.IsEligible(x, MemoryRecallPolicy.MaximumSensitivity(audience.Partition), DateTimeOffset.UtcNow));
        }
    }

    [MemoryPostgresFact]
    public async Task GenericInputRevisionCannotBeRestoredByReturningPayloadToItsEarlierValues()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['sensitivity'],to_jsonb({(int)MemorySensitivity.Restricted})) WHERE id={episode.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['sensitivity'],to_jsonb({(int)MemorySensitivity.Personal})) WHERE id={episode.Id}");
        var factory = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("No stale dispatch"));
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync()); Assert.Equal(0, factory.Calls);
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
    }
}
