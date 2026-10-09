using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid User, MemoryEpisode Episode, Guid Audience)> SeedOperatorAudienceAsync(DurabilityFixture fixture, string kind,
        bool legacyShared = false, MemorySensitivity? sensitivity = null)
    {
        var tenant = fixture.OrganizationId.ToString("D"); var employee = fixture.EmployeeId.ToString("D");
        var audienceId = Guid.NewGuid();
        var audience = kind switch
        {
            "Organization" => EmployeeMemoryNamespaces.Organization(tenant, "csweet"),
            "Team" => EmployeeMemoryNamespaces.Team(tenant, audienceId.ToString("D"), "csweet"),
            "Role" => EmployeeMemoryNamespaces.Role(tenant, audienceId.ToString("D"), "csweet"),
            "InstallationEmployee" => EmployeeMemoryNamespaces.Employee(tenant, employee, fixture.InstallationId.ToString("D")),
            _ => EmployeeMemoryNamespaces.UserRelationship(tenant, employee, fixture.HumanId.ToString("D"), fixture.InstallationId.ToString("D"))
        };
        var (user, episode) = await SeedJoblessProposalAsync(fixture, x => x with
        {
            Partition = legacyShared ? audience.Partition with { ApplicationId = fixture.InstallationId.ToString("D") } : audience.Partition,
            Scope = audience.Scope, Sensitivity = sensitivity ?? (legacyShared ? MemorySensitivity.Internal : x.Sensitivity)
        });
        await using var db = fixture.Context();
        if (kind == "Role")
        {
            db.CoreRoles.Add(new Role { Id = audienceId, OrganizationId = fixture.OrganizationId, Name = "Designer" });
            await db.SaveChangesAsync();
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId || x.Id == fixture.HumanId)
                .ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, audienceId));
        }
        if (kind == "Team")
        {
            db.OrganizationTeams.Add(new OrganizationTeam { Id = audienceId, OrganizationId = fixture.OrganizationId,
                Name = "Design", NormalizedName = "DESIGN", TeamKey = "design", LeadOrganizationUserId = fixture.HumanId });
            db.TeamMemberships.AddRange(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                TeamId = audienceId, OrganizationUserId = fixture.EmployeeId, ExclusiveAgentEmployeeId = fixture.EmployeeId,
                JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, TeamId = audienceId,
                    OrganizationUserId = fixture.HumanId, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        return (user, episode, audienceId);
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    [InlineData("InstallationEmployee")]
    [InlineData("InstallationRelationship")]
    public async Task OperatorRecoveryPreservesVerifiedTeamRoleAndInstallationPrivateAudiences(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var candidate = Assert.Single((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        Assert.Equal(episode.Id, candidate.EpisodeId);
        Assert.Equal(kind.StartsWith("Installation", StringComparison.Ordinal) ? "Installation-private" : kind, candidate.Audience);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.True(preview.CanQueue);
        Assert.Equal(candidate.Audience, preview.Audience);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        var queued = await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.Equal("Pending", queued.Status);
        Assert.True((await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request)).Replayed);
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var claim = Assert.Single((await fixture.Store.ExportAsync(episode.Partition)).Claims);
        Assert.Equal(episode.Partition, claim.Partition); Assert.Equal(MemorySensitivity.Personal, claim.Sensitivity);
        Assert.Equal(MemoryConfirmationState.Pending, claim.Confirmation);
        Assert.Empty((await fixture.Store.ExportAsync(EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), "csweet").Partition)).Claims);
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
    [InlineData("InstallationRelationship", "relationship")]
    public async Task OperatorRecoveryRejectsAudienceRevocationAfterPreviewAndRemovesDiscovery(string kind, string revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await RevokeOperatorAudienceAsync(fixture, db, audience, revoke);
        var error = await Record.ExceptionAsync(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId,
            episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        Assert.True(error is UnauthorizedAccessException or InvalidOperationException, error?.ToString());
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        if (revoke == "installation") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user));
        else Assert.Empty((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
    }

    private static async Task RevokeOperatorAudienceAsync(DurabilityFixture fixture, Infrastructure.Persistence.CSweetDbContext db,
        Guid audience, string revoke)
    {
        switch (revoke)
        {
            case "actor-ended": await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, DateTimeOffset.UtcNow)); break;
            case "actor-future": await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.JoinedAt, DateTimeOffset.UtcNow.AddDays(1))); break;
            case "employee-ended": await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.EndedAt, DateTimeOffset.UtcNow)); break;
            case "archive": await db.OrganizationTeams.ExecuteUpdateAsync(x => x.SetProperty(j => j.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "actor-role": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, (Guid?)null)); break;
            case "employee-role": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.RoleId, (Guid?)null)); break;
            case "foreign-role":
                var foreign = new Organization { Id = Guid.NewGuid(), Name = "Other" }; db.CoreOrganizations.Add(foreign); await db.SaveChangesAsync();
                await db.CoreRoles.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(j => j.OrganizationId, foreign.Id)); break;
            case "installation": await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(j => j.IsEnabled, false)); break;
            case "relationship": await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(j => j.ArchivedAt, DateTimeOffset.UtcNow)); break;
        }
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    [InlineData("InstallationEmployee")]
    [InlineData("InstallationRelationship")]
    public async Task OperatorRetryUsesCurrentAudienceWithoutChangingTheSavedSource(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(EpisodeMemoryEnrichmentRecovery.InstallTriggers);
        var review = IngestionRecovery(fixture, db);
        var preview = await review.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var queue = await review.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken));
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.Attempts, 10).SetProperty(j => j.LastError, "provider-timeout"));
        var source = (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).SourceJson;
        var retry = EpisodeRecovery(fixture, db); var request = new RetryMemoryEnrichmentRequest(Guid.NewGuid(), 0);
        Assert.Equal("Pending", (await retry.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, queue.JobId, user, request)).Job.Status);
        await RevokeOperatorAudienceAsync(fixture, db, audience, kind switch {
            "Team" => "actor-ended", "Role" => "actor-role", "InstallationEmployee" => "installation", _ => "relationship" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => retry.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, queue.JobId, user, request));
        Assert.Equal(source, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).SourceJson);
        Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("foreign-installation")]
    [InlineData("installation-shared")]
    [InlineData("foreign-private-owner")]
    [InlineData("extra-field")]
    public async Task OperatorRecoveryDoesNotAdoptAmbiguousOrForeignPrivateKeys(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture, x => defect switch {
            "foreign-installation" => x with { Partition = x.Partition with { ApplicationId = Guid.NewGuid().ToString("D") } },
            "installation-shared" => x with { Partition = EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), fixture.InstallationId.ToString("D")).Partition, Scope = MemoryScope.Tenant },
            "foreign-private-owner" => x with { Partition = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"), Guid.NewGuid().ToString("D"), fixture.InstallationId.ToString("D")).Partition },
            _ => x with { Partition = x.Partition with { ConversationId = Guid.NewGuid().ToString("D") } } });
        await using var db = fixture.Context(); var service = IngestionRecovery(fixture, db);
        Assert.Empty((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task OperatorRecoveryFailsForRefreshWhileAudienceAuthorityIsChanging(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await using var changing = fixture.Context();
        await using var transaction = await changing.Database.BeginTransactionAsync();
        if (kind == "Team") await changing.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"TeamMemberships\" WHERE \"TeamId\"={audience} AND \"OrganizationUserId\"={fixture.HumanId} FOR UPDATE");
        else await changing.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreRoles\" WHERE \"Id\"={audience} FOR UPDATE");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId,
            episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
    }
}
