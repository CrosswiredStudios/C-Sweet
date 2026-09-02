using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

public sealed class ArtifactDocumentService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    TimeProvider clock) : IArtifactDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] CreatorActions =
        [ArtifactActions.Read, ArtifactActions.Revise, ArtifactActions.Submit];

    public async Task<IReadOnlyList<ArtifactDocumentSummary>> BrowseAsync(
        Guid organizationId, ArtifactHumanActor actor, ArtifactDocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var isAdmin = IsHumanAdmin(member);
        var allowedIds = isAdmin ? null : await ReadableArtifactIdsAsync(organizationId,
            GrantSubjectKind.OrganizationUser, member.Id, cancellationToken);

        var source = db.CoreArtifacts.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (!isAdmin) source = source.Where(x => allowedIds!.Contains(x.Id));
        if (!query.IncludeArchived) source = source.Where(x => x.ArchivedAt == null);
        if (query.FolderId.HasValue) source = source.Where(x => x.FolderId == query.FolderId);
        if (query.PackageId.HasValue) source = source.Where(x => x.PackageId == query.PackageId);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = Enum.Parse<ArtifactDocumentStatus>(query.Status, true);
            source = source.Where(x => x.DocumentStatus == status);
        }
        if (!string.IsNullOrWhiteSpace(query.DocumentType))
            source = source.Where(x => x.DocumentType == query.DocumentType);
        if (query.OriginWorkItemId.HasValue) source = source.Where(x => x.OriginWorkItemId == query.OriginWorkItemId);
        if (query.UpdatedFrom.HasValue) source = source.Where(x => x.UpdatedAt >= query.UpdatedFrom);
        if (query.UpdatedTo.HasValue) source = source.Where(x => x.UpdatedAt <= query.UpdatedTo);
        if (!string.IsNullOrWhiteSpace(query.CreatorOrSteward))
        {
            var person = query.CreatorOrSteward.Trim().ToLower();
            source = source.Where(x => x.CreatorDisplayName.ToLower().Contains(person) ||
                (x.StewardOrganizationUser != null && x.StewardOrganizationUser.DisplayName.ToLower().Contains(person)));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            source = source.Where(x => x.Title.ToLower().Contains(search) ||
                x.Revisions.Any(r => r.Content.ToLower().Contains(search)));
        }

        var artifacts = await source.Include(x => x.Revisions)
            .OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(cancellationToken);
        var activeCreatorIds = await ActiveEmployeeIdsAsync(organizationId,
            artifacts.Select(x => x.CreatedByOrganizationUserId), cancellationToken);
        await AuditAsync(string.IsNullOrWhiteSpace(query.Search) ? "artifact.listed" : "artifact.searched",
            "Completed", organizationId, null, member,
            new { query.Search, query.FolderId, query.PackageId, query.Status, query.DocumentType, query.IncludeArchived, count = artifacts.Count }, cancellationToken);
        return artifacts.Select(x => Summary(x, activeCreatorIds)).ToList();
    }

    public async Task<ArtifactDocumentDetail?> GetAsync(
        Guid organizationId, ArtifactHumanActor actor, Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (!await CanAsync(organizationId, member, artifactId, ArtifactActions.Read, cancellationToken))
        {
            await AuditAsync("artifact.read", "Denied", organizationId, artifactId, member,
                new { action = ArtifactActions.Read }, cancellationToken, "artifact_access_denied");
            return null;
        }
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken);
        if (artifact is null) return null;
        var latest = artifact.Revisions.Single(x => x.Id == artifact.LatestRevisionId);
        await AuditAsync("artifact.read", "Completed", organizationId, artifactId, member,
            new { artifact.LatestRevisionId, artifact.AcceptedRevisionId, latest.ContentSha256,
                contentBytes = Encoding.UTF8.GetByteCount(latest.Content) }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactDocumentDetail> CreateAsync(
        Guid organizationId, ArtifactHumanActor actor, CreateArtifactDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        ValidateDocument(request.Title, request.Content, request.DocumentType, request.IdempotencyKey);
        var existing = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
            return await DetailAsync((await LoadArtifactAsync(organizationId, existing.ArtifactId, cancellationToken))!, cancellationToken);
        await ValidateFolderPackageAsync(organizationId, request.FolderId, request.PackageId, cancellationToken);

        var now = clock.GetUtcNow();
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Type = ArtifactType.Document,
            Title = request.Title.Trim(), Content = request.Content, Version = 1,
            ApprovalStatus = ApprovalStatus.Pending, CreatedAt = now, UpdatedAt = now,
            FolderId = request.FolderId, PackageId = request.PackageId,
            OriginConversationId = request.OriginConversationId, OriginWorkItemId = request.OriginWorkItemId,
            CreatedByOrganizationUserId = member.Id, StewardOrganizationUserId = request.StewardOrganizationUserId ?? member.Id,
            CreatorDisplayName = member.DisplayName, DocumentType = request.DocumentType.Trim(),
            DocumentStatus = ArtifactDocumentStatus.Draft
        };
        var revision = NewRevision(artifact, request.Content, member, null, request.IdempotencyKey, now);
        artifact.LatestRevisionId = revision.Id;
        db.CoreArtifacts.Add(artifact);
        db.ArtifactRevisions.Add(revision);
        AddCreatorGrants(organizationId, artifact.Id, GrantSubjectKind.OrganizationUser, member.Id, member.Id, now);
        await db.SaveChangesAsync(cancellationToken);
        await AuditContentAsync("artifact.created", organizationId, artifact, revision, member, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactRevisionResponse> ReviseAsync(
        Guid organizationId, ArtifactHumanActor actor, Guid artifactId, CreateArtifactRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        await RequireActionAsync(organizationId, member, artifactId, ArtifactActions.Revise, cancellationToken);
        ValidateContent(request.Content, request.IdempotencyKey);
        var existing = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return Revision(existing);
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        if (artifact.ArchivedAt.HasValue) throw new InvalidOperationException("Archived documents cannot be revised.");
        if (artifact.LatestRevisionId != request.ExpectedBaseRevisionId)
            throw new DbUpdateConcurrencyException("The document changed. Reload it before submitting this edit.");
        var now = clock.GetUtcNow();
        var revision = NewRevision(artifact, request.Content, member, request.ExpectedBaseRevisionId,
            request.IdempotencyKey, now, artifact.Revisions.Max(x => x.Number) + 1);
        db.ArtifactRevisions.Add(revision);
        artifact.LatestRevisionId = revision.Id;
        artifact.Content = request.Content;
        artifact.Version = revision.Number;
        artifact.DocumentStatus = ArtifactDocumentStatus.Draft;
        artifact.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await AuditContentAsync("artifact.revision.created", organizationId, artifact, revision, member, cancellationToken);
        return Revision(revision);
    }

    public async Task<ArtifactDocumentDetail> SubmitAsync(
        Guid organizationId, ArtifactHumanActor actor, Guid artifactId, SubmitArtifactRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        await RequireActionAsync(organizationId, member, artifactId, ArtifactActions.Submit, cancellationToken);
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == request.RevisionId) ?? throw NotFound();
        if (revision.Status == ArtifactRevisionStatus.Submitted)
            return await DetailAsync(artifact, cancellationToken);
        if (artifact.LatestRevisionId != revision.Id || revision.Status != ArtifactRevisionStatus.Draft)
            throw new InvalidOperationException("Only the latest draft revision can be submitted.");
        var now = clock.GetUtcNow();
        revision.Status = ArtifactRevisionStatus.Submitted;
        revision.SubmittedAt = now;
        artifact.SubmittedRevisionId = revision.Id;
        artifact.DocumentStatus = ArtifactDocumentStatus.InReview;
        artifact.ApprovalStatus = ApprovalStatus.Pending;
        artifact.UpdatedAt = now;
        var reviewer = request.ReviewerOrganizationUserId ?? artifact.StewardOrganizationUserId;
        Guid? reviewerInstallation = null;
        if (reviewer.HasValue && reviewer != member.Id)
            reviewerInstallation = await db.CoreOrganizationUsers.Where(x => x.Id == reviewer &&
                    x.OrganizationId == organizationId && x.IsActive)
                .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(cancellationToken);
        if (reviewerInstallation.HasValue && !await db.ArtifactReviewJobs.AnyAsync(x => x.OrganizationId == organizationId &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken))
            db.ArtifactReviewJobs.Add(new ArtifactReviewJob
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifactId,
                RevisionId = revision.Id, ConversationId = request.ConversationId ?? artifact.OriginConversationId,
                ReviewerOrganizationUserId = reviewer, ReviewerInstallationId = reviewerInstallation,
                IdempotencyKey = request.IdempotencyKey, CreatedAt = now, NextAttemptAt = now
            });
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.revision.submitted", "Completed", organizationId, artifactId, member,
            new { revisionId = revision.Id, revision.Number, reviewQueued = reviewerInstallation.HasValue }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactDocumentDetail> DecideAsync(
        Guid organizationId, ArtifactHumanActor actor, Guid artifactId, DecideArtifactRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        await RequireActionAsync(organizationId, member, artifactId, ArtifactActions.Decide, cancellationToken);
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == request.RevisionId) ?? throw NotFound();
        var decision = request.Decision.Trim().ToLowerInvariant();
        if (decision is not ("accept" or "approve" or "reject" or "request-revision"))
            throw new ArgumentException("Decision must be accept, reject, or request-revision.");
        var accepted = decision is "accept" or "approve";
        if (revision.Status is ArtifactRevisionStatus.Accepted or ArtifactRevisionStatus.Rejected)
        {
            if ((revision.Status == ArtifactRevisionStatus.Accepted) != accepted)
                throw new InvalidOperationException("The revision already has the opposite terminal decision.");
            return await DetailAsync(artifact, cancellationToken);
        }
        if (revision.Status != ArtifactRevisionStatus.Submitted)
            throw new InvalidOperationException("Only a submitted revision can be decided.");
        var now = clock.GetUtcNow();
        revision.Status = accepted ? ArtifactRevisionStatus.Accepted : ArtifactRevisionStatus.Rejected;
        revision.DecidedAt = now;
        artifact.DocumentStatus = accepted ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.ChangesRequested;
        artifact.ApprovalStatus = accepted ? ApprovalStatus.Approved : ApprovalStatus.RevisionRequested;
        artifact.SubmittedRevisionId = null;
        if (accepted) artifact.AcceptedRevisionId = revision.Id;
        artifact.UpdatedAt = now;
        db.CoreApprovals.Add(new Approval
        {
            Id = Guid.NewGuid(), ArtifactId = artifact.Id, ArtifactRevisionId = revision.Id,
            Status = accepted ? ApprovalStatus.Approved : ApprovalStatus.RevisionRequested,
            Comment = request.Comment, DecidedAt = now, CreatedAt = now,
            DecidedByOrganizationUserId = member.Id,
            EvidenceConversationMessageId = request.EvidenceConversationMessageId
        });
        foreach (var job in await db.ArtifactReviewJobs.Where(x => x.RevisionId == revision.Id &&
                     x.Status != ArtifactReviewJobStatus.Completed).ToListAsync(cancellationToken))
            job.Status = ArtifactReviewJobStatus.Completed;
        await AddArtifactDecisionEventAsync(
            artifact,
            revision,
            accepted ? "accepted" : "changes-required",
            request.Comment,
            member.Id,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(accepted ? "artifact.revision.accepted" : "artifact.revision.changes-requested",
            "Completed", organizationId, artifactId, member,
            new { revisionId = revision.Id, revision.Number, request.EvidenceConversationMessageId }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactDocumentDetail> MoveAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid artifactId, MoveArtifactRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        await RequireActionAsync(organizationId, member, artifactId, ArtifactActions.Revise, cancellationToken);
        await ValidateFolderPackageAsync(organizationId, request.FolderId, null, cancellationToken);
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        artifact.FolderId = request.FolderId; artifact.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.moved", "Completed", organizationId, artifactId, member,
            new { request.FolderId }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactDocumentDetail> ReassignStewardAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid artifactId, ReassignArtifactStewardRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (request.StewardOrganizationUserId.HasValue && !await db.CoreOrganizationUsers.AnyAsync(x =>
                x.Id == request.StewardOrganizationUserId && x.OrganizationId == organizationId && x.IsActive,
                cancellationToken))
            throw new ArgumentException("The new steward must be an active employee of this organization.");
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        artifact.StewardOrganizationUserId = request.StewardOrganizationUserId;
        artifact.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.steward.reassigned", "Completed", organizationId, artifactId, member,
            new { request.StewardOrganizationUserId, request.IdempotencyKey }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<ArtifactDocumentDetail> SetArchivedAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid artifactId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var artifact = await LoadArtifactAsync(organizationId, artifactId, cancellationToken) ?? throw NotFound();
        artifact.ArchivedAt = archived ? clock.GetUtcNow() : null;
        artifact.DocumentStatus = archived ? ArtifactDocumentStatus.Archived :
            artifact.AcceptedRevisionId.HasValue ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.Draft;
        artifact.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(archived ? "artifact.archived" : "artifact.restored", "Completed",
            organizationId, artifactId, member, new { request.IdempotencyKey }, cancellationToken);
        return await DetailAsync(artifact, cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactFolderResponse>> ListFoldersAsync(Guid organizationId,
        ArtifactHumanActor actor, bool includeArchived, CancellationToken cancellationToken = default)
    {
        _ = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        return await db.ArtifactFolders.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            (includeArchived || x.ArchivedAt == null)).OrderBy(x => x.Name)
            .Select(x => new ArtifactFolderResponse(x.Id, x.OrganizationId, x.ParentFolderId, x.Name,
                x.CreatedAt, x.UpdatedAt, x.ArchivedAt)).ToListAsync(cancellationToken);
    }

    public async Task<ArtifactFolderResponse> CreateFolderAsync(Guid organizationId, ArtifactHumanActor actor,
        CreateArtifactFolderRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160) throw new ArgumentException("Folder name is required and limited to 160 characters.");
        await ValidateFolderPackageAsync(organizationId, request.ParentFolderId, null, cancellationToken);
        var now = clock.GetUtcNow();
        var folder = new ArtifactFolder { Id = Guid.NewGuid(), OrganizationId = organizationId,
            ParentFolderId = request.ParentFolderId, Name = request.Name.Trim(), CreatedAt = now, UpdatedAt = now };
        db.ArtifactFolders.Add(folder); await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.folder.created", "Completed", organizationId, folder.Id, member,
            new { folder.Name, folder.ParentFolderId }, cancellationToken);
        return Folder(folder);
    }

    public async Task<ArtifactFolderResponse> UpdateFolderAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid folderId, UpdateArtifactFolderRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var folder = await db.ArtifactFolders.SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        if (request.ParentFolderId == folderId) throw new ArgumentException("A folder cannot contain itself.");
        await ValidateFolderPackageAsync(organizationId, request.ParentFolderId, null, cancellationToken);
        folder.Name = request.Name.Trim(); folder.ParentFolderId = request.ParentFolderId; folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.folder.updated", "Completed", organizationId, folder.Id, member,
            new { folder.Name, folder.ParentFolderId }, cancellationToken);
        return Folder(folder);
    }

    public async Task<ArtifactFolderResponse> SetFolderArchivedAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid folderId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var folder = await db.ArtifactFolders.SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        folder.ArchivedAt = archived ? clock.GetUtcNow() : null; folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(archived ? "artifact.folder.archived" : "artifact.folder.restored", "Completed",
            organizationId, folder.Id, member, new { request.IdempotencyKey }, cancellationToken);
        return Folder(folder);
    }

    public async Task<IReadOnlyList<ArtifactGrantResponse>> SetGrantsAsync(Guid organizationId,
        ArtifactHumanActor actor, Guid artifactId, UpsertArtifactGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        _ = await db.CoreArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        if (!Enum.TryParse<GrantSubjectKind>(request.SubjectKind, true, out var kind) || kind is not (GrantSubjectKind.OrganizationUser or GrantSubjectKind.AgentInstallation))
            throw new ArgumentException("Subject kind must be OrganizationUser or AgentInstallation.");
        var actions = request.Actions.Distinct(StringComparer.Ordinal).ToList();
        if (actions.Any(x => !ArtifactActions.FileActions.Contains(x)))
            throw new ArgumentException("One or more document actions are invalid.");
        ValidateMutationKey(request.IdempotencyKey);
        if (request.ExpiresAt.HasValue && request.ExpiresAt <= clock.GetUtcNow())
            throw new ArgumentException("Grant expiry must be in the future.");
        var subjectExists = kind == GrantSubjectKind.OrganizationUser
            ? await db.CoreOrganizationUsers.AnyAsync(x => x.Id == request.SubjectId && x.OrganizationId == organizationId &&
                x.IsActive && x.EmployeeType == EmployeeType.Human, cancellationToken)
            : await db.CoreOrganizationUsers.AnyAsync(x => x.AgentInstallationId == request.SubjectId &&
                x.OrganizationId == organizationId && x.IsActive && x.EmployeeType == EmployeeType.Agent, cancellationToken);
        if (!subjectExists) throw new ArgumentException("The access principal is not an active employee in this organization.");
        var now = clock.GetUtcNow();
        var active = await db.ScopedActionGrants.Where(x => x.OrganizationId == organizationId &&
            x.SubjectKind == kind && x.SubjectId == request.SubjectId && x.ScopeKind == GrantScopeKind.Artifact &&
            x.ScopeId == artifactId && x.RevokedAt == null).ToListAsync(cancellationToken);
        var revoked = active.Where(x => !actions.Contains(x.Action, StringComparer.Ordinal)).ToList();
        foreach (var old in revoked) { old.RevokedAt = now; old.Revision++; }
        var granted = new List<string>();
        foreach (var action in actions)
        {
            var grant = active.SingleOrDefault(x => x.Action == action);
            if (grant is null)
            {
                db.ScopedActionGrants.Add(NewGrant(organizationId, artifactId, kind,
                    request.SubjectId, action, member.Id, now, request.ExpiresAt));
                granted.Add(action);
            }
            else { grant.ExpiresAt = request.ExpiresAt; grant.Revision++; }
        }
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(actions.Count == 0 ? "artifact.access.revoked" : granted.Count > 0 ? "artifact.access.granted" : "artifact.access.grants-updated",
            "Completed", organizationId, artifactId, member,
            new { request.SubjectKind, request.SubjectId, actions, revokedGrantIds = revoked.Select(x => x.Id), request.ExpiresAt }, cancellationToken);
        return await GrantResponsesAsync(organizationId, artifactId, cancellationToken);
    }

    public async Task<ArtifactAccessRequestResponse> RequestAccessAsync(Guid organizationId,
        ArtifactAgentActor actor, Guid artifactId, RequestArtifactAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await db.CoreOrganizationUsers.AnyAsync(x => x.Id == actor.OrganizationUserId &&
            x.OrganizationId == organizationId && x.AgentInstallationId == actor.InstallationId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("The requesting agent is not active in this organization.");
        _ = await db.CoreArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        ValidateAccessRequest(request);
        var actions = request.Actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (actions.Count == 0 || actions.Any(x => !ArtifactActions.FileActions.Contains(x)))
            throw new ArgumentException("One or more requested actions are invalid.");
        var existing = await db.ArtifactAccessRequests.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return await AccessResponseAsync(existing, cancellationToken);
        var duplicate = await db.ArtifactAccessRequests.FirstOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ArtifactId == artifactId && x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == actor.InstallationId && x.Status == ArtifactAccessRequestStatus.Pending &&
            x.ActionsJson == JsonSerializer.Serialize(actions, JsonOptions), cancellationToken);
        if (duplicate is not null) return await AccessResponseAsync(duplicate, cancellationToken);
        var item = new ArtifactAccessRequest
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifactId,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = actor.InstallationId,
            RequestingInstallationId = actor.InstallationId, ActionsJson = JsonSerializer.Serialize(actions, JsonOptions),
            Justification = request.Justification.Trim(), IdempotencyKey = request.IdempotencyKey,
            CreatedAt = clock.GetUtcNow(), ExpiresAt = request.ExpiresAt
        };
        db.ArtifactAccessRequests.Add(item); await db.SaveChangesAsync(cancellationToken);
        await AuditAgentAsync("artifact.access.requested", "Completed", organizationId, artifactId, actor,
            new { requestId = item.Id, actions, justificationBytes = Encoding.UTF8.GetByteCount(item.Justification) }, cancellationToken);
        return await AccessResponseAsync(item, cancellationToken);
    }

    public async Task<ArtifactAccessRequestResponse> RequestAccessAsync(Guid organizationId,
        ArtifactHumanActor actor, Guid artifactId, RequestArtifactAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (!await db.CoreArtifacts.AsNoTracking().AnyAsync(x => x.Id == artifactId && x.OrganizationId == organizationId, cancellationToken))
            throw NotFound();
        ValidateAccessRequest(request);
        var actions = request.Actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var existing = await db.ArtifactAccessRequests.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return await AccessResponseAsync(existing, cancellationToken);
        var now = clock.GetUtcNow();
        var actionsJson = JsonSerializer.Serialize(actions, JsonOptions);
        var duplicate = await db.ArtifactAccessRequests.FirstOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ArtifactId == artifactId && x.SubjectKind == GrantSubjectKind.OrganizationUser &&
            x.SubjectId == member.Id && x.Status == ArtifactAccessRequestStatus.Pending &&
            x.ActionsJson == actionsJson, cancellationToken);
        if (duplicate is not null) return await AccessResponseAsync(duplicate, cancellationToken);
        var item = new ArtifactAccessRequest
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifactId,
            SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = member.Id,
            ActionsJson = actionsJson, Justification = request.Justification.Trim(),
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now, ExpiresAt = request.ExpiresAt
        };
        db.ArtifactAccessRequests.Add(item); await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.access.requested", "Completed", organizationId, artifactId, member,
            new { requestId = item.Id, actions }, cancellationToken);
        return await AccessResponseAsync(item, cancellationToken);
    }

    public async Task<ArtifactAccessRequestResponse> DecideAccessAsync(Guid organizationId,
        ArtifactHumanActor actor, Guid requestId, DecideArtifactAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        ValidateMutationKey(request.IdempotencyKey);
        if (request.GrantExpiresAt.HasValue && request.GrantExpiresAt <= clock.GetUtcNow())
            throw new ArgumentException("Grant expiry must be in the future.");
        var item = await db.ArtifactAccessRequests.SingleOrDefaultAsync(x => x.Id == requestId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        var approved = request.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase) || request.Decision.Equals("accept", StringComparison.OrdinalIgnoreCase);
        var rejected = request.Decision.Equals("reject", StringComparison.OrdinalIgnoreCase);
        if (!approved && !rejected) throw new ArgumentException("Decision must be approve or reject.");
        if (item.Status != ArtifactAccessRequestStatus.Pending)
        {
            if ((item.Status == ArtifactAccessRequestStatus.Approved) != approved)
                throw new InvalidOperationException("The access request already has the opposite terminal decision.");
            return await AccessResponseAsync(item, cancellationToken);
        }
        var now = clock.GetUtcNow();
        item.Status = approved ? ArtifactAccessRequestStatus.Approved : ArtifactAccessRequestStatus.Rejected;
        item.DecidedAt = now; item.DecidedByOrganizationUserId = member.Id;
        item.EvidenceConversationMessageId = request.EvidenceConversationMessageId;
        var grantIds = new List<Guid>(); var revisions = new List<long>();
        if (approved)
            foreach (var action in DeserializeActions(item.ActionsJson))
            {
                var grant = await db.ScopedActionGrants.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                    x.SubjectKind == item.SubjectKind && x.SubjectId == item.SubjectId &&
                    x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == item.ArtifactId &&
                    x.Action == action && x.RevokedAt == null, cancellationToken);
                if (grant is null)
                {
                    grant = NewGrant(organizationId, item.ArtifactId, item.SubjectKind, item.SubjectId,
                        action, member.Id, now, request.GrantExpiresAt);
                    db.ScopedActionGrants.Add(grant);
                }
                else
                {
                    grant.ExpiresAt = request.GrantExpiresAt ?? grant.ExpiresAt;
                    grant.Revision++;
                }
                grantIds.Add(grant.Id); revisions.Add(grant.Revision);
            }
        if (item.RequestingInstallationId.HasValue)
        {
            var payload = new ArtifactAccessDecisionEvent(item.Id, item.ArtifactId,
                approved ? "Approved" : "Rejected", DeserializeActions(item.ActionsJson), grantIds, revisions, now);
            db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                TargetInstallationId = item.RequestingInstallationId.Value,
                EventType = ArtifactPlatformCapabilities.AccessDecisionEvent,
                DataJson = JsonSerializer.Serialize(payload, JsonOptions),
                IdempotencyKey = $"artifact-access:{item.Id:D}:{item.Status}",
                Status = AgentPlatformEventOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(approved ? "artifact.access.granted" : "artifact.access.rejected", "Completed",
            organizationId, item.ArtifactId, member, new { requestId, item.SubjectKind, item.SubjectId, grantIds }, cancellationToken);
        return await AccessResponseAsync(item, cancellationToken);
    }

    public async Task<ArtifactPackageResponse> CreatePackageAsync(Guid organizationId, ArtifactHumanActor actor,
        CreateArtifactPackageRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256 ||
            string.IsNullOrWhiteSpace(request.PackageType) || request.PackageType.Length > 160)
            throw new ArgumentException("A valid package name, type, and idempotency key are required.");
        var replay = await db.ArtifactPackages.AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null) return Package(replay);
        if (request.Members.Count == 0) throw new ArgumentException("A package requires at least one document.");
        var ids = request.Members.Select(x => x.ArtifactId).Distinct().ToList();
        var artifacts = await db.CoreArtifacts.Where(x => x.OrganizationId == organizationId && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (ids.Count != request.Members.Count || artifacts.Count != ids.Count)
            throw new ArgumentException("Every package member must be a distinct document in this organization.");
        if (request.Members.Any(member => artifacts.Single(x => x.Id == member.ArtifactId).DocumentType != member.RequiredDocumentType))
            throw new ArgumentException("Every package member's declared type must match the document's immutable type.");
        var now = clock.GetUtcNow();
        var package = new ArtifactPackage { Id = Guid.NewGuid(), OrganizationId = organizationId,
            Name = request.Name.Trim(), PackageType = request.PackageType.Trim(), IdempotencyKey = request.IdempotencyKey,
            CreatedByOrganizationUserId = member.Id,
            CreatedAt = now, UpdatedAt = now };
        foreach (var input in request.Members.OrderBy(x => x.Position)) package.Members.Add(new ArtifactPackageMember
        { Id = Guid.NewGuid(), PackageId = package.Id, ArtifactId = input.ArtifactId, Position = input.Position, RequiredDocumentType = input.RequiredDocumentType });
        db.ArtifactPackages.Add(package);
        foreach (var artifact in artifacts)
            artifact.PackageId = package.Id;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.package.created", "Completed", organizationId, package.Id, member,
            new { package.PackageType, memberCount = ids.Count }, cancellationToken);
        return Package(package);
    }

    public async Task<IReadOnlyList<ArtifactPackageResponse>> ListPackagesAsync(Guid organizationId,
        ArtifactHumanActor actor, bool includeArchived, CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var query = db.ArtifactPackages.AsNoTracking().Include(x => x.Members)
            .Where(x => x.OrganizationId == organizationId && (includeArchived || x.ArchivedAt == null));
        if (!IsHumanAdmin(member))
        {
            var readable = await ReadableArtifactIdsAsync(organizationId, GrantSubjectKind.OrganizationUser,
                member.Id, cancellationToken);
            query = query.Where(x => x.Members.All(m => readable.Contains(m.ArtifactId)));
        }
        return (await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken)).Select(Package).ToList();
    }

    public async Task<ArtifactPackageResponse?> GetPackageAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid packageId, CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var package = await db.ArtifactPackages.AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == packageId && x.OrganizationId == organizationId, cancellationToken);
        if (package is null) return null;
        if (!IsHumanAdmin(member))
            foreach (var entry in package.Members)
                if (!await CanAsync(organizationId, member, entry.ArtifactId, ArtifactActions.Read, cancellationToken))
                    return null;
        await AuditAsync("artifact.package.read", "Completed", organizationId, packageId, member,
            new { package.Version, memberCount = package.Members.Count }, cancellationToken);
        return Package(package);
    }

    public async Task<ArtifactPackageResponse> SubmitPackageAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid packageId, SubmitArtifactPackageRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireMemberAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("An idempotency key is required.");
        var package = await db.ArtifactPackages.Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == packageId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        if (package.LastSubmissionIdempotencyKey == request.IdempotencyKey) return Package(package);
        if (package.ArchivedAt.HasValue) throw new InvalidOperationException("Archived packages cannot be submitted.");
        foreach (var entry in package.Members)
            await RequireActionAsync(organizationId, member, entry.ArtifactId, ArtifactActions.Submit, cancellationToken);
        package.Status = ArtifactDocumentStatus.InReview;
        package.LastSubmissionIdempotencyKey = request.IdempotencyKey;
        package.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync("artifact.package.submitted", "Completed", organizationId, package.Id, member,
            new { package.Version }, cancellationToken);
        return Package(package);
    }

    public async Task<ArtifactPackageResponse> DecidePackageAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid packageId, DecideArtifactPackageRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("An idempotency key is required.");
        var package = await db.ArtifactPackages.Include(x => x.Members).ThenInclude(x => x.Artifact)
            .SingleOrDefaultAsync(x => x.Id == packageId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        if (package.LastDecisionIdempotencyKey == request.IdempotencyKey) return Package(package);
        var accept = request.Decision.Equals("accept", StringComparison.OrdinalIgnoreCase) || request.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        if (!accept && !request.Decision.Equals("reject", StringComparison.OrdinalIgnoreCase) &&
            !request.Decision.Equals("request-revision", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Decision must be accept, reject, or request-revision.");
        if (accept && package.Members.Any(x => x.Artifact?.AcceptedRevisionId is null))
            throw new InvalidOperationException("Every package document must have an accepted revision.");
        var now = clock.GetUtcNow();
        package.Status = accept ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.ChangesRequested;
        package.AcceptedByOrganizationUserId = accept ? member.Id : null; package.AcceptedAt = accept ? now : null;
        package.LastDecisionIdempotencyKey = request.IdempotencyKey; package.UpdatedAt = now;
        foreach (var entry in package.Members) entry.AcceptedRevisionId = accept ? entry.Artifact!.AcceptedRevisionId : null;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(accept ? "artifact.package.accepted" : "artifact.package.changes-requested", "Completed",
            organizationId, package.Id, member, new { package.Version }, cancellationToken);
        return Package(package);
    }

    public async Task<ArtifactPackageResponse> SetPackageArchivedAsync(Guid organizationId, ArtifactHumanActor actor,
        Guid packageId, bool archived, ArtifactArchiveRequest request, CancellationToken cancellationToken = default)
    {
        var member = await RequireAdminAsync(organizationId, actor.ApplicationUserId, cancellationToken);
        var package = await db.ArtifactPackages.Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == packageId && x.OrganizationId == organizationId, cancellationToken) ?? throw NotFound();
        package.ArchivedAt = archived ? clock.GetUtcNow() : null;
        package.Status = archived ? ArtifactDocumentStatus.Archived :
            package.AcceptedAt.HasValue ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.Draft;
        package.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(archived ? "artifact.package.archived" : "artifact.package.restored", "Completed",
            organizationId, packageId, member, new { request.IdempotencyKey }, cancellationToken);
        return Package(package);
    }

    private async Task<OrganizationUser> RequireMemberAsync(Guid organizationId, Guid applicationUserId, CancellationToken token) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive && x.EmployeeType == EmployeeType.Human, token)
        ?? throw new UnauthorizedAccessException("The signed-in user is not an active human employee of this organization.");

    private async Task<OrganizationUser> RequireAdminAsync(Guid organizationId, Guid applicationUserId, CancellationToken token)
    {
        var member = await RequireMemberAsync(organizationId, applicationUserId, token);
        if (!IsHumanAdmin(member)) throw new UnauthorizedAccessException("Only human Owners and Managers can administer documents.");
        return member;
    }

    private static bool IsHumanAdmin(OrganizationUser member) => member.EmployeeType == EmployeeType.Human && member.PermissionLevel >= OrganizationPermissionLevel.Manager;

    private async Task<bool> CanAsync(Guid organizationId, OrganizationUser member, Guid artifactId, string action, CancellationToken token)
    {
        if (IsHumanAdmin(member)) return true;
        var now = clock.GetUtcNow();
        return await db.ScopedActionGrants.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.OrganizationUser && x.SubjectId == member.Id && x.Action == action &&
            x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == artifactId && x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now), token);
    }

    private async Task RequireActionAsync(Guid organizationId, OrganizationUser member, Guid artifactId, string action, CancellationToken token)
    {
        if (await CanAsync(organizationId, member, artifactId, action, token)) return;
        await AuditAsync("artifact.access.denied", "Denied", organizationId, artifactId, member, new { action }, token, "artifact_access_denied");
        throw new UnauthorizedAccessException("This document has not been shared with the requested permission.");
    }

    private async Task<List<Guid>> ReadableArtifactIdsAsync(Guid organizationId, GrantSubjectKind kind, Guid subjectId, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        return await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.SubjectKind == kind && x.SubjectId == subjectId && x.Action == ArtifactActions.Read &&
            x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId.HasValue && x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now)).Select(x => x.ScopeId!.Value).Distinct().ToListAsync(token);
    }

    private async Task<Artifact?> LoadArtifactAsync(Guid organizationId, Guid artifactId, CancellationToken token) =>
        await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == artifactId && x.OrganizationId == organizationId, token);

    private async Task<ArtifactDocumentDetail> DetailAsync(Artifact artifact, CancellationToken token)
    {
        if (artifact.Revisions.Count == 0)
            artifact = (await LoadArtifactAsync(artifact.OrganizationId, artifact.Id, token))!;
        var activeIds = await ActiveEmployeeIdsAsync(artifact.OrganizationId, [artifact.CreatedByOrganizationUserId], token);
        var revisions = artifact.Revisions.OrderByDescending(x => x.Number).Select(Revision).ToList();
        var latest = revisions.Single(x => x.Id == artifact.LatestRevisionId);
        var accepted = revisions.SingleOrDefault(x => x.Id == artifact.AcceptedRevisionId);
        var requestIds = await db.ArtifactAccessRequests.AsNoTracking().Where(x => x.ArtifactId == artifact.Id)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).ToListAsync(token);
        var requests = await Task.WhenAll(requestIds.Select(id => AccessResponseByIdAsync(id, token)));
        return new ArtifactDocumentDetail(Summary(artifact, activeIds), latest, accepted, revisions,
            await GrantResponsesAsync(artifact.OrganizationId, artifact.Id, token), requests);
    }

    private async Task<ArtifactAccessRequestResponse> AccessResponseByIdAsync(Guid id, CancellationToken token) =>
        await AccessResponseAsync((await db.ArtifactAccessRequests.AsNoTracking().SingleAsync(x => x.Id == id, token)), token);

    private async Task<ArtifactAccessRequestResponse> AccessResponseAsync(ArtifactAccessRequest item, CancellationToken token)
    {
        string name = item.SubjectKind == GrantSubjectKind.AgentInstallation
            ? await db.CoreOrganizationUsers.Where(x => x.AgentInstallationId == item.SubjectId).Select(x => x.DisplayName).FirstOrDefaultAsync(token) ?? item.SubjectId.ToString("D")
            : await db.CoreOrganizationUsers.Where(x => x.Id == item.SubjectId).Select(x => x.DisplayName).FirstOrDefaultAsync(token) ?? item.SubjectId.ToString("D");
        return new(item.Id, item.ArtifactId, item.SubjectKind.ToString(), item.SubjectId, name,
            DeserializeActions(item.ActionsJson), item.Justification, item.Status.ToString(), item.CreatedAt, item.ExpiresAt, item.DecidedAt);
    }

    private async Task<IReadOnlyList<ArtifactGrantResponse>> GrantResponsesAsync(Guid organizationId, Guid artifactId, CancellationToken token)
    {
        var grants = await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == artifactId).OrderBy(x => x.GrantedAt).ToListAsync(token);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId).ToListAsync(token);
        return grants.Select(x => new ArtifactGrantResponse(x.Id, x.SubjectKind.ToString(), x.SubjectId,
            x.SubjectKind == GrantSubjectKind.AgentInstallation
                ? people.FirstOrDefault(p => p.AgentInstallationId == x.SubjectId)?.DisplayName ?? x.SubjectId.ToString("D")
                : people.FirstOrDefault(p => p.Id == x.SubjectId)?.DisplayName ?? x.SubjectId.ToString("D"),
            x.Action, x.GrantedAt, x.ExpiresAt, x.RevokedAt, x.ParentGrantId, x.Revision)).ToList();
    }

    private void AddCreatorGrants(Guid organizationId, Guid artifactId, GrantSubjectKind kind, Guid subjectId, Guid grantedBy, DateTimeOffset now)
    {
        foreach (var action in CreatorActions) db.ScopedActionGrants.Add(NewGrant(organizationId, artifactId, kind, subjectId, action, grantedBy, now, null));
    }

    private static ScopedActionGrant NewGrant(Guid organizationId, Guid artifactId, GrantSubjectKind kind,
        Guid subjectId, string action, Guid grantedBy, DateTimeOffset now, DateTimeOffset? expiresAt) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, SubjectKind = kind, SubjectId = subjectId,
        Action = action, ScopeKind = GrantScopeKind.Artifact, ScopeId = artifactId,
        GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = grantedBy,
        GrantedAt = now, ExpiresAt = expiresAt
    };

    private static ArtifactRevision NewRevision(Artifact artifact, string content, OrganizationUser member,
        Guid? baseRevisionId, string idempotencyKey, DateTimeOffset now, int number = 1) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = artifact.OrganizationId, ArtifactId = artifact.Id,
        Number = number, BaseRevisionId = baseRevisionId, Content = content,
        ContentSha256 = Sha256(content), CreatedByOrganizationUserId = member.Id,
        CreatorDisplayName = member.DisplayName, IdempotencyKey = idempotencyKey, CreatedAt = now
    };

    private static ArtifactRevisionResponse Revision(ArtifactRevision revision) => new(
        revision.Id, revision.ArtifactId, revision.Number, revision.BaseRevisionId, revision.Content,
        revision.ContentSha256, revision.Status.ToString(), revision.CreatorDisplayName,
        revision.CreatedAt, revision.SubmittedAt, revision.DecidedAt);

    private static ArtifactDocumentSummary Summary(Artifact artifact, IReadOnlySet<Guid> activeCreators) => new(
        artifact.Id, artifact.OrganizationId, artifact.Title, artifact.DocumentType, artifact.DocumentStatus.ToString(),
        artifact.FolderId, artifact.PackageId, artifact.LatestRevisionId, artifact.SubmittedRevisionId,
        artifact.AcceptedRevisionId, artifact.Revisions.Count == 0 ? artifact.Version : artifact.Revisions.Max(x => x.Number),
        artifact.CreatorDisplayName, artifact.CreatedByOrganizationUserId.HasValue && !activeCreators.Contains(artifact.CreatedByOrganizationUserId.Value),
        artifact.StewardOrganizationUserId, artifact.CreatedAt, artifact.UpdatedAt, artifact.ArchivedAt)
    {
        WorkstreamId = artifact.WorkstreamId,
        TeamId = artifact.TeamId
    };

    private static ArtifactFolderResponse Folder(ArtifactFolder x) => new(x.Id, x.OrganizationId,
        x.ParentFolderId, x.Name, x.CreatedAt, x.UpdatedAt, x.ArchivedAt);

    private static ArtifactPackageResponse Package(ArtifactPackage x) => new(x.Id, x.OrganizationId,
        x.Name, x.PackageType, x.Version, x.Status.ToString(), x.Members.OrderBy(m => m.Position)
            .Select(m => new ArtifactPackageMemberResponse(m.Id, m.ArtifactId, m.AcceptedRevisionId, m.Position, m.RequiredDocumentType)).ToList(),
        x.CreatedAt, x.UpdatedAt, x.AcceptedAt, x.ArchivedAt);

    private async Task<HashSet<Guid>> ActiveEmployeeIdsAsync(Guid organizationId, IEnumerable<Guid?> ids, CancellationToken token)
    {
        var values = ids.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        return (await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive && values.Contains(x.Id))
            .Select(x => x.Id).ToListAsync(token)).ToHashSet();
    }

    private async Task ValidateFolderPackageAsync(Guid organizationId, Guid? folderId, Guid? packageId, CancellationToken token)
    {
        if (folderId.HasValue && !await db.ArtifactFolders.AnyAsync(x => x.Id == folderId && x.OrganizationId == organizationId && x.ArchivedAt == null, token))
            throw new ArgumentException("Folder was not found in this organization.");
        if (packageId.HasValue && !await db.ArtifactPackages.AnyAsync(x => x.Id == packageId && x.OrganizationId == organizationId && x.ArchivedAt == null, token))
            throw new ArgumentException("Package was not found in this organization.");
    }

    private static void ValidateDocument(string title, string content, string type, string key)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512) throw new ArgumentException("Title is required and limited to 512 characters.");
        if (string.IsNullOrWhiteSpace(type) || type.Length > 160) throw new ArgumentException("Document type is required and limited to 160 characters.");
        ValidateContent(content, key);
    }

    private static void ValidateContent(string content, string key)
    {
        if (string.IsNullOrWhiteSpace(content) || Encoding.UTF8.GetByteCount(content) > 131072) throw new ArgumentException("Markdown content is required and limited to 128 KiB.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("An idempotency key is required and limited to 200 characters.");
    }

    private void ValidateAccessRequest(RequestArtifactAccessRequest request)
    {
        ValidateMutationKey(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(request.Justification) || request.Justification.Length > 2048)
            throw new ArgumentException("A justification is required and limited to 2048 characters.");
        if (request.Actions.Count == 0 || request.Actions.Any(x => !ArtifactActions.FileActions.Contains(x)))
            throw new ArgumentException("One or more requested actions are invalid.");
        if (request.ExpiresAt.HasValue && request.ExpiresAt <= clock.GetUtcNow())
            throw new ArgumentException("Request expiry must be in the future.");
    }

    private static void ValidateMutationKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new ArgumentException("An idempotency key is required and limited to 200 characters.");
    }

    private static IReadOnlyList<string> DeserializeActions(string json) => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    private static KeyNotFoundException NotFound() => new("Document was not found.");

    private async Task AddArtifactDecisionEventAsync(
        Artifact artifact,
        ArtifactRevision revision,
        string disposition,
        string? comment,
        Guid decidedByOrganizationUserId,
        CancellationToken cancellationToken)
    {
        var creatorInstallationId = artifact.CreatedByOrganizationUserId.HasValue
            ? await db.CoreOrganizationUsers
                .Where(x => x.Id == artifact.CreatedByOrganizationUserId.Value &&
                            x.OrganizationId == artifact.OrganizationId &&
                            x.IsActive &&
                            x.AgentInstallationId != null)
                .Select(x => x.AgentInstallationId)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (!artifact.WorkstreamId.HasValue && !creatorInstallationId.HasValue) return;

        var now = clock.GetUtcNow();
        var context = new W.AgentWorkContext(
            artifact.OrganizationId,
            artifact.WorkstreamId ?? Guid.Empty,
            artifact.TeamId,
            null,
            artifact.OriginWorkItemId,
            null,
            null,
            Guid.NewGuid(),
            null,
            null);
        var metadata = new
        {
            artifactId = artifact.Id,
            revisionId = revision.Id,
            revision.ContentSha256,
            artifact.DocumentType,
            artifact.OriginConversationId,
            disposition,
            comment,
            decidedByOrganizationUserId
        };
        var payload = new W.GenericResourceEvent(
            Guid.NewGuid(),
            now,
            context,
            "ArtifactRevision",
            revision.Id,
            revision.Number,
            artifact.DocumentType,
            disposition,
            JsonSerializer.SerializeToElement(metadata, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = artifact.OrganizationId,
            TargetInstallationId = creatorInstallationId,
            EventType = W.WorkstreamEventNames.ArtifactRevisionDecidedV1,
            DataJson = JsonSerializer.Serialize(payload, JsonOptions),
            IdempotencyKey = $"{W.WorkstreamEventNames.ArtifactRevisionDecidedV1}:{revision.Id:N}:{disposition}",
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private Task AuditContentAsync(string eventType, Guid organizationId, Artifact artifact,
        ArtifactRevision revision, OrganizationUser actor, CancellationToken token) =>
        AuditAsync(eventType, "Completed", organizationId, artifact.Id, actor,
            new { revisionId = revision.Id, revision.Number, revision.ContentSha256, contentBytes = Encoding.UTF8.GetByteCount(revision.Content) }, token);

    private Task AuditAsync(string eventType, string outcome, Guid organizationId, Guid? entityId,
        OrganizationUser actor, object metadata, CancellationToken token, string? errorCode = null) =>
        audit.AppendAsync(new AuditEventWriteRequest(eventType, "DocumentAccess", "Internal", outcome,
            organizationId, "Artifact", entityId, Summary: eventType.Replace('.', ' '),
            MetadataJson: JsonSerializer.Serialize(metadata, JsonOptions),
            Actor: new AuditActor("Human", true, actor.ApplicationUserId, actor.Id, actor.DisplayName),
            ErrorCode: errorCode, UseAmbientOrganization: false), token);

    private Task AuditAgentAsync(string eventType, string outcome, Guid organizationId, Guid? entityId,
        ArtifactAgentActor actor, object metadata, CancellationToken token, string? errorCode = null) =>
        audit.AppendAsync(new AuditEventWriteRequest(eventType, "DocumentAccess", "Internal", outcome,
            organizationId, "Artifact", entityId, Summary: eventType.Replace('.', ' '),
            MetadataJson: JsonSerializer.Serialize(metadata, JsonOptions),
            Actor: new AuditActor("Agent", true, OrganizationUserId: actor.OrganizationUserId,
                AgentId: actor.AgentId, InstallationId: actor.InstallationId, PackageVersion: actor.AgentVersion),
            ErrorCode: errorCode, UseAmbientOrganization: false), token);
}
