using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Security;
using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Authorizes assignment-scoped source-control operations and delegates them to CSweet.GitHost.
/// This process never handles provider credentials and never executes Git or repository code.
/// </summary>
public sealed class GitWorkspaceCapabilityHandler(
    CSweetDbContext db,
    ITrustedGitHostClient gitHost,
    IScopedActionAuthorizationService authorization,
    ISourceControlDecisionSigner decisionSigner) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Handled =
    [
        GitWorkspaceCapabilities.Prepare,
        GitWorkspaceCapabilities.Refresh,
        GitWorkspaceCapabilities.Inspect,
        GitWorkspaceCapabilities.Publish,
        GitWorkspaceCapabilities.Cleanup,
        GitWorkspaceCapabilities.ListLocks,
        GitWorkspaceCapabilities.LockFile,
        GitWorkspaceCapabilities.UnlockFile,
        GitMergeCapabilities.Review,
        GitMergeCapabilities.Authorize,
        SourceControlCapabilities.TeamRepositoryOptions,
        SourceControlCapabilities.ProvisionRepository
    ];

    public bool CanHandle(string capability) => Handled.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
        {
            yield return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation capability grant does not include '{request.Capability}'.");
            yield break;
        }
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
        {
            yield return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The authenticated runtime identity is invalid.");
            yield break;
        }

        CapabilityResult response;
        try
        {
            object value = request.Capability switch
            {
                GitWorkspaceCapabilities.Prepare => await PrepareAsync(
                    organizationId, installationId,
                    Read<PrepareGitWorkspaceRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.ListLocks => await ListLocksAsync(organizationId, installationId, Read<ListGitWorkspaceLocksRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.LockFile => await LockFileAsync(organizationId, installationId, Read<LockGitWorkspaceFileRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.UnlockFile => await UnlockFileAsync(organizationId, installationId, Read<UnlockGitWorkspaceFileRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.Refresh => await RefreshAsync(
                    organizationId, installationId,
                    Read<RefreshGitWorkspaceRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.Inspect => await InspectAsync(
                    organizationId, installationId,
                    Read<InspectGitWorkspaceRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.Publish => await PublishAsync(
                    organizationId, installationId,
                    Read<PublishGitWorkspaceRequest>(request), cancellationToken),
                GitWorkspaceCapabilities.Cleanup => await CleanupAsync(
                    organizationId, installationId,
                    Read<CleanupGitWorkspaceRequest>(request), cancellationToken),
                GitMergeCapabilities.Review => await ReviewMergeAsync(
                    organizationId, installationId,
                    Read<ReviewGitMergeRequest>(request), cancellationToken),
                GitMergeCapabilities.Authorize => await AuthorizeMergeAsync(
                    organizationId, installationId,
                    Read<AuthorizeGitMergeRequest>(request), cancellationToken),
                SourceControlCapabilities.TeamRepositoryOptions => await ListTeamRepositoryOptionsAsync(
                    organizationId, installationId,
                    Read<TeamRepositoryOptionsRequest>(request), cancellationToken),
                SourceControlCapabilities.ProvisionRepository => await ProvisionRepositoryAsync(
                    organizationId, installationId,
                    Read<ProvisionSourceControlRepositoryRequest>(request), cancellationToken),
                _ => throw new KeyNotFoundException("The source-control capability is not implemented.")
            };
            response = Success(request.RequestId, value);
        }
        catch (JsonException)
        {
            response = Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The capability payload is not valid JSON.");
        }
        catch (UnauthorizedAccessException exception)
        {
            response = Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            response = Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message);
        }
        catch (ArgumentException exception)
        {
            response = Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            response = Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        yield return response;
    }

    private async Task<IReadOnlyList<TeamRepositoryOption>> ListTeamRepositoryOptionsAsync(
        Guid organizationId,
        Guid installationId,
        TeamRepositoryOptionsRequest input,
        CancellationToken cancellationToken)
    {
        await RequireActiveTeamMemberAsync(
            organizationId, installationId, input.TeamId, cancellationToken);
        await RequireAuthorizationAsync(
            organizationId, installationId,
            SourceControlCapabilities.TeamRepositoryOptions,
            GrantScopeKind.Team, input.TeamId, cancellationToken);

        return await (
            from policy in db.TeamRepositoryPolicies.AsNoTracking()
            join repository in db.SourceControlRepositories.AsNoTracking()
                on new { policy.OrganizationId, Id = policy.RepositoryId }
                equals new { repository.OrganizationId, repository.Id }
            join connection in db.SourceControlConnections.AsNoTracking()
                on new { repository.OrganizationId, Id = repository.ConnectionId }
                equals new { connection.OrganizationId, connection.Id }
            where policy.OrganizationId == organizationId &&
                  policy.TeamId == input.TeamId &&
                  policy.DisabledAt == null &&
                  repository.Status == SourceControlRepositoryStatus.Ready &&
                  repository.ArchivedAt == null &&
                  connection.Status == SourceControlConnectionStatus.Connected
            orderby policy.IsPrimary descending, repository.Name
            select new TeamRepositoryOption(
                repository.Id,
                repository.Name,
                connection.Provider.ToString(),
                repository.CanonicalPath,
                repository.DefaultBranch,
                (connection.Provider == SourceControlProvider.GitHub || connection.Provider == SourceControlProvider.InternalGit)
                    ? GitDeliveryKinds.PullRequest
                    : GitDeliveryKinds.BranchOnly))
            .ToListAsync(cancellationToken);
    }

    private async Task<RepositoryProvisioningResult> ProvisionRepositoryAsync(
        Guid organizationId,
        Guid installationId,
        ProvisionSourceControlRepositoryRequest input,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        RequireBounded(input.ProjectDisplayName, 160, "project display name");
        if (input.Description?.Length > 350)
            throw new ArgumentException("The project description is too long.");
        if (input.ProductOrWorkstreamId == Guid.Empty)
            throw new ArgumentException("A workstream is required.");

        await RequireAuthorizationAsync(
            organizationId, installationId,
            SourceControlCapabilities.ProvisionRepository,
            GrantScopeKind.Organization, organizationId, cancellationToken);
        var caller = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException(
            "The installation has no active organization identity.");
        if (!await db.Workstreams.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.Id == input.ProductOrWorkstreamId,
                cancellationToken))
            throw new KeyNotFoundException("The requested workstream was not found.");

        var replay = await db.RepositoryProvisioningRequests.AsNoTracking().Include(r => r.Connection)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                                       x.IdempotencyKey == input.IdempotencyKey,
                cancellationToken);
        if (replay is not null)
        {
            if (replay.RequestedByAgentInstallationId != installationId || replay.WorkstreamId != input.ProductOrWorkstreamId ||
                replay.ProjectDisplayName != input.ProjectDisplayName.Trim() || replay.Description != (input.Description?.Trim() ?? "") ||
                (input.TemplateId != Guid.Empty && replay.TemplateId != input.TemplateId) ||
                (replay.UsedBusinessDefault is { } usedDefault ? usedDefault != (input.TemplateId == Guid.Empty) :
                    input.TemplateId == Guid.Empty && replay.Connection?.Provider != SourceControlProvider.InternalGit))
                throw new InvalidOperationException("The provisioning key was already used for a different request.");
            return ToProvisioningResult(replay);
        }
        var templateId = input.TemplateId == Guid.Empty
            ? await CSweet.Infrastructure.SourceControl.BusinessSourceControlDefaultResolver.ResolveAsync(db, organizationId, cancellationToken)
            : input.TemplateId;

        var template = await db.SourceControlRepositoryTemplates.AsNoTracking().Include(t => t.Connection)
            .SingleOrDefaultAsync(t => t.OrganizationId == organizationId && t.IsEnabled &&
                t.Id == templateId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The repository template is unavailable or disabled.");
        var policy = await db.RepositoryProvisioningPolicies.AsTracking().Include(p => p.Connection)
            .SingleOrDefaultAsync(p => p.OrganizationId == organizationId && p.ConnectionId == template.ConnectionId && p.IsEnabled &&
                p.Connection!.Status == SourceControlConnectionStatus.Connected, cancellationToken);
        if (policy is null) return new RepositoryProvisioningResult(Guid.Empty, "Blocked", null, null, "Repository creation is disabled for this connection.");
        var approvedTemplateIds = JsonSerializer.Deserialize<IReadOnlyList<Guid>>(policy.ApprovedTemplatesJson, JsonOptions) ?? [];
        if (!approvedTemplateIds.Contains(template.Id)) throw new UnauthorizedAccessException("The template is not approved by this business policy.");
        if (!CSweet.Infrastructure.SourceControl.BusinessSourceControlDefaultResolver.SupportsCreation(policy.Connection!))
            return new RepositoryProvisioningResult(Guid.Empty, "Blocked", null, null, "The selected connection is not ready for repository creation.");
        var createdCount = await db.SourceControlRepositories.AsNoTracking().CountAsync(x =>
            x.OrganizationId == organizationId && x.ConnectionId == policy.ConnectionId &&
            x.IsManaged && x.ArchivedAt == null,
            cancellationToken);
        var reservedCount = await db.RepositoryProvisioningRequests.AsNoTracking().CountAsync(r => r.OrganizationId == organizationId &&
            r.ConnectionId == policy.ConnectionId && r.RepositoryId == null && (r.Status == RepositoryProvisioningStatus.Pending ||
                r.Status == RepositoryProvisioningStatus.Provisioning || r.Status == RepositoryProvisioningStatus.AwaitingApproval), cancellationToken);
        if (createdCount + reservedCount >= policy.MaximumRepositories)
            return new RepositoryProvisioningResult(
                Guid.Empty, "Blocked", null, null,
                "The repository creation quota has been reached.");

        var assignmentTime = DateTimeOffset.UtcNow;
        var teamId = policy.DefaultTeamId;
        // Both providers require an active team for immediate agent handoff.
        {
            var teams = await (from membership in db.TeamMemberships.AsNoTracking()
                join team in db.OrganizationTeams.AsNoTracking() on membership.TeamId equals team.Id
                where membership.OrganizationId == organizationId && membership.OrganizationUserId == caller.Id && membership.EndedAt == null &&
                    team.OrganizationId == organizationId && team.ArchivedAt == null &&
                    db.WorkstreamTeamAssignments.Any(a => a.OrganizationId == organizationId &&
                        a.WorkstreamId == input.ProductOrWorkstreamId && a.TeamId == team.Id &&
                        a.StartsAt <= assignmentTime && (a.EndsAt == null || a.EndsAt > assignmentTime))
                select team.Id).Distinct().ToListAsync(cancellationToken);
            if (teamId is null && teams.Count == 1) teamId = teams[0];
            if (teamId is null || !teams.Contains(teamId.Value))
                return new RepositoryProvisioningResult(Guid.Empty, "Blocked", null, null, "Select a provisioning team assigned to this product that the requesting agent belongs to in Source Control settings.");
        }
        var slug = Slug(input.ProjectDisplayName);
        var repositoryName = string.IsNullOrWhiteSpace(policy.NamePrefix)
            ? slug
            : $"{policy.NamePrefix.Trim().TrimEnd('-')}-{slug}";
        if (repositoryName.Length > 100)
            repositoryName = repositoryName[..100].TrimEnd('-');
        var now = DateTimeOffset.UtcNow;
        var provisioning = new RepositoryProvisioningRequest
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ConnectionId = policy.ConnectionId,
            PolicyId = policy.Id,
            RequestedByOrganizationUserId = caller.Id,
            RequestedByAgentInstallationId = installationId,
            WorkstreamId = input.ProductOrWorkstreamId,
            TeamId = teamId,
            TemplateId = template.Id,
            UsedBusinessDefault = input.TemplateId == Guid.Empty,
            PolicyRevision = policy.Revision,
            ProjectDisplayName = input.ProjectDisplayName.Trim(),
            Description = input.Description?.Trim() ?? string.Empty,
            RepositoryName = repositoryName,
            IdempotencyKey = input.IdempotencyKey,
            Status = policy.RequiresManagerApproval
                ? RepositoryProvisioningStatus.AwaitingApproval
                : RepositoryProvisioningStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        policy.Connection!.Revision++; // Serialize quota reservations using the existing optimistic concurrency token.
        policy.Connection.UpdatedAt = now;
        db.RepositoryProvisioningRequests.Add(provisioning);
        if (policy.RequiresManagerApproval)
        {
            var approval = new SourceControlApproval
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Kind = SourceControlApprovalKind.RepositoryProvisioning,
                Status = CSweet.Domain.Core.ApprovalStatus.Pending,
                RequestedByOrganizationUserId = caller.Id,
                RequestedByAgentInstallationId = installationId,
                ProvisioningRequestId = provisioning.Id,
                IdempotencyKey = $"provision-approval:{provisioning.Id:N}",
                CreatedAt = now,
                UpdatedAt = now
            };
            provisioning.ApprovalId = approval.Id;
            db.SourceControlApprovals.Add(approval);
            var approvers = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(candidate => candidate.OrganizationId == organizationId &&
                                    candidate.IsActive &&
                                    candidate.EmployeeType == EmployeeType.Human &&
                                    candidate.PermissionLevel >= OrganizationPermissionLevel.Manager)
                .Select(candidate => candidate.Id)
                .ToListAsync(cancellationToken);
            foreach (var approverId in approvers)
            {
                db.UserNotifications.Add(new UserNotification
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    RecipientOrganizationUserId = approverId,
                    OriginatingAgentOrganizationUserId = caller.Id,
                    Severity = NotificationSeverity.Important,
                    Category = "RepositoryProvisioningApproval",
                    Title = "New code project approval needed",
                    Body = $"Review the private code project {repositoryName} for {policy.Connection!.AccountLogin}.",
                    ActionUri = $"/organizations/{organizationId:D}/approvals",
                    DeduplicationKey = $"source-control-approval:{approval.Id:N}:{approverId:N}",
                    CreatedAt = now
                });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToProvisioningResult(provisioning);
    }

    private async Task<GitWorkspaceResult> PrepareAsync(
        Guid organizationId,
        Guid installationId,
        PrepareGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRequest(input.WorkItemId, input.AssignmentRevision, input.IdempotencyKey);
        var context = await RequireAssignmentContextAsync(
            organizationId, installationId, input.WorkItemId,
            input.AssignmentRevision, GitWorkspaceCapabilities.Prepare, cancellationToken);

        var existing = await db.SourceControlWorkspaces.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId &&
            x.WorkItemId == input.WorkItemId &&
            x.AssignmentRevision == input.AssignmentRevision,
            cancellationToken);
        if (existing is not null && existing.Status == SourceControlWorkspaceStatus.Ready)
        {
            return ToWorkspaceResult(existing, context, AgentWorkspacePath(existing), true);
        }

        var now = DateTimeOffset.UtcNow;
        var workspace = existing ?? new SourceControlWorkspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamId = context.TeamId,
            RepositoryId = context.Repository.Id,
            AgentInstallationId = installationId,
            WorkItemId = input.WorkItemId,
            AssignmentRevision = input.AssignmentRevision,
            BranchName = DeterministicBranch(context.Item),
            Status = SourceControlWorkspaceStatus.Preparing,
            CreatedAt = now
        };
        if (existing is null) db.SourceControlWorkspaces.Add(workspace);
        workspace.Status = SourceControlWorkspaceStatus.Preparing;
        workspace.LastError = null;
        workspace.UpdatedAt = now;
        workspace.Revision++;
        await db.SaveChangesAsync(cancellationToken);

        TrustedWorkspaceMaterialization materialized;
        try
        {
            materialized = await gitHost.PrepareAsync(
                new TrustedWorkspacePrepareRequest(
                    organizationId, installationId, context.Repository.Id, workspace.Id,
                    input.WorkItemId, input.AssignmentRevision, workspace.BranchName,
                    context.ExpectedCommitSha,
                    input.IdempotencyKey),
                cancellationToken);
        }
        catch
        {
            workspace.Status = SourceControlWorkspaceStatus.Failed;
            workspace.LastError = "Trusted workspace materialization failed.";
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            workspace.Revision++;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        ValidateAgentWorkspacePath(materialized.AgentWorkspacePath, workspace.Id);
        workspace.WorkspaceKey = RequireBounded(materialized.WorkspaceKey, 256, "workspace key");
        workspace.BaseCommitSha = ValidateCommitSha(materialized.BaseCommitSha);
        if (context.ExpectedCommitSha is not null &&
            !FixedTimeEquals(workspace.BaseCommitSha, context.ExpectedCommitSha))
            throw new InvalidOperationException(
                "GitHost materialized a commit other than the exact assigned QA commit.");
        workspace.Status = SourceControlWorkspaceStatus.Ready;
        workspace.LastError = null;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToWorkspaceResult(
            workspace, context, materialized.AgentWorkspacePath, materialized.Resumed);
    }

    private async Task<GitWorkspaceRefreshResult> RefreshAsync(
        Guid organizationId,
        Guid installationId,
        RefreshGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRevision(input.AssignmentRevision);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var context = await RequireWorkspaceContextAsync(
            organizationId, installationId, input.WorkspaceId,
            input.AssignmentRevision, GitWorkspaceCapabilities.Refresh, cancellationToken);
        var result = await gitHost.RefreshAsync(
            Operation(context, input.IdempotencyKey), cancellationToken);
        context.Workspace.BaseCommitSha = ValidateCommitSha(result.BaseCommitSha);
        context.Workspace.UpdatedAt = DateTimeOffset.UtcNow;
        context.Workspace.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new GitWorkspaceRefreshResult(
            context.Workspace.Id, result.Status, result.BaseCommitSha,
            result.Conflicts.Take(100).ToList());
    }

    private Task<GitWorkspaceLockResult> ListLocksAsync(Guid business, Guid installation, ListGitWorkspaceLocksRequest request, CancellationToken ct) =>
        WorkspaceLocksAsync(business, installation, request.WorkspaceId, request.AssignmentRevision, GitWorkspaceCapabilities.ListLocks, "list", "list-locks", null, null, request.Cursor, ct);

    private Task<GitWorkspaceLockResult> LockFileAsync(Guid business, Guid installation, LockGitWorkspaceFileRequest request, CancellationToken ct) =>
        WorkspaceLocksAsync(business, installation, request.WorkspaceId, request.AssignmentRevision, GitWorkspaceCapabilities.LockFile, "create", request.IdempotencyKey, request.Path, null, null, ct);

    private Task<GitWorkspaceLockResult> UnlockFileAsync(Guid business, Guid installation, UnlockGitWorkspaceFileRequest request, CancellationToken ct) =>
        WorkspaceLocksAsync(business, installation, request.WorkspaceId, request.AssignmentRevision, GitWorkspaceCapabilities.UnlockFile, "unlock", request.IdempotencyKey, null, request.LockId, null, ct);

    private async Task<GitWorkspaceLockResult> WorkspaceLocksAsync(Guid business, Guid installation, Guid workspace, long revision,
        string capability, string operation, string key, string? path, string? id, string? cursor, CancellationToken ct)
    {
        ValidateAssignmentRevision(revision); ValidateIdempotencyKey(key);
        if (operation == "create") RequireBounded(path ?? "", 1024, "lock path");
        if (operation == "unlock" && !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid file lock identity.");
        if (cursor is not null && !Guid.TryParseExact(cursor, "N", out _)) throw new ArgumentException("Invalid lock page cursor.");
        var context = await RequireWorkspaceContextAsync(business, installation, workspace, revision, capability, ct);
        return await gitHost.LocksAsync(Operation(context, key), operation, path, id, cursor, ct);
    }

    private async Task<GitWorkspaceInspection> InspectAsync(
        Guid organizationId,
        Guid installationId,
        InspectGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRevision(input.AssignmentRevision);
        var context = await RequireWorkspaceContextAsync(
            organizationId, installationId, input.WorkspaceId,
            input.AssignmentRevision, GitWorkspaceCapabilities.Inspect, cancellationToken);
        return await gitHost.InspectAsync(
            Operation(context, $"inspect:{context.Workspace.Revision}"), cancellationToken);
    }

    private async Task<GitWorkspacePublication> PublishAsync(
        Guid organizationId,
        Guid installationId,
        PublishGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRevision(input.AssignmentRevision);
        ValidateIdempotencyKey(input.IdempotencyKey);
        RequireBounded(input.CommitMessage, 512, "commit message");
        RequireBounded(input.ProposedChangeTitle, 256, "proposed change title");
        if (input.ProposedChangeBody.Length > 32_768)
            throw new ArgumentException("The proposed change body is too long.");
        var validations = (input.Validations ?? []).Take(100).ToList();
        if (validations.Count == 0 || validations.Any(x => !x.Succeeded || x.ExitCode != 0))
            throw new InvalidOperationException("Publication requires successful validation evidence.");

        var context = await RequireWorkspaceContextAsync(
            organizationId, installationId, input.WorkspaceId,
            input.AssignmentRevision, GitWorkspaceCapabilities.Publish, cancellationToken);
        var result = await gitHost.PublishAsync(
            new TrustedWorkspacePublishRequest(
                Operation(context, input.IdempotencyKey),
                input.CommitMessage,
                input.ProposedChangeTitle,
                input.ProposedChangeBody,
                validations),
            cancellationToken);
        var commitSha = ValidateCommitSha(result.CommitSha);
        if (!string.Equals(result.BranchName, context.Workspace.BranchName, StringComparison.Ordinal))
            throw new InvalidOperationException("GitHost returned a non-authorized branch.");
        if (result.DeliveryKind == GitDeliveryKinds.PullRequest && result.PullRequestUrl is null)
            throw new InvalidOperationException("GitHub publication did not return a proposed-change URL.");
        if (result.DeliveryKind == GitDeliveryKinds.BranchOnly && result.PullRequestUrl is not null)
            throw new InvalidOperationException("Branch-only publication returned an unexpected pull request.");

        var replay = await db.SourceControlPublications.AsNoTracking().SingleOrDefaultAsync(p =>
            p.OrganizationId == organizationId && p.WorkspaceId == context.Workspace.Id && p.CommitSha == commitSha, cancellationToken);
        if (replay is not null)
            return new(replay.Id, context.Workspace.Id, context.Repository.Id, result.Provider, result.DeliveryKind,
                result.BranchName, commitSha, result.PullRequestUrl, replay.Status.ToString());
        var now = DateTimeOffset.UtcNow;
        var superseded = await (
            from prior in db.SourceControlPublications
            join priorWorkspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { prior.OrganizationId, Id = prior.WorkspaceId }
                equals new { priorWorkspace.OrganizationId, priorWorkspace.Id }
            where prior.OrganizationId == organizationId &&
                  priorWorkspace.WorkItemId == context.Workspace.WorkItemId &&
                  priorWorkspace.AssignmentRevision == context.Workspace.AssignmentRevision &&
                  prior.Status != SourceControlPublicationStatus.Merged &&
                  prior.Status != SourceControlPublicationStatus.BranchPublishedExternalMerge &&
                  prior.Status != SourceControlPublicationStatus.Superseded
            select prior)
            .AsTracking()
            .ToListAsync(cancellationToken);
        foreach (var prior in superseded)
        {
            prior.Status = SourceControlPublicationStatus.Superseded;
            prior.UpdatedAt = now;
            prior.Revision++;
        }
        if (superseded.Count > 0)
        {
            var supersededIds = superseded.Select(x => x.Id).ToArray();
            var priorValidations = await db.SourceControlValidations
                .Where(x => supersededIds.Contains(x.PublicationId) &&
                            x.Status != SourceControlValidationStatus.Superseded)
                .ToListAsync(cancellationToken);
            foreach (var priorValidation in priorValidations)
            {
                priorValidation.Status = SourceControlValidationStatus.Superseded;
                priorValidation.SupersededAt = now;
                priorValidation.UpdatedAt = now;
            }
        }
        var publication = new SourceControlPublication
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkspaceId = context.Workspace.Id,
            RepositoryId = context.Workspace.RepositoryId,
            CommitSha = commitSha,
            TargetBranch = context.Repository.DefaultBranch,
            TicketBranch = context.Workspace.BranchName,
            PullRequestUrl = result.PullRequestUrl?.ToString(),
            Status = result.DeliveryKind == GitDeliveryKinds.BranchOnly
                ? SourceControlPublicationStatus.BranchPublishedExternalMerge
                : SourceControlPublicationStatus.AwaitingValidation,
            ChangedFilesJson = JsonSerializer.Serialize(result.ChangedFiles ?? [], JsonOptions),
            ValidationResultsJson = JsonSerializer.Serialize(validations, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SourceControlPublications.Add(publication);
        context.Workspace.Status = SourceControlWorkspaceStatus.Published;
        context.Workspace.BaseCommitSha = commitSha;
        context.Workspace.UpdatedAt = now;
        context.Workspace.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new GitWorkspacePublication(
            publication.Id,
            context.Workspace.Id,
            context.Repository.Id,
            result.Provider,
            result.DeliveryKind,
            result.BranchName,
            commitSha,
            result.PullRequestUrl,
            publication.Status.ToString());
    }

    private async Task<GitWorkspaceCleanupResult> CleanupAsync(
        Guid organizationId,
        Guid installationId,
        CleanupGitWorkspaceRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRevision(input.AssignmentRevision);
        var context = await RequireWorkspaceContextAsync(
            organizationId, installationId, input.WorkspaceId,
            input.AssignmentRevision, GitWorkspaceCapabilities.Cleanup, cancellationToken);
        var result = await gitHost.CleanupAsync(
            new TrustedWorkspaceCleanupRequest(
                Operation(context, $"cleanup:{context.Workspace.Revision}"),
                input.RetainOnFailure),
            cancellationToken);
        context.Workspace.Status = result.Removed
            ? SourceControlWorkspaceStatus.Removed
            : context.Workspace.Status;
        context.Workspace.RetainUntil = result.RetainUntil;
        context.Workspace.UpdatedAt = DateTimeOffset.UtcNow;
        context.Workspace.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<GitMergeReview> ReviewMergeAsync(
        Guid organizationId,
        Guid installationId,
        ReviewGitMergeRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRequest(input.WorkItemId, input.AssignmentRevision, input.IdempotencyKey);
        var lead = await RequireTeamLeadAsync(
            organizationId, installationId, input.WorkItemId,
            input.AssignmentRevision, GitMergeCapabilities.Review, cancellationToken);
        var publication = await LatestPublicationAsync(
            organizationId, input.WorkItemId, input.AssignmentRevision, cancellationToken);
        var evidenceJson = await db.SourceControlValidations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.PublicationId == publication.Id &&
                        x.CommitSha == publication.CommitSha &&
                        x.Status == SourceControlValidationStatus.Passed &&
                        x.SupersededAt == null)
            .Select(x => x.ResultsJson)
            .ToListAsync(cancellationToken);
        var evidence = evidenceJson
            .SelectMany(x => JsonSerializer.Deserialize<List<GitValidationResult>>(
                x, JsonOptions) ?? [])
            .Take(100)
            .ToList();
        if (evidence.Count == 0)
            throw new InvalidOperationException("The exact candidate SHA does not have passing QA evidence.");
        var repositoryName = await db.SourceControlRepositories.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == publication.RepositoryId)
            .Select(x => x.Name)
            .SingleAsync(cancellationToken);
        return new GitMergeReview(
            publication.Id, publication.RepositoryId, input.WorkItemId,
            repositoryName, publication.CommitSha,
            Uri.TryCreate(publication.PullRequestUrl, UriKind.Absolute, out var pr) ? pr : null,
            $"Candidate {publication.CommitSha[..Math.Min(12, publication.CommitSha.Length)]} for team {lead.TeamId:D}.",
            evidence, JsonSerializer.Deserialize<List<string>>(publication.ChangedFilesJson, JsonOptions) ?? [], publication.Status.ToString());
    }

    private async Task<GitMergeAuthorizationResult> AuthorizeMergeAsync(
        Guid organizationId,
        Guid installationId,
        AuthorizeGitMergeRequest input,
        CancellationToken cancellationToken)
    {
        ValidateAssignmentRequest(input.WorkItemId, input.AssignmentRevision, input.IdempotencyKey);
        if (input.Decision is not (GitMergeDecisions.Approve or GitMergeDecisions.Reject))
            throw new ArgumentException("The merge decision must be Approve or Reject.");
        if (input.Decision == GitMergeDecisions.Reject && string.IsNullOrWhiteSpace(input.Feedback))
            throw new ArgumentException("A rejected merge requires feedback.");
        var lead = await RequireTeamLeadAsync(
            organizationId, installationId, input.WorkItemId,
            input.AssignmentRevision, GitMergeCapabilities.Authorize, cancellationToken);
        var publication = await LatestPublicationAsync(
            organizationId, input.WorkItemId, input.AssignmentRevision, cancellationToken);
        if (publication.Id != input.PublicationId ||
            !FixedTimeEquals(publication.CommitSha, ValidateCommitSha(input.CandidateCommitSha)))
            throw new InvalidOperationException("The merge candidate changed; review the current exact SHA.");

        var policy = await db.TeamRepositoryPolicies.SingleAsync(x =>
            x.OrganizationId == organizationId && x.TeamId == lead.TeamId &&
            x.RepositoryId == publication.RepositoryId && x.DisabledAt == null,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (input.Decision == GitMergeDecisions.Reject)
        {
            publication.Status = SourceControlPublicationStatus.Superseded;
            publication.UpdatedAt = now;
            publication.Revision++;
            await db.SaveChangesAsync(cancellationToken);
            return new GitMergeAuthorizationResult(
                publication.Id, publication.CommitSha, input.Decision,
                publication.Status.ToString(), now, null);
        }
        var hasPassingQa = await db.SourceControlValidations.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.PublicationId == publication.Id &&
            x.CommitSha == publication.CommitSha &&
            x.Status == SourceControlValidationStatus.Passed && x.SupersededAt == null,
            cancellationToken);
        if (!hasPassingQa)
            throw new InvalidOperationException("The exact candidate SHA does not have passing QA evidence.");
        var expiresAt = now.AddHours(24);
        var authorizationRecord = new SourceControlMergeAuthorization
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PublicationId = publication.Id,
            AuthorizedByOrganizationUserId = lead.OrganizationUserId,
            CommitSha = publication.CommitSha,
            TeamPolicyRevision = policy.Revision,
            AuthorizedAt = now,
            ExpiresAt = expiresAt
        };
        authorizationRecord.DecisionSignature = decisionSigner.Sign(
            new SourceControlMergeDecision(
                organizationId, publication.Id, publication.CommitSha,
                lead.OrganizationUserId, policy.Revision, now, expiresAt));
        db.SourceControlMergeAuthorizations.Add(authorizationRecord);
        publication.Status = policy.MergeApprovalMode == TeamMergeApprovalMode.LeadAuthorizedAutoMerge
            ? SourceControlPublicationStatus.ReadyToMerge
            : SourceControlPublicationStatus.AwaitingAdministratorApproval;
        publication.UpdatedAt = now;
        publication.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new GitMergeAuthorizationResult(
            publication.Id, publication.CommitSha, input.Decision,
            publication.Status.ToString(), now, null);
    }

    private async Task<AssignmentContext> RequireAssignmentContextAsync(
        Guid organizationId,
        Guid installationId,
        Guid workItemId,
        long assignmentRevision,
        string action,
        CancellationToken cancellationToken)
    {
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workItemId,
            cancellationToken) ?? throw new KeyNotFoundException("The assigned work item was not found.");
        if (item.AssignmentRevision != assignmentRevision)
            throw new UnauthorizedAccessException("The source-control assignment revision is stale.");
        var activeStageKey = await (
            from stage in db.WorkStageExecutions.AsNoTracking()
            join itemExecution in db.WorkItemExecutions.AsNoTracking()
                on stage.ItemExecutionId equals itemExecution.Id
            where itemExecution.WorkItemId == workItemId &&
                  stage.AgentInstallationId == installationId &&
                  (stage.Status == WorkStageExecutionStatus.Dispatching ||
                   stage.Status == WorkStageExecutionStatus.Running)
            orderby stage.CreatedAt descending
            select stage.StageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (item.AssignedAgentInstallationId != installationId && activeStageKey is null)
            throw new UnauthorizedAccessException(
                "The source-control assignment belongs to another installation.");
        var development = JsonSerializer.Deserialize<SoftwareDevelopmentBrief>(
            item.DevelopmentBriefJson ?? "null", JsonOptions)
            ?? throw new InvalidOperationException("The work item has no software delivery assignment.");
        var repository = await db.SourceControlRepositories.AsNoTracking()
            .Include(x => x.Connection)
            .SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == development.RepositoryId &&
            x.Status == SourceControlRepositoryStatus.Ready && x.ArchivedAt == null,
            cancellationToken) ?? throw new InvalidOperationException("The assigned repository is not ready.");
        var teamId = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == item.BoardId)
            .Select(x => x.TeamId)
            .SingleAsync(cancellationToken)
            ?? throw new InvalidOperationException("The software board is not assigned to a team.");
        var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.TeamId == teamId &&
            x.RepositoryId == repository.Id && x.DisabledAt == null,
            cancellationToken) ?? throw new InvalidOperationException("The team repository policy is unavailable.");
        await RequireActiveTeamMemberAsync(
            organizationId, installationId, teamId, cancellationToken);
        await RequireAuthorizationAsync(
            organizationId, installationId, action,
            GrantScopeKind.WorkItem, workItemId, cancellationToken);
        string? expectedCommitSha = null;
        if (string.Equals(activeStageKey, "quality", StringComparison.Ordinal))
        {
            expectedCommitSha = await (
                from publication in db.SourceControlPublications.AsNoTracking()
                join workspace in db.SourceControlWorkspaces.AsNoTracking()
                    on new { publication.OrganizationId, Id = publication.WorkspaceId }
                    equals new { workspace.OrganizationId, workspace.Id }
                where publication.OrganizationId == organizationId &&
                      workspace.WorkItemId == workItemId &&
                      workspace.AssignmentRevision == assignmentRevision &&
                      publication.Status != SourceControlPublicationStatus.Superseded
                orderby publication.CreatedAt descending
                select publication.CommitSha)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "QA cannot prepare a workspace until an exact source publication exists.");
            expectedCommitSha = ValidateCommitSha(expectedCommitSha);
        }
        return new AssignmentContext(item, teamId, repository, policy, expectedCommitSha);
    }

    private async Task<WorkspaceContext> RequireWorkspaceContextAsync(
        Guid organizationId,
        Guid installationId,
        Guid workspaceId,
        long assignmentRevision,
        string action,
        CancellationToken cancellationToken)
    {
        var workspace = await db.SourceControlWorkspaces.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workspaceId &&
            x.AgentInstallationId == installationId,
            cancellationToken) ?? throw new KeyNotFoundException("The source-control workspace was not found.");
        if (workspace.AssignmentRevision != assignmentRevision)
            throw new UnauthorizedAccessException("The source-control workspace assignment is stale.");
        var assignment = await RequireAssignmentContextAsync(
            organizationId, installationId, workspace.WorkItemId,
            assignmentRevision, action, cancellationToken);
        if (workspace.RepositoryId != assignment.Repository.Id ||
            workspace.TeamId != assignment.TeamId)
            throw new UnauthorizedAccessException("The source-control workspace no longer matches its assignment.");
        return new WorkspaceContext(workspace, assignment.Repository);
    }

    private async Task<TeamLeadContext> RequireTeamLeadAsync(
        Guid organizationId,
        Guid installationId,
        Guid workItemId,
        long assignmentRevision,
        string action,
        CancellationToken cancellationToken)
    {
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workItemId,
            cancellationToken) ?? throw new KeyNotFoundException("The work item was not found.");
        if (item.AssignmentRevision != assignmentRevision)
            throw new UnauthorizedAccessException("The merge review assignment is stale.");
        var teamId = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == item.BoardId)
            .Select(x => x.TeamId)
            .SingleAsync(cancellationToken)
            ?? throw new InvalidOperationException("The software board is not assigned to a team.");
        var caller = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException("The installation has no active employee identity.");
        var leadId = await db.OrganizationTeams.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == teamId && x.ArchivedAt == null)
            .Select(x => x.LeadOrganizationUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (leadId == Guid.Empty || leadId != caller.Id)
            throw new UnauthorizedAccessException("Only the current canonical team lead may decide this merge.");
        await RequireAuthorizationAsync(
            organizationId, installationId, action,
            GrantScopeKind.WorkItem, workItemId, cancellationToken);
        return new TeamLeadContext(teamId, caller.Id);
    }

    private async Task<SourceControlPublication> LatestPublicationAsync(
        Guid organizationId,
        Guid workItemId,
        long assignmentRevision,
        CancellationToken cancellationToken) =>
        await (
            from publication in db.SourceControlPublications
            join workspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { publication.OrganizationId, Id = publication.WorkspaceId }
                equals new { workspace.OrganizationId, workspace.Id }
            where publication.OrganizationId == organizationId &&
                  workspace.WorkItemId == workItemId &&
                  workspace.AssignmentRevision == assignmentRevision &&
                  publication.Status != SourceControlPublicationStatus.Superseded
            orderby publication.CreatedAt descending
            select publication)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException("No current publication exists for this assignment.");

    private async Task<Guid> RequireActiveTeamMemberAsync(
        Guid organizationId,
        Guid installationId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var organizationUserId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The installation has no active employee identity.");
        if (!await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.TeamId == teamId &&
                x.OrganizationUserId == organizationUserId && x.EndedAt == null,
                cancellationToken))
            throw new UnauthorizedAccessException("The installation is not an active member of this team.");
        return organizationUserId;
    }

    private async Task RequireAuthorizationAsync(
        Guid organizationId,
        Guid installationId,
        string action,
        GrantScopeKind scope,
        Guid scopeId,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(
            organizationId, GrantSubjectKind.AgentInstallation, installationId,
            action, scope, scopeId, cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException("The installation is not authorized for this source-control operation.");
    }

    private static TrustedWorkspaceOperationRequest Operation(
        WorkspaceContext context,
        string idempotencyKey) => new(
            context.Workspace.OrganizationId,
            context.Workspace.RepositoryId,
            context.Workspace.Id,
            context.Workspace.WorkspaceKey,
            context.Workspace.WorkItemId,
            context.Workspace.AssignmentRevision,
            idempotencyKey);

    private static GitWorkspaceResult ToWorkspaceResult(
        SourceControlWorkspace workspace,
        AssignmentContext context,
        string path,
        bool resumed) => new(
            workspace.Id,
            workspace.WorkItemId,
            path,
            workspace.RepositoryId,
            context.Repository.Connection?.Provider.ToString() ?? SourceControlProvider.GenericGit.ToString(),
            context.Repository.Connection?.Provider is SourceControlProvider.GitHub or SourceControlProvider.InternalGit
                ? GitDeliveryKinds.PullRequest
                : GitDeliveryKinds.BranchOnly,
            workspace.BaseCommitSha,
            workspace.Status.ToString(),
            resumed);

    private static string AgentWorkspacePath(SourceControlWorkspace workspace) =>
        $"/workspace/{workspace.WorkItemId:N}/{workspace.AssignmentRevision}";

    private static void ValidateAgentWorkspacePath(string path, Guid workspaceId)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith("/workspace/", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"GitHost returned an invalid agent workspace for {workspaceId:D}.");
    }

    private static string DeterministicBranch(WorkTask item)
    {
        var slug = new string(item.Title.ToLowerInvariant()
            .Select(x => char.IsAsciiLetterOrDigit(x) ? x : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (slug.Length == 0) slug = "work";
        return $"csweet/{item.Id:N}-{slug}";
    }

    private static void ValidateAssignmentRequest(
        Guid workItemId,
        long assignmentRevision,
        string idempotencyKey)
    {
        if (workItemId == Guid.Empty) throw new ArgumentException("A work item is required.");
        ValidateAssignmentRevision(assignmentRevision);
        ValidateIdempotencyKey(idempotencyKey);
    }

    private static void ValidateAssignmentRevision(long value)
    {
        if (value < 1) throw new ArgumentException("The authoritative assignment revision is required.");
    }

    private static RepositoryProvisioningResult ToProvisioningResult(
        RepositoryProvisioningRequest request) => new(
        request.Id,
        request.Status.ToString(),
        request.RepositoryId,
        request.ApprovalId,
        request.Status switch
        {
            RepositoryProvisioningStatus.AwaitingApproval =>
                "A manager or owner must approve this private code project.",
            RepositoryProvisioningStatus.Failed => request.FailureMessage,
            _ => null
        });

    private static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(x => char.IsAsciiLetterOrDigit(x) ? x : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }

    private static void ValidateIdempotencyKey(string value) =>
        RequireBounded(value, 160, "idempotency key");

    private static string ValidateCommitSha(string value)
    {
        value = RequireBounded(value, 64, "commit SHA");
        if (value.Length < 40 || value.Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("The commit SHA is invalid.");
        return value.ToLowerInvariant();
    }

    private static string RequireBounded(string value, int maximum, string label)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Length > maximum)
            throw new ArgumentException($"The {label} must contain 1 to {maximum} characters.");
        return value;
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static T Read<T>(RequestCapability request) =>
        request.Payload.ToElement().Deserialize<T>(JsonOptions)
        ?? throw new JsonException("Capability payload was empty.");

    private static CapabilityResult Success(string requestId, object value) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        Payload = JsonPayload.From(value, JsonOptions)
    };

    private static CapabilityResult Failure(
        string requestId,
        PlatformCapabilityErrorCode code,
        string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        Error = error,
        Payload = JsonPayload.From(new { code = code.ToString(), error }, JsonOptions)
    };

    private sealed record AssignmentContext(
        WorkTask Item,
        Guid TeamId,
        SourceControlRepository Repository,
        TeamRepositoryPolicy Policy,
        string? ExpectedCommitSha);

    private sealed record WorkspaceContext(
        SourceControlWorkspace Workspace,
        SourceControlRepository Repository);

    private sealed record TeamLeadContext(Guid TeamId, Guid OrganizationUserId);
}
