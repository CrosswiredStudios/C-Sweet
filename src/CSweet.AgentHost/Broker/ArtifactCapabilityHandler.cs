using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatformCapabilityError = CSweet.Agent.SDK.PlatformCapabilityError;
using PlatformCapabilityErrorCode = CSweet.Agent.SDK.PlatformCapabilityErrorCode;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class ArtifactCapabilityHandler(
    CSweetDbContext db,
    IArtifactDocumentService documents,
    IAuditEventWriter audit,
    TimeProvider clock) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        ArtifactPlatformCapabilities.Create, ArtifactPlatformCapabilities.Read,
        ArtifactPlatformCapabilities.Revise, ArtifactPlatformCapabilities.Submit,
        ArtifactPlatformCapabilities.Decide, ArtifactPlatformCapabilities.DecideV2, ArtifactPlatformCapabilities.RequestAccess,
        ArtifactPlatformCapabilities.PackageCreate, ArtifactPlatformCapabilities.PackageRead,
        ArtifactPlatformCapabilities.PackageSubmit, ArtifactPlatformCapabilities.PackageDecide
    };

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The installation is not granted this document capability.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent identity is invalid.");
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive &&
            x.EmployeeType == EmployeeType.Agent, token);
        if (employee is null) return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent is not an active employee.");
        var actor = new ArtifactAgentActor(employee.Id, installationId, session.AgentId, session.AgentVersion);
        try
        {
            object result = request.Capability switch
            {
                ArtifactPlatformCapabilities.Create => await CreateAsync(organizationId, employee, actor,
                    Read<CreateArtifactDocumentRequest>(request), token),
                ArtifactPlatformCapabilities.Read => await ReadAsync(organizationId, actor,
                    Read<AgentArtifactReadRequest>(request), token),
                ArtifactPlatformCapabilities.Revise => await ReviseAsync(organizationId, actor,
                    Read<AgentArtifactRevisionRequest>(request), token),
                ArtifactPlatformCapabilities.Submit => await SubmitAsync(organizationId, actor,
                    Read<AgentArtifactSubmitRequest>(request), token),
                ArtifactPlatformCapabilities.Decide => await DecideAsync(organizationId, actor,
                    Read<AgentArtifactDecisionRequest>(request), token),
                ArtifactPlatformCapabilities.DecideV2 => await DecideStructuredAsync(organizationId, actor,
                    Read<W.StructuredArtifactDecisionRequest>(request), token),
                ArtifactPlatformCapabilities.RequestAccess => await RequestAccessAsync(organizationId, actor,
                    Read<AgentArtifactAccessRequest>(request), token),
                ArtifactPlatformCapabilities.PackageCreate => await CreatePackageAsync(organizationId, actor,
                    Read<CreateArtifactPackageRequest>(request), token),
                ArtifactPlatformCapabilities.PackageRead => await ReadPackageAsync(organizationId, actor,
                    Read<AgentArtifactPackageReadRequest>(request), token),
                ArtifactPlatformCapabilities.PackageSubmit => await SetPackageReviewAsync(organizationId, actor,
                    Read<AgentArtifactPackageMutationRequest>(request), false, token),
                ArtifactPlatformCapabilities.PackageDecide => await SetPackageReviewAsync(organizationId, actor,
                    Read<AgentArtifactPackageMutationRequest>(request), true, token),
                _ => throw new KeyNotFoundException("The document capability is not implemented.")
            };
            return Success(request.RequestId, result);
        }
        catch (JsonException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (ArgumentException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message); }
        catch (DbUpdateConcurrencyException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (InvalidOperationException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (KeyNotFoundException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message); }
    }

    private async Task<object> CreateAsync(Guid organizationId, OrganizationUser employee, ArtifactAgentActor actor,
        CreateArtifactDocumentRequest request, CancellationToken token)
    {
        if (!await HasOrganizationCreateAsync(organizationId, actor.InstallationId, token))
        { await DeniedAsync(organizationId, null, actor, ArtifactActions.Create, token); throw new UnauthorizedAccessException("An organization-level document-create grant is required."); }
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 512 ||
            string.IsNullOrWhiteSpace(request.DocumentType) || request.DocumentType.Length > 160 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Content) || Encoding.UTF8.GetByteCount(request.Content) > 131072)
            throw new ArgumentException("A title and Markdown content up to 128 KiB are required.");
        if (request.FolderId.HasValue && !await db.ArtifactFolders.AnyAsync(x => x.Id == request.FolderId &&
                x.OrganizationId == organizationId && x.ArchivedAt == null, token))
            throw new ArgumentException("Folder was not found in this organization.");
        if (request.PackageId.HasValue && !await db.ArtifactPackages.AnyAsync(x => x.Id == request.PackageId &&
                x.OrganizationId == organizationId && x.ArchivedAt == null, token))
            throw new ArgumentException("Package was not found in this organization.");
        if (request.StewardOrganizationUserId.HasValue && !await db.CoreOrganizationUsers.AnyAsync(x =>
                x.Id == request.StewardOrganizationUserId && x.OrganizationId == organizationId && x.IsActive, token))
            throw new ArgumentException("The steward must be an active employee of this organization.");
        var (workstreamId, teamId) = await ResolveWorkContextAsync(organizationId, employee.Id, request, token);
        var existing = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return await AgentDetailAsync(organizationId, existing.ArtifactId, token);
        var now = clock.GetUtcNow();
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, Type = ArtifactType.Document,
            WorkstreamId = workstreamId, TeamId = teamId,
            Title = request.Title.Trim(), Content = request.Content, Version = 1,
            ApprovalStatus = ApprovalStatus.Pending, CreatedAt = now, UpdatedAt = now,
            FolderId = request.FolderId, PackageId = request.PackageId,
            OriginConversationId = request.OriginConversationId, OriginWorkItemId = request.OriginWorkItemId,
            CreatedByOrganizationUserId = employee.Id, StewardOrganizationUserId = request.StewardOrganizationUserId ?? employee.Id,
            CreatorDisplayName = employee.DisplayName, CreatorAgentId = actor.AgentId, CreatorAgentVersion = actor.AgentVersion,
            DocumentType = request.DocumentType.Trim(), DocumentStatus = ArtifactDocumentStatus.Draft
        };
        var revision = new ArtifactRevision
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifact.Id, Number = 1,
            Content = request.Content, ContentSha256 = Sha256(request.Content), CreatedByOrganizationUserId = employee.Id,
            CreatedByAgentInstallationId = actor.InstallationId, CreatorDisplayName = employee.DisplayName,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now
        };
        artifact.LatestRevisionId = revision.Id;
        db.CoreArtifacts.Add(artifact); db.ArtifactRevisions.Add(revision);
        foreach (var action in new[] { ArtifactActions.Read, ArtifactActions.Revise, ArtifactActions.Submit })
            db.ScopedActionGrants.Add(NewGrant(organizationId, artifact.Id, actor.InstallationId, action, now));
        await db.SaveChangesAsync(token);
        await AuditAsync("artifact.created", "Completed", organizationId, artifact.Id, actor,
            new { revisionId = revision.Id, revision.ContentSha256, contentBytes = Encoding.UTF8.GetByteCount(revision.Content) }, token);
        return await AgentDetailAsync(organizationId, artifact.Id, token);
    }

    private async Task<(Guid? WorkstreamId, Guid? TeamId)> ResolveWorkContextAsync(
        Guid organizationId, Guid actorId, CreateArtifactDocumentRequest request, CancellationToken token)
    {
        Guid? workstreamId = request.WorkstreamId;
        Guid? teamId = request.TeamId;
        if (request.OriginWorkItemId.HasValue)
        {
            var source = await (from item in db.CoreWorkTasks.AsNoTracking()
                                join board in db.WorkBoards.AsNoTracking() on item.BoardId equals board.Id
                                where item.OrganizationId == organizationId && item.Id == request.OriginWorkItemId
                                select new { board.WorkstreamId, board.TeamId }).SingleOrDefaultAsync(token)
                ?? throw new ArgumentException("The origin work item was not found.");
            if (workstreamId.HasValue && source.WorkstreamId != workstreamId || teamId.HasValue && source.TeamId != teamId)
                throw new ArgumentException("The explicit document context conflicts with its origin work item.");
            workstreamId ??= source.WorkstreamId; teamId ??= source.TeamId;
        }
        if (request.OriginConversationId.HasValue)
        {
            var source = await db.CoreConversations.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.Id == request.OriginConversationId)
                .Select(x => new { x.WorkstreamId, x.TeamId }).SingleOrDefaultAsync(token)
                ?? throw new ArgumentException("The origin conversation was not found.");
            if (workstreamId.HasValue && source.WorkstreamId.HasValue && source.WorkstreamId != workstreamId ||
                teamId.HasValue && source.TeamId.HasValue && source.TeamId != teamId)
                throw new ArgumentException("The explicit document context conflicts with its origin conversation.");
            workstreamId ??= source.WorkstreamId; teamId ??= source.TeamId;
        }
        if (!workstreamId.HasValue) return (null, teamId);
        var workstream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workstreamId, token)
            ?? throw new ArgumentException("The Workstream was not found.");
        var supervisor = await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x =>
            x.WorkstreamId == workstream.Id && x.SupervisorOrganizationUserId == actorId && x.EndsAt == null, token);
        var assignedTeams = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x =>
            x.WorkstreamId == workstream.Id && x.EndsAt == null).Select(x => x.TeamId).ToListAsync(token);
        if (teamId.HasValue && !assignedTeams.Contains(teamId.Value))
            throw new ArgumentException("The team is not assigned to the Workstream.");
        var member = await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
            assignedTeams.Contains(x.TeamId) && x.OrganizationUserId == actorId && x.EndedAt == null, token);
        if (workstream.AccountableManagerOrganizationUserId != actorId && !supervisor && !member)
            throw new UnauthorizedAccessException("The document Workstream is outside this employee's scope.");
        return (workstreamId, teamId);
    }

    private async Task<object> ReadAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactReadRequest request, CancellationToken token)
    {
        if (request.ArtifactId.HasValue)
        {
            await RequireFileGrantAsync(organizationId, request.ArtifactId.Value, actor, ArtifactActions.Read, token);
            var latest = await db.ArtifactRevisions.AsNoTracking().Where(x =>
                    x.OrganizationId == organizationId && x.ArtifactId == request.ArtifactId.Value &&
                    x.Artifact!.LatestRevisionId == x.Id)
                .SingleAsync(token);
            await AuditAsync("artifact.read", "Completed", organizationId, request.ArtifactId, actor,
                new { revisionId = latest.Id, latest.ContentSha256,
                    contentBytes = Encoding.UTF8.GetByteCount(latest.Content) }, token);
            return await AgentDetailAsync(organizationId, request.ArtifactId.Value, token);
        }
        var ids = await GrantedArtifactIdsAsync(organizationId, actor.InstallationId, ArtifactActions.Read, token);
        var list = await db.CoreArtifacts.AsNoTracking().Where(x => x.OrganizationId == organizationId && ids.Contains(x.Id) &&
            (request.IncludeArchived || x.ArchivedAt == null)).OrderByDescending(x => x.UpdatedAt)
            .Select(x => new { x.Id, x.Title, x.DocumentType, Status = x.DocumentStatus.ToString(), x.LatestRevisionId, x.AcceptedRevisionId, x.WorkstreamId, x.TeamId, x.UpdatedAt }).ToListAsync(token);
        await AuditAsync("artifact.listed", "Completed", organizationId, null, actor, new { count = list.Count }, token);
        return list;
    }

    private async Task<object> ReviseAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactRevisionRequest request, CancellationToken token)
    {
        await RequireFileGrantAsync(organizationId, request.ArtifactId, actor, ArtifactActions.Revise, token);
        if (string.IsNullOrWhiteSpace(request.Content) || Encoding.UTF8.GetByteCount(request.Content) > 131072 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Markdown content up to 128 KiB and an idempotency key are required.");
        var artifact = await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == request.ArtifactId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        if (artifact.LatestRevisionId != request.ExpectedBaseRevisionId) throw new DbUpdateConcurrencyException("The document changed; reload before revising.");
        var existing = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapRevision(existing);
        var employeeName = await db.CoreOrganizationUsers.Where(x => x.Id == actor.OrganizationUserId).Select(x => x.DisplayName).SingleAsync(token);
        var now = clock.GetUtcNow();
        var revision = new ArtifactRevision { Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifact.Id,
            Number = artifact.Revisions.Max(x => x.Number) + 1, BaseRevisionId = request.ExpectedBaseRevisionId,
            Content = request.Content, ContentSha256 = Sha256(request.Content), CreatedByOrganizationUserId = actor.OrganizationUserId,
            CreatedByAgentInstallationId = actor.InstallationId, CreatorDisplayName = employeeName,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now };
        db.ArtifactRevisions.Add(revision); artifact.LatestRevisionId = revision.Id; artifact.Content = revision.Content;
        artifact.Version = revision.Number; artifact.DocumentStatus = ArtifactDocumentStatus.Draft; artifact.UpdatedAt = now;
        await db.SaveChangesAsync(token);
        await AuditAsync("artifact.revision.created", "Completed", organizationId, artifact.Id, actor,
            new { revisionId = revision.Id, revision.ContentSha256, contentBytes = Encoding.UTF8.GetByteCount(revision.Content) }, token);
        return MapRevision(revision);
    }

    private async Task<object> SubmitAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactSubmitRequest request, CancellationToken token)
    {
        await RequireFileGrantAsync(organizationId, request.ArtifactId, actor, ArtifactActions.Submit, token);
        var artifact = await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == request.ArtifactId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == request.RevisionId) ?? throw new KeyNotFoundException();
        if (revision.Status == ArtifactRevisionStatus.Submitted) return await AgentDetailAsync(organizationId, artifact.Id, token);
        if (artifact.LatestRevisionId != revision.Id || revision.Status != ArtifactRevisionStatus.Draft) throw new InvalidOperationException("Only the latest draft can be submitted.");
        var now = clock.GetUtcNow(); revision.Status = ArtifactRevisionStatus.Submitted; revision.SubmittedAt = now;
        artifact.SubmittedRevisionId = revision.Id; artifact.DocumentStatus = ArtifactDocumentStatus.InReview; artifact.UpdatedAt = now;
        Guid? reviewerId = request.ReviewerOrganizationUserId ?? artifact.StewardOrganizationUserId;
        Guid? reviewerInstallation = reviewerId.HasValue ? await db.CoreOrganizationUsers.Where(x => x.Id == reviewerId && x.IsActive).Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token) : null;
        db.ArtifactReviewJobs.Add(new ArtifactReviewJob { Id = Guid.NewGuid(), OrganizationId = organizationId,
            ArtifactId = artifact.Id, RevisionId = revision.Id, ConversationId = request.ConversationId ?? artifact.OriginConversationId,
            ReviewerOrganizationUserId = reviewerId, ReviewerInstallationId = reviewerInstallation,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now, NextAttemptAt = now });
        AddArtifactEvent(W.WorkstreamEventNames.ArtifactRevisionSubmittedV1, artifact, revision,
            "submitted", artifact.DocumentType, new { artifact.Id, revisionId = revision.Id, revision.ContentSha256 });
        await db.SaveChangesAsync(token);
        await AuditAsync("artifact.revision.submitted", "Completed", organizationId, artifact.Id, actor, new { revisionId = revision.Id }, token);
        return await AgentDetailAsync(organizationId, artifact.Id, token);
    }

    private async Task<object> DecideStructuredAsync(
        Guid organizationId, ArtifactAgentActor actor, W.StructuredArtifactDecisionRequest request, CancellationToken token)
    {
        await RequireFileGrantAsync(organizationId, request.ArtifactId, actor, ArtifactActions.Decide, token);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.RubricTypeKey) || request.RubricTypeKey.Length > 200)
            throw new ArgumentException("Rubric type key and idempotency key are required.");
        var existingReview = await db.ArtifactReviews.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existingReview is not null)
        {
            if (existingReview.ArtifactId != request.ArtifactId || existingReview.RevisionId != request.RevisionId ||
                !string.Equals(existingReview.RevisionDigest, request.RevisionDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The idempotency key is already bound to a different review target.");
            return await AgentDetailAsync(organizationId, request.ArtifactId, token);
        }
        var artifact = await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x =>
            x.Id == request.ArtifactId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == request.RevisionId) ?? throw new KeyNotFoundException();
        if (!string.Equals(revision.ContentSha256, request.RevisionDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The submitted digest does not match the exact artifact revision.");
        if (revision.Status != ArtifactRevisionStatus.Submitted)
            throw new InvalidOperationException("Only a submitted revision can receive a structured decision.");
        var disposition = request.Disposition.Trim().ToLowerInvariant();
        if (disposition is not ("accepted" or "accepted-with-findings" or "changes-required" or "rejected"))
            throw new ArgumentException("The structured review disposition is invalid.");
        var accepted = disposition is "accepted" or "accepted-with-findings";
        if (accepted && request.Findings.Any(x => x.Blocking))
            throw new ArgumentException("A revision with blocking findings cannot be accepted.");
        if (disposition == "accepted" && request.Findings.Count > 0)
            throw new ArgumentException("Use accepted-with-findings when findings are present.");
        var now = clock.GetUtcNow();
        db.ArtifactReviews.Add(new ArtifactReview
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ArtifactId = artifact.Id, RevisionId = revision.Id,
            RevisionDigest = revision.ContentSha256, RubricTypeKey = request.RubricTypeKey,
            Disposition = disposition, FindingsJson = JsonSerializer.Serialize(request.Findings, JsonOptions),
            Comment = request.Comment, ReviewerOrganizationUserId = actor.OrganizationUserId,
            ReviewerInstallationId = actor.InstallationId,
            EvidenceConversationMessageId = request.EvidenceConversationMessageId,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now
        });
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
            Status = artifact.ApprovalStatus, Comment = request.Comment, DecidedAt = now, CreatedAt = now,
            DecidedByOrganizationUserId = actor.OrganizationUserId,
            DecidedByAgentInstallationId = actor.InstallationId,
            EvidenceConversationMessageId = request.EvidenceConversationMessageId
        });
        foreach (var job in await db.ArtifactReviewJobs.Where(x => x.RevisionId == revision.Id &&
                     x.Status != ArtifactReviewJobStatus.Completed).ToListAsync(token))
            job.Status = ArtifactReviewJobStatus.Completed;
        AddArtifactEvent(W.WorkstreamEventNames.ArtifactRevisionDecidedV1, artifact, revision,
            disposition, request.RubricTypeKey, new { artifact.Id, revisionId = revision.Id, revision.ContentSha256,
                request.RubricTypeKey, disposition, request.Findings });
        await db.SaveChangesAsync(token);
        await AuditAsync("artifact.revision.structured-decided", "Completed", organizationId, artifact.Id, actor,
            new { revisionId = revision.Id, revision.ContentSha256, request.RubricTypeKey, disposition, request.Findings }, token);
        return await AgentDetailAsync(organizationId, artifact.Id, token);
    }

    private async Task<object> DecideAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactDecisionRequest request, CancellationToken token)
    {
        var humanEvidence = request.EvidenceConversationMessageId.HasValue
            ? await ResolveHumanChatDecisionAsync(organizationId, request.ArtifactId, request.RevisionId,
                request.EvidenceConversationMessageId.Value, request.Decision.Trim().ToLowerInvariant(), token)
            : null;
        if (humanEvidence is null)
            await RequireFileGrantAsync(organizationId, request.ArtifactId, actor, ArtifactActions.Decide, token);
        var artifact = await db.CoreArtifacts.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == request.ArtifactId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == request.RevisionId) ?? throw new KeyNotFoundException();
        var normalizedDecision = request.Decision.Trim().ToLowerInvariant();
        var accepted = normalizedDecision is "accept" or "approve";
        if (!accepted && normalizedDecision is not ("reject" or "request-revision")) throw new ArgumentException("Decision is invalid.");
        if (revision.Status is ArtifactRevisionStatus.Accepted or ArtifactRevisionStatus.Rejected)
        {
            if ((revision.Status == ArtifactRevisionStatus.Accepted) != accepted)
                throw new InvalidOperationException("The revision already has the opposite terminal decision.");
            return await AgentDetailAsync(organizationId, artifact.Id, token);
        }
        if (revision.Status != ArtifactRevisionStatus.Submitted) throw new InvalidOperationException("Only submitted revisions can be decided.");
        var now = clock.GetUtcNow(); revision.Status = accepted ? ArtifactRevisionStatus.Accepted : ArtifactRevisionStatus.Rejected; revision.DecidedAt = now;
        artifact.DocumentStatus = accepted ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.ChangesRequested;
        artifact.ApprovalStatus = accepted ? ApprovalStatus.Approved : ApprovalStatus.RevisionRequested;
        artifact.SubmittedRevisionId = null; if (accepted) artifact.AcceptedRevisionId = revision.Id; artifact.UpdatedAt = now;
        db.CoreApprovals.Add(new Approval { Id = Guid.NewGuid(), ArtifactId = artifact.Id, ArtifactRevisionId = revision.Id,
            Status = artifact.ApprovalStatus, Comment = request.Comment, DecidedAt = now, CreatedAt = now,
            DecidedByOrganizationUserId = humanEvidence?.OrganizationUserId ?? actor.OrganizationUserId,
            DecidedByAgentInstallationId = humanEvidence is null ? actor.InstallationId : null,
            EvidenceConversationMessageId = request.EvidenceConversationMessageId });
        foreach (var job in await db.ArtifactReviewJobs.Where(x => x.RevisionId == revision.Id &&
                     x.Status != ArtifactReviewJobStatus.Completed).ToListAsync(token))
            job.Status = ArtifactReviewJobStatus.Completed;
        await db.SaveChangesAsync(token);
        await AuditAsync(accepted ? "artifact.revision.accepted" : "artifact.revision.changes-requested", "Completed", organizationId, artifact.Id, actor, new { revisionId = revision.Id }, token);
        return await AgentDetailAsync(organizationId, artifact.Id, token);
    }

    private async Task<ChatDecisionEvidence?> ResolveHumanChatDecisionAsync(
        Guid organizationId, Guid artifactId, Guid revisionId, Guid messageId, string decision,
        CancellationToken token)
    {
        var evidence = await (from message in db.CoreConversationMessages.AsNoTracking()
                              join conversation in db.CoreConversations.AsNoTracking() on message.ConversationId equals conversation.Id
                              join sender in db.CoreOrganizationUsers.AsNoTracking() on message.SenderOrganizationUserId equals sender.Id
                              where message.Id == messageId && conversation.OrganizationId == organizationId && sender.IsActive &&
                                    sender.EmployeeType == EmployeeType.Human &&
                                    sender.PermissionLevel >= OrganizationPermissionLevel.Manager
                              select new { message, sender }).SingleOrDefaultAsync(token);
        if (evidence is null || !IsUnambiguousDecisionText(evidence.message.Content, decision)) return null;
        var explicitlyLinked = await db.ConversationMessageArtifacts.AsNoTracking().AnyAsync(x =>
            x.MessageId == messageId && x.ArtifactId == artifactId &&
            (!x.RevisionId.HasValue || x.RevisionId == revisionId), token);
        if (!explicitlyLinked)
        {
            var pending = await db.CoreArtifacts.AsNoTracking().CountAsync(x =>
                x.OrganizationId == organizationId && x.OriginConversationId == evidence.message.ConversationId &&
                x.SubmittedRevisionId != null, token);
            if (pending != 1 || !await db.CoreArtifacts.AsNoTracking().AnyAsync(x =>
                    x.Id == artifactId && x.SubmittedRevisionId == revisionId &&
                    x.OriginConversationId == evidence.message.ConversationId, token)) return null;
        }
        return new ChatDecisionEvidence(evidence.sender.Id);
    }

    private static bool IsUnambiguousDecisionText(string content, string decision)
    {
        var normalized = content.Trim().ToLowerInvariant();
        return decision is "accept" or "approve"
            ? normalized is "accept" or "accepted" or "approve" or "approved" or "i accept" or "i approve" or
              "looks good, approved" or "looks good - approved"
            : normalized is "reject" or "rejected" or "request revision" or "request changes";
    }

    private Task<ArtifactAccessRequestResponse> RequestAccessAsync(Guid organizationId, ArtifactAgentActor actor,
        AgentArtifactAccessRequest request, CancellationToken token) => documents.RequestAccessAsync(organizationId, actor,
            request.ArtifactId, new RequestArtifactAccessRequest(request.Actions, request.Justification, request.IdempotencyKey, request.ExpiresAt), token);

    private async Task<object> CreatePackageAsync(Guid organizationId, ArtifactAgentActor actor, CreateArtifactPackageRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256 ||
            string.IsNullOrWhiteSpace(request.PackageType) || request.PackageType.Length > 160)
            throw new ArgumentException("A valid package name, type, and idempotency key are required.");
        var replay = await db.ArtifactPackages.AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (replay is not null) return MapPackage(replay);
        if (request.Members.Count is 0 or > 100 ||
            request.Members.Select(x => x.Position).Distinct().Count() != request.Members.Count)
            throw new ArgumentException("A package requires up to 100 members with unique positions.");
        foreach (var item in request.Members) await RequireFileGrantAsync(organizationId, item.ArtifactId, actor, ArtifactActions.Read, token);
        var ids = request.Members.Select(x => x.ArtifactId).Distinct().ToList();
        var artifacts = await db.CoreArtifacts.Where(x => x.OrganizationId == organizationId && ids.Contains(x.Id)).ToListAsync(token);
        if (ids.Count != request.Members.Count || artifacts.Count != ids.Count ||
            request.Members.Any(member => artifacts.Single(x => x.Id == member.ArtifactId).DocumentType != member.RequiredDocumentType))
            throw new ArgumentException("Every package member must be a distinct same-organization document whose type matches the declared package member type.");
        var workstreamIds = artifacts.Select(x => x.WorkstreamId).Distinct().ToList();
        var teamIds = artifacts.Select(x => x.TeamId).Distinct().ToList();
        if (workstreamIds.Count != 1 || teamIds.Count > 1)
            throw new ArgumentException("Every package member must share one Workstream and team context.");
        var now = clock.GetUtcNow();
        var package = new ArtifactPackage { Id = Guid.NewGuid(), OrganizationId = organizationId, Name = request.Name,
            PackageType = request.PackageType, IdempotencyKey = request.IdempotencyKey,
            WorkstreamId = workstreamIds[0], TeamId = teamIds.SingleOrDefault(),
            CreatedByOrganizationUserId = actor.OrganizationUserId, CreatedAt = now, UpdatedAt = now };
        foreach (var item in request.Members.OrderBy(x => x.Position)) package.Members.Add(new ArtifactPackageMember
        { Id = Guid.NewGuid(), PackageId = package.Id, ArtifactId = item.ArtifactId, Position = item.Position, RequiredDocumentType = item.RequiredDocumentType });
        db.ArtifactPackages.Add(package);
        foreach (var artifact in artifacts)
            artifact.PackageId = package.Id;
        await db.SaveChangesAsync(token);
        await AuditAsync("artifact.package.created", "Completed", organizationId, package.Id, actor,
            new { package.PackageType, memberCount = package.Members.Count }, token);
        return MapPackage(package);
    }

    private async Task<object> ReadPackageAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactPackageReadRequest request, CancellationToken token)
    {
        var package = await db.ArtifactPackages.AsNoTracking().Include(x => x.Members).SingleOrDefaultAsync(x => x.Id == request.PackageId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        foreach (var item in package.Members) await RequireFileGrantAsync(organizationId, item.ArtifactId, actor, ArtifactActions.Read, token);
        return MapPackage(package);
    }

    private async Task<object> SetPackageReviewAsync(Guid organizationId, ArtifactAgentActor actor, AgentArtifactPackageMutationRequest request, bool decide, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("An idempotency key is required.");
        var package = await db.ArtifactPackages.Include(x => x.Members).ThenInclude(x => x.Artifact).SingleOrDefaultAsync(x => x.Id == request.PackageId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        if ((decide ? package.LastDecisionIdempotencyKey : package.LastSubmissionIdempotencyKey) == request.IdempotencyKey)
            return MapPackage(package);
        foreach (var item in package.Members) await RequireFileGrantAsync(organizationId, item.ArtifactId, actor, decide ? ArtifactActions.Decide : ArtifactActions.Submit, token);
        if (decide && package.Members.Any(x => x.Artifact?.AcceptedRevisionId is null)) throw new InvalidOperationException("Every package document needs an accepted revision.");
        var now = clock.GetUtcNow(); package.Status = decide ? ArtifactDocumentStatus.Approved : ArtifactDocumentStatus.InReview; package.UpdatedAt = now;
        if (decide) package.LastDecisionIdempotencyKey = request.IdempotencyKey;
        else package.LastSubmissionIdempotencyKey = request.IdempotencyKey;
        if (decide) { package.AcceptedByOrganizationUserId = actor.OrganizationUserId; package.AcceptedAt = now; foreach (var item in package.Members) item.AcceptedRevisionId = item.Artifact!.AcceptedRevisionId; }
        AddPackageEvent(decide ? W.WorkstreamEventNames.ArtifactPackageDecidedV1 : W.WorkstreamEventNames.ArtifactPackageSubmittedV1,
            package, decide ? "decided" : "submitted");
        await db.SaveChangesAsync(token);
        await AuditAsync(decide ? "artifact.package.accepted" : "artifact.package.submitted", "Completed",
            organizationId, package.Id, actor, new { package.Version }, token);
        return MapPackage(package);
    }

    private async Task RequireFileGrantAsync(Guid organizationId, Guid artifactId, ArtifactAgentActor actor, string action, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var allowed = await db.ScopedActionGrants.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == actor.InstallationId &&
            x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == artifactId && x.Action == action &&
            x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now), token);
        if (allowed) return;
        await DeniedAsync(organizationId, artifactId, actor, action, token);
        throw new UnauthorizedAccessException("The agent has no grant for this action on this exact document.");
    }

    private async Task<bool> HasOrganizationCreateAsync(Guid organizationId, Guid installationId, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        return await db.ScopedActionGrants.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == installationId &&
            x.ScopeKind == GrantScopeKind.Organization && x.Action == ArtifactActions.Create && x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now), token);
    }

    private async Task<List<Guid>> GrantedArtifactIdsAsync(Guid organizationId, Guid installationId, string action, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        return await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == installationId &&
            x.ScopeKind == GrantScopeKind.Artifact && x.Action == action && x.ScopeId.HasValue && x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now)).Select(x => x.ScopeId!.Value).Distinct().ToListAsync(token);
    }

    private async Task<object> AgentDetailAsync(Guid organizationId, Guid artifactId, CancellationToken token)
    {
        var item = await db.CoreArtifacts.AsNoTracking().Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == artifactId && x.OrganizationId == organizationId, token) ?? throw new KeyNotFoundException();
        return new { item.Id, item.Title, item.DocumentType, Status = item.DocumentStatus.ToString(), item.LatestRevisionId,
            item.SubmittedRevisionId, item.AcceptedRevisionId, item.OriginConversationId, item.OriginWorkItemId,
            item.WorkstreamId, item.TeamId,
            Revisions = item.Revisions.OrderByDescending(x => x.Number).Select(MapRevision).ToList() };
    }

    private static object MapRevision(ArtifactRevision x) => new { x.Id, x.Number, x.BaseRevisionId, x.Content, x.ContentSha256, Status = x.Status.ToString(), x.CreatedAt, x.SubmittedAt, x.DecidedAt };
    private static object MapPackage(ArtifactPackage x) => new { x.Id, x.Name, x.PackageType, x.Version, Status = x.Status.ToString(), x.WorkstreamId, x.TeamId, Members = x.Members.OrderBy(m => m.Position).Select(m => new { m.ArtifactId, m.AcceptedRevisionId, m.Position, m.RequiredDocumentType }).ToList(), x.AcceptedAt };
    private static ScopedActionGrant NewGrant(Guid organizationId, Guid artifactId, Guid installationId, string action, DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), OrganizationId = organizationId, SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installationId,
      Action = action, ScopeKind = GrantScopeKind.Artifact, ScopeId = artifactId, GrantedBySubjectKind = GrantSubjectKind.AgentInstallation,
      GrantedBySubjectId = installationId, GrantedAt = now };
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void AddArtifactEvent(string eventType, Artifact artifact, ArtifactRevision revision,
        string action, string typeKey, object metadata)
    {
        if (!artifact.WorkstreamId.HasValue) return;
        var now = clock.GetUtcNow();
        var context = new W.AgentWorkContext(artifact.OrganizationId, artifact.WorkstreamId.Value, artifact.TeamId,
            null, artifact.OriginWorkItemId, null, null, Guid.NewGuid(), null, null);
        var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "ArtifactRevision", revision.Id,
            revision.Number, typeKey, action, JsonSerializer.SerializeToElement(metadata, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = artifact.OrganizationId, EventType = eventType,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            IdempotencyKey = $"{eventType}:{revision.Id:N}:{action}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
        });
    }

    private void AddPackageEvent(string eventType, ArtifactPackage package, string action)
    {
        if (!package.WorkstreamId.HasValue) return;
        var now = clock.GetUtcNow();
        var context = new W.AgentWorkContext(package.OrganizationId, package.WorkstreamId.Value, package.TeamId,
            null, null, null, null, Guid.NewGuid(), null, null);
        var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "ArtifactPackage", package.Id,
            package.Version, package.PackageType, action,
            JsonSerializer.SerializeToElement(new
            {
                package.Id, package.Version, package.Status,
                memberArtifactIds = package.Members.OrderBy(x => x.Position).Select(x => x.ArtifactId).ToList()
            }, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = package.OrganizationId, EventType = eventType,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            IdempotencyKey = $"{eventType}:{package.Id:N}:{package.Version}:{action}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now
        });
    }

    private Task DeniedAsync(Guid organizationId, Guid? artifactId, ArtifactAgentActor actor, string action, CancellationToken token) =>
        AuditAsync("artifact.access.denied", "Denied", organizationId, artifactId, actor, new { action }, token, "artifact_access_denied");
    private Task AuditAsync(string eventType, string outcome, Guid organizationId, Guid? artifactId,
        ArtifactAgentActor actor, object metadata, CancellationToken token, string? errorCode = null) => audit.AppendAsync(
        new AuditEventWriteRequest(eventType, "DocumentAccess", "Internal", outcome, organizationId, "Artifact", artifactId,
            eventType.Replace('.', ' '), JsonSerializer.Serialize(metadata, JsonOptions), Actor: new AuditActor("Agent", true,
                OrganizationUserId: actor.OrganizationUserId, AgentId: actor.AgentId, InstallationId: actor.InstallationId,
                PackageVersion: actor.AgentVersion), ErrorCode: errorCode, UseAmbientOrganization: false), token);

    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions) ?? throw new JsonException("The document payload is required.");
    private static CapabilityResult Success<T>(string requestId, T value) => new() { RequestId = requestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)) };
    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string message) => new() { RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = message, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), JsonOptions)) };

    private sealed record AgentArtifactReadRequest(Guid? ArtifactId = null, bool IncludeArchived = false);
    private sealed record AgentArtifactRevisionRequest(Guid ArtifactId, Guid ExpectedBaseRevisionId, string Content, string IdempotencyKey);
    private sealed record AgentArtifactSubmitRequest(Guid ArtifactId, Guid RevisionId, string IdempotencyKey, Guid? ConversationId = null, Guid? ReviewerOrganizationUserId = null);
    private sealed record AgentArtifactDecisionRequest(Guid ArtifactId, Guid RevisionId, string Decision, string? Comment, string IdempotencyKey, Guid? EvidenceConversationMessageId = null);
    private sealed record AgentArtifactAccessRequest(Guid ArtifactId, IReadOnlyList<string> Actions, string Justification, string IdempotencyKey, DateTimeOffset? ExpiresAt = null);
    private sealed record AgentArtifactPackageReadRequest(Guid PackageId);
    private sealed record AgentArtifactPackageMutationRequest(Guid PackageId, string IdempotencyKey);
    private sealed record ChatDecisionEvidence(Guid OrganizationUserId);
}
