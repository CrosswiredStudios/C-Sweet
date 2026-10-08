using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team", "jobless")]
    [InlineData("Team", "pending")]
    [InlineData("Team", "completed")]
    [InlineData("Role", "jobless")]
    [InlineData("Role", "pending")]
    [InlineData("Role", "completed")]
    [InlineData("InstallationEmployee", "jobless")]
    [InlineData("InstallationEmployee", "pending")]
    [InlineData("InstallationEmployee", "completed")]
    [InlineData("InstallationRelationship", "jobless")]
    [InlineData("InstallationRelationship", "pending")]
    [InlineData("InstallationRelationship", "completed")]
    public async Task AudienceErasureCleansVerifiedInputJobsAndDerivativesAndReplays(string kind, string state)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        if (state != "jobless")
        {
            var recovery = IngestionRecovery(fixture, db);
            var review = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
            await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
                new(Guid.NewGuid(), review.Revision, review.EvidenceToken));
            if (state == "completed")
                Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
            db.ChangeTracker.Clear();
        }
        var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.Null(preview.ApplyBlockedReason);
        var audience = Assert.Single(preview.Audiences);
        Assert.Equal(kind.StartsWith("Installation", StringComparison.Ordinal) ? "Installation-private" : kind, audience.Scope);
        Assert.Equal(kind is "Team" or "Role" ? null : (Guid?)fixture.EmployeeId, audience.EmployeeId);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        var result = await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.Equal("completed", result.Status); Assert.Equal(state == "jobless" ? 0 : 1, result.ClearedJobs);
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id));
        Assert.Empty((await fixture.Store.ExportAsync(episode.Partition)).Claims);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.True((await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request)).WasReplay);
        Assert.Equal("completed", (await service.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, user)).Status);
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "actor-ended")]
    [InlineData("Team", "actor-future")]
    [InlineData("Team", "employee-ended")]
    [InlineData("Team", "archive")]
    [InlineData("Role", "actor-role")]
    [InlineData("Role", "employee-role")]
    [InlineData("Role", "foreign-role")]
    [InlineData("InstallationEmployee", "installation")]
    [InlineData("InstallationRelationship", "installation")]
    public async Task AudienceErasureRechecksCurrentMembershipBeforeApplyAndAfterCleanup(string kind, string revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        await RevokeOperatorAudienceAsync(fixture, db, audience, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request));
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id));
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
        // A separate fixture verifies replay against the same revocation after deletion.
        await using var second = await DurabilityFixture.CreateAsync(postgres: true);
        var (secondUser, secondEpisode, secondAudience) = await SeedOperatorAudienceAsync(second, kind);
        await using var secondDb = second.Context(); var secondService = ErasureService(second, secondDb);
        var secondPreview = await secondService.GetErasureImpactAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser);
        var secondRequest = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(secondPreview.EvidenceToken));
        await secondService.EraseSourceAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondRequest);
        await RevokeOperatorAudienceAsync(second, secondDb, secondAudience, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secondService.EraseSourceAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secondService.GetErasureStatusAsync(second.OrganizationId, second.EmployeeId, secondRequest.OperationId, secondUser));
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task AudienceErasureRejectsChangedReviewEvenWhenMembershipStillAllowsAccess(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        if (kind == "Team") await db.OrganizationTeams.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Renamed team"));
        else await db.CoreRoles.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Renamed role"));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken))));
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AudienceErasureAllowsHistoricalInstallationRelationshipWhileRecoveryStillRequiresLiveConversation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, "InstallationRelationship");
        await using var db = fixture.Context();
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(t => t.ArchivedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => IngestionRecovery(fixture, db).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.Null(preview.ApplyBlockedReason);
        await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
    }

    [MemoryPostgresTheory]
    [InlineData("manager", false, false)]
    [InlineData("member", false, false)]
    [InlineData("manager", true, false)]
    [InlineData("member", true, false)]
    [InlineData("manager", false, true)]
    [InlineData("member", false, true)]
    [InlineData("manager", true, true)]
    [InlineData("member", true, true)]
    public async Task AudienceErasureRetainsEverySharedProducerAuthority(string revoke, bool afterCleanup, bool joblessContributor)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, team) = await SeedOperatorAudienceAsync(fixture, "Team");
        await using var db = fixture.Context();
        var original = await db.AgentInstallations.SingleAsync(x => x.Id == fixture.InstallationId);
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), BusinessId = fixture.OrganizationId.ToString("D"),
            PackageVersionId = original.PackageVersionId, IsEnabled = true, SetupState = PluginSetupState.Ready };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Agent,
            AgentInstallation = installation, ReportsToOrganizationUserId = fixture.HumanId };
        db.CoreOrganizationUsers.Add(employee);
        db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, RequiredCapabilitiesJson = "[\"platform.memory.write.v1\"]" });
        db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, TeamId = team,
            OrganizationUserId = employee.Id, ExclusiveAgentEmployeeId = employee.Id, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var id = Guid.NewGuid();
        var copy = episode with { Id = id, Source = episode.Source with { Id = id.ToString("D"), Author = employee.Id.ToString("D") },
            IdempotencyKey = "producer-copy:" + id.ToString("D"), Metadata = new Dictionary<string, string> { ["installationId"] = installation.Id.ToString("D"), ["employeeId"] = employee.Id.ToString("D") } };
        await fixture.Store.AppendEpisodeAsync(copy);
        var contributor = copy;
        var revokedEmployee = employee.Id;
        if (joblessContributor)
        {
            var contributorInstallation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
                BusinessId = fixture.OrganizationId.ToString("D"), PackageVersionId = original.PackageVersionId, IsEnabled = true };
            var contributorEmployee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                EmployeeType = EmployeeType.Agent, AgentInstallation = contributorInstallation, ReportsToOrganizationUserId = fixture.HumanId };
            db.CoreOrganizationUsers.Add(contributorEmployee);
            db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, TeamId = team,
                OrganizationUserId = contributorEmployee.Id, ExclusiveAgentEmployeeId = contributorEmployee.Id, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var contributorId = Guid.NewGuid(); revokedEmployee = contributorEmployee.Id;
            contributor = copy with { Id = contributorId, Source = copy.Source with { Id = contributorId.ToString("D"), Author = contributorEmployee.Id.ToString("D") },
                IdempotencyKey = "contributor:" + contributorId.ToString("D"), Metadata = new Dictionary<string, string> { ["installationId"] = contributorInstallation.Id.ToString("D"), ["employeeId"] = contributorEmployee.Id.ToString("D") } };
            await fixture.Store.AppendEpisodeAsync(contributor);
        }
        await fixture.Store.WriteProcedureAsync(new(Guid.NewGuid(), episode.Partition, episode.Id, "Shared lineage", "Verified retained procedure", null,
            1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, episode.OccurredAt, null, DateTimeOffset.UtcNow)
            { SourceEpisodeIds = new[] { episode.Id, copy.Id, contributor.Id }.Distinct().ToArray() });
        await db.Database.ExecuteSqlRawAsync(Infrastructure.Persistence.Migrations.ReviewedEpisodeReconciliation.InstallGuards);
        var recovery = IngestionRecovery(fixture, db);
        var review = await recovery.PreviewAsync(fixture.OrganizationId, employee.Id, copy.Id, user);
        Assert.True(review.CanQueue);
        await recovery.RecoverAsync(fixture.OrganizationId, employee.Id, copy.Id, user,
            new(Guid.NewGuid(), review.Revision, review.EvidenceToken, review.RequiredReconciliationPolicy));
        db.ChangeTracker.Clear();
        var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.Null(preview.ApplyBlockedReason);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        if (afterCleanup) await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        if (revoke == "manager") await db.CoreOrganizationUsers.Where(x => x.Id == revokedEmployee).ExecuteUpdateAsync(x => x.SetProperty(t => t.ReportsToOrganizationUserId, (Guid?)null));
        else await db.TeamMemberships.Where(x => x.OrganizationUserId == revokedEmployee).ExecuteUpdateAsync(x => x.SetProperty(t => t.EndedAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request));
        if (afterCleanup)
        {
            var receipt = await db.MemoryErasureReceipts.SingleAsync();
            Assert.Contains(revokedEmployee.ToString("D"), receipt.InventoryJson);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, user));
        }
        else Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AudienceErasureReadsOlderContentFreeReceiptWithoutOwnerExtension()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, _) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var receipt = new MemoryErasureReceipt { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeId = fixture.EmployeeId,
            EpisodeId = Guid.NewGuid(), OperationId = Guid.NewGuid(), ActorApplicationUserId = user, ActorOrganizationUserId = fixture.HumanId,
            RequestHash = new string('a', 64), CreatedAt = DateTimeOffset.UtcNow,
            InventoryJson = JsonSerializer.Serialize(new { Audiences = new[] { fixture.Partition }, PendingRuntimes = Array.Empty<object>(), TurnIds = Array.Empty<Guid>() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.MemoryErasureReceipts.Add(receipt); await db.SaveChangesAsync();
        var service = ErasureService(fixture, db);
        Assert.Equal("completed", (await service.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, receipt.OperationId, user)).Status);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(t => t.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, receipt.OperationId, user));
    }
}
