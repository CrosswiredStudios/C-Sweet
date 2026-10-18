using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ArtifactDocumentServiceTests
{
    [Fact]
    public async Task RecoveredEvidenceIsRetainedWithoutReviewAndRemainsImmutableAcrossRetries()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);
        const string logs = "{\"observations\":{\"commands\":[{\"result\":\"original output\\n\\u001b[32mpassed\"}]}}";
        var created = await service.CreateAsync(organization.Id, actor,
            new("Recovered QA results", logs, ArtifactDocumentTypes.RecoveredQaEvidence, "create-evidence"));
        Assert.Equal(ApprovalStatus.NotRequired, (await db.CoreArtifacts.SingleAsync()).ApprovalStatus);
        var retained = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "save-evidence"));
        var retry = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "save-evidence"));
        Assert.Equal("Retained", retained.Document.Status);
        Assert.Equal("Retained", retained.LatestRevision.Status);
        Assert.Null(retained.Document.SubmittedRevisionId);
        Assert.Null(retained.Document.AcceptedRevisionId);
        Assert.Null(retained.LatestRevision.DecidedAt);
        Assert.Equal(logs, retry.LatestRevision.Content);
        Assert.Equal(created.LatestRevision.ContentSha256, retry.LatestRevision.ContentSha256);
        Assert.Single(await db.ArtifactRevisions.ToListAsync());
        Assert.Empty(await db.ArtifactReviewJobs.ToListAsync());
        Assert.Empty(await db.CoreApprovals.ToListAsync());
        Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(organization.Id, actor,
            created.Document.Id, new(created.LatestRevision.Id, "accept", null, "wrong-approval")));
        var compatibility = new ArtifactApprovalService(db, new TestAuditEventWriter());
        Assert.False((await compatibility.ApproveAsync(created.Document.Id)).Succeeded);
        var inbox = new ApprovalDashboardService(db, new ResourceChangeService(db, new TestAuditEventWriter()), new HiringService(db, null!, null!));
        Assert.Empty((await inbox.GetAsync(organization.Id, owner.ApplicationUserId.Value)).Items);
    }

    [Fact]
    public async Task LegacyEvidenceRepairRemovesOnlyItsReviewAndPreservesAllLogsAndBindings()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);
        var created = await service.CreateAsync(organization.Id, actor,
            new("Historical logs", "exact historical output", "old-type", "legacy-create"));
        var submitted = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "legacy-submit"));
        var artifact = await db.CoreArtifacts.SingleAsync();
        artifact.DocumentType = ArtifactDocumentTypes.RecoveredQaEvidence;
        var job = new ArtifactReviewJob { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            ArtifactId = artifact.Id, RevisionId = created.LatestRevision.Id, LastError = "Reviewer unavailable" };
        db.Add(job);
        await service.CreateAsync(organization.Id, actor, new("Real decision", "Review me", "brief", "normal-create"));
        await db.SaveChangesAsync();
        // Excluded even before startup repair runs.
        var inbox = new ApprovalDashboardService(db, new ResourceChangeService(db, new TestAuditEventWriter()), new HiringService(db, null!, null!));
        Assert.DoesNotContain((await inbox.GetAsync(organization.Id, owner.ApplicationUserId.Value)).Items,
            x => x.Id == artifact.Id);
        Assert.Equal(1, await ArtifactEvidenceRetention.DismissLegacyApprovalsAsync(db, DateTimeOffset.UtcNow));
        Assert.Equal(0, await ArtifactEvidenceRetention.DismissLegacyApprovalsAsync(db, DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalStatus.NotRequired, artifact.ApprovalStatus);
        Assert.Equal(ArtifactReviewJobStatus.Completed, job.Status);
        Assert.Null(job.LastError);
        Assert.Equal(submitted.Document.SubmittedRevisionId, artifact.SubmittedRevisionId);
        var revision = await db.ArtifactRevisions.SingleAsync(x => x.Id == created.LatestRevision.Id);
        Assert.Equal(submitted.LatestRevision.Content, revision.Content);
        Assert.Equal(submitted.LatestRevision.ContentSha256, revision.ContentSha256);
        Assert.Equal(ArtifactRevisionStatus.Submitted, revision.Status);
        Assert.Null(revision.DecidedAt);
        Assert.Empty(await db.CoreApprovals.ToListAsync());
        Assert.Equal(ApprovalStatus.Pending, (await db.CoreArtifacts.SingleAsync(x => x.Id != artifact.Id)).ApprovalStatus);
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x =>
            x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.ApprovalChanged);
    }

    [Theory]
    [InlineData("active", "Approved")]
    [InlineData("partial", "Pending")]
    [InlineData("expired", "Pending")]
    [InlineData("revoked", "Pending")]
    public async Task AgentAccessRequestsDoNotAskForPermissionsAlreadyGranted(string grantState, string expected)
    {
        await using var db = CreateDb();
        var (organization, _) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, DisplayName = "Naomi" };
        var artifact = new Artifact { Id = Guid.NewGuid(), OrganizationId = organization.Id, Title = "Brief" };
        db.AddRange(agent, artifact);
        foreach (var action in new[] { ArtifactActions.Read, ArtifactActions.Decide })
        {
            if (grantState == "partial" && action == ArtifactActions.Decide) continue;
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                SubjectKind = CSweet.Domain.Security.GrantSubjectKind.AgentInstallation, SubjectId = agent.AgentInstallationId.Value,
                ScopeKind = CSweet.Domain.Security.GrantScopeKind.Artifact, ScopeId = artifact.Id, Action = action,
                ExpiresAt = grantState == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : null,
                RevokedAt = grantState == "revoked" ? DateTimeOffset.UtcNow.AddHours(-1) : null });
        }
        await db.SaveChangesAsync();
        var grantCount = await db.ScopedActionGrants.CountAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactAgentActor(agent.Id, agent.AgentInstallationId.Value, "director", "1.18.3");
        var request = new RequestArtifactAccessRequest([ArtifactActions.Read, ArtifactActions.Decide], "Review brief", "access");
        var result = await service.RequestAccessAsync(organization.Id, actor, artifact.Id, request);
        var retry = await service.RequestAccessAsync(organization.Id, actor, artifact.Id, request);
        Assert.Equal(expected, result.Status);
        Assert.Equal(result.Id, retry.Id);
        Assert.Equal(grantCount, await db.ScopedActionGrants.CountAsync());
        Assert.Empty(await db.AgentPlatformEventOutbox.ToListAsync());
    }

    [Fact]
    public async Task HumanDecisionResolvesReviewErrorsAndNotificationsInTheDecisionSave()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);
        var created = await service.CreateAsync(organization.Id, actor,
            new("Brief", "# Brief", "test.document.v1", "create"));
        var submitted = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "submit"));
        var job = new ArtifactReviewJob { Id = Guid.NewGuid(), OrganizationId = organization.Id,
            ArtifactId = created.Document.Id, RevisionId = submitted.LatestRevision.Id,
            LastError = "The reviewer is unavailable." };
        var notification = new CSweet.Domain.Communications.UserNotification
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, RecipientOrganizationUserId = owner.Id,
            Category = ArtifactReviewJobLifecycle.ReassignmentCategory,
            DeduplicationKey = $"artifact-review-reassignment:{job.Id:N}:{owner.Id:N}"
        };
        db.AddRange(job, notification);
        await db.SaveChangesAsync();
        await service.DecideAsync(organization.Id, actor, created.Document.Id,
            new(submitted.LatestRevision.Id, "accept", null, "accept"));
        Assert.Equal(ArtifactReviewJobStatus.Completed, job.Status);
        Assert.Null(job.LastError);
        Assert.NotNull(notification.DismissedAt);
        Assert.Equal(ArtifactRevisionStatus.Accepted, (await db.ArtifactRevisions.SingleAsync()).Status);
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x =>
            x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.NotificationUpdated);
    }

    [Fact]
    public async Task Accepted_revision_remains_authoritative_while_a_new_draft_exists()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);

        var created = await service.CreateAsync(organization.Id, actor,
            new("Vision", "# One", "test.document.v1", "create-one"));
        var submitted = await service.SubmitAsync(organization.Id, actor, created.Document.Id,
            new(created.LatestRevision.Id, "submit-one"));
        Assert.Empty(await db.ArtifactReviewJobs.ToListAsync());
        var accepted = await service.DecideAsync(organization.Id, actor, created.Document.Id,
            new(submitted.LatestRevision.Id, "accept", null, "accept-one"));
        var draft = await service.ReviseAsync(organization.Id, actor, created.Document.Id,
            new(accepted.LatestRevision.Id, "# Two", "revise-two"));
        var detail = await service.GetAsync(organization.Id, actor, created.Document.Id);

        Assert.NotNull(detail);
        Assert.Equal(draft.Id, detail!.Document.LatestRevisionId);
        Assert.Equal(accepted.LatestRevision.Id, detail.Document.AcceptedRevisionId);
        Assert.Equal("# One", detail.AcceptedRevision!.Content);
        Assert.Equal("# Two", detail.LatestRevision.Content);
    }

    [Fact]
    public async Task Ordinary_employee_cannot_discover_or_read_an_unshared_document()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        var employee = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Employee", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(employee);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var service = new ArtifactDocumentService(db, audit, TimeProvider.System);
        var document = await service.CreateAsync(organization.Id,
            new(owner.ApplicationUserId!.Value), new("Private", "secret", "test.v1", "private-create"));

        var results = await service.BrowseAsync(organization.Id,
            new(employee.ApplicationUserId!.Value), new());
        var read = await service.GetAsync(organization.Id,
            new(employee.ApplicationUserId.Value), document.Document.Id);

        Assert.Empty(results);
        Assert.Null(read);
        Assert.Contains(audit.Events, x => x.Category == "DocumentAccess" && x.Outcome == "Denied");
    }

    [Fact]
    public async Task Human_decision_notifies_the_agent_that_created_an_intake_document()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        var installationId = Guid.NewGuid();
        var creator = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id,
            DisplayName = "Creative Director", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            AgentInstallationId = installationId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.CoreOrganizationUsers.Add(creator);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);
        var conversationId = Guid.NewGuid();

        var created = await service.CreateAsync(organization.Id, actor,
            new("Game pitch", "# First draft", "video-game.game-vision.v1", "create-pitch",
                OriginConversationId: conversationId));
        var artifact = await db.CoreArtifacts.SingleAsync(x => x.Id == created.Document.Id);
        artifact.CreatedByOrganizationUserId = creator.Id;
        artifact.CreatorDisplayName = creator.DisplayName;
        await db.SaveChangesAsync();
        var submitted = await service.SubmitAsync(organization.Id, actor, artifact.Id,
            new(created.LatestRevision.Id, "submit-pitch"));

        await service.DecideAsync(organization.Id, actor, artifact.Id,
            new(submitted.LatestRevision.Id, "accept", "Approved from the document preview.", "accept-pitch"));

        var notification = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == WorkstreamEventNames.ArtifactRevisionDecidedV1).ToListAsync());
        Assert.Equal(installationId, notification.TargetInstallationId);
        var payload = JsonSerializer.Deserialize<GenericResourceEvent>(
            notification.DataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(submitted.LatestRevision.Id, payload!.AggregateId);
        Assert.Equal("accepted", payload.Action);
        Assert.Equal(conversationId, payload.Metadata.GetProperty("originConversationId").GetGuid());
        Assert.Equal("Approved from the document preview.", payload.Metadata.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task DocumentLabelComesFromThePinnedExtensionProfile()
    {
        await using var db = CreateDb();
        var (organization, owner) = SeedHuman(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = new ArtifactDocumentService(db, new TestAuditEventWriter(), TimeProvider.System);
        var actor = new ArtifactHumanActor(owner.ApplicationUserId!.Value);
        var created = await service.CreateAsync(organization.Id, actor,
            new("Brief", "Audience notes", "publisher.campaign-brief.v1", "create-brief"));
        var workstreamId = Guid.NewGuid();
        db.Workstreams.Add(new Workstream
        {
            Id = workstreamId, OrganizationId = organization.Id, ProfileKey = "campaign.v1",
            ProfileVersion = 1, ProfileDefinitionDigest = "pinned"
        });
        db.WorkstreamProfileDefinitions.Add(new WorkstreamProfileDefinitionRecord
        {
            Id = Guid.NewGuid(), Key = "campaign.v1", Version = 1, DefinitionDigest = "pinned",
            DefinitionJson = """{"artifactTypes":[{"key":"publisher.campaign-brief.v1","displayName":"Audience brief","schemaVersion":"1.0"}]}"""
        });
        (await db.CoreArtifacts.SingleAsync(x => x.Id == created.Document.Id)).WorkstreamId = workstreamId;
        await db.SaveChangesAsync();
        var detail = await service.GetAsync(organization.Id, actor, created.Document.Id);
        Assert.Equal("Audience brief", detail!.DocumentTypeDisplayName);
        Assert.Equal("publisher.campaign-brief.v1", detail.Document.DocumentType);
    }
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Organization, OrganizationUser) SeedHuman(CSweetDbContext db, OrganizationPermissionLevel level)
    {
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Studio", CreatedAt = now, UpdatedAt = now };
        var user = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = level, CreatedAt = now
        };
        db.CoreOrganizations.Add(organization);
        db.CoreOrganizationUsers.Add(user);
        return (organization, user);
    }
}
