using System.Text.Json;
using CSweet.Agent.Contracts.Packaging;
using CSweet.Agent.SDK;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class SoftwareDevelopmentWorkService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IPluginSecretStore secrets,
    IAgentRuntimeManager runtimeManager) : ISoftwareDevelopmentWorkService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> RequiredAgentCapabilities = new HashSet<string>(
        [
            WorkItemCapabilities.Read,
            WorkItemCapabilities.Start,
            WorkItemCapabilities.Comment,
            WorkItemCapabilities.Complete,
            GitWorkspaceCapabilities.Prepare,
            GitWorkspaceCapabilities.Inspect,
            GitWorkspaceCapabilities.Publish,
            GitWorkspaceCapabilities.Cleanup
        ],
        StringComparer.Ordinal);

    public async Task<IReadOnlyList<GitRepositoryConnectionResponse>> ListConnectionsAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var isMember = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive,
            cancellationToken);
        if (!isMember)
            throw new UnauthorizedAccessException(
                "The current user is not an active organization member.");
        return (await db.GitRepositoryConnections.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken))
            .Select(ToResponse)
            .ToList();
    }

    public async Task<GitRepositoryConnectionResponse> CreateConnectionAsync(
        Guid organizationId,
        Guid applicationUserId,
        CreateGitRepositoryConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireOrganizationManagerAsync(organizationId, applicationUserId, cancellationToken);
        var provider = ParseEnum<GitRepositoryProvider>(request.Provider, nameof(request.Provider));
        var authentication = ParseEnum<GitAuthenticationMode>(
            request.AuthenticationMode, nameof(request.AuthenticationMode));
        var pullRequestProvider = ParseEnum<GitPullRequestProvider>(
            request.PullRequestProvider, nameof(request.PullRequestProvider));
        var uri = ValidateCloneUrl(request.CloneUrl, authentication);
        var hosts = NormalizeHosts(request.AllowedHosts);
        if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The clone URL host must be included in allowedHosts.");
        var ports = request.AllowedPorts.Distinct().Order().ToArray();
        if (ports.Any(x => x is < 1 or > 65535))
            throw new ArgumentException("Allowed ports must be between 1 and 65535.");
        var clonePort = uri.IsDefaultPort
            ? authentication == GitAuthenticationMode.Ssh ? 22 : 443
            : uri.Port;
        if (!ports.Contains(clonePort))
            throw new ArgumentException(
                "The clone URL port must be included in allowedPorts.");
        var permittedRepositoryPath = NormalizeRepositoryPath(request.PermittedRepositoryPath);
        var cloneRepositoryPath = Uri.UnescapeDataString(uri.AbsolutePath)
            .Trim('/')
            .Replace('\\', '/');
        if (cloneRepositoryPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            cloneRepositoryPath = cloneRepositoryPath[..^4];
        if (!string.Equals(
                cloneRepositoryPath,
                permittedRepositoryPath,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The clone URL must resolve to the exact permitted repository path.");
        if (authentication == GitAuthenticationMode.Ssh &&
            (request.SshHostFingerprints is null || request.SshHostFingerprints.Count == 0))
            throw new ArgumentException("SSH connections require at least one known-host fingerprint.");
        if (authentication == GitAuthenticationMode.Anonymous && request.AllowPush)
            throw new ArgumentException("Anonymous repository connections cannot request push authority.");
        if (pullRequestProvider == GitPullRequestProvider.GitHub &&
            provider != GitRepositoryProvider.GitHub)
            throw new ArgumentException("The GitHub pull-request provider requires a GitHub repository.");
        if (provider == GitRepositoryProvider.GitHub &&
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The initial GitHub provider supports repositories hosted on github.com.");

        var now = DateTimeOffset.UtcNow;
        var connection = new GitRepositoryConnection
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = RequireText(request.Name, nameof(request.Name)),
            Provider = provider,
            CloneUrl = uri.AbsoluteUri,
            PermittedRepositoryPath = permittedRepositoryPath,
            AuthenticationMode = authentication,
            AllowedOperations = GitAllowedOperation.ReadFetch |
                (request.AllowPush ? GitAllowedOperation.PushTicketBranch : 0),
            DefaultBranch = ValidateGitReference(request.DefaultBranch, nameof(request.DefaultBranch)),
            PullRequestProvider = pullRequestProvider,
            AllowedHostsJson = JsonSerializer.Serialize(hosts, JsonOptions),
            AllowedPortsJson = JsonSerializer.Serialize(ports, JsonOptions),
            SshHostFingerprintsJson = JsonSerializer.Serialize(
                request.SshHostFingerprints?.Select(RequireFingerprint).Distinct().ToArray() ?? [],
                JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.GitRepositoryConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(connection);
    }

    public async Task GrantConnectionAsync(
        Guid organizationId,
        Guid connectionId,
        Guid applicationUserId,
        GrantGitRepositoryConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireOrganizationManagerAsync(organizationId, applicationUserId, cancellationToken);
        var connection = await RequireConnectionAsync(organizationId, connectionId, cancellationToken);
        var installation = await RequireInstallationAsync(
            organizationId, request.AgentInstallationId, cancellationToken);
        if (request.CanPushTicketBranch &&
            !connection.AllowedOperations.HasFlag(GitAllowedOperation.PushTicketBranch))
            throw new InvalidOperationException("The repository connection does not allow push.");

        var existing = await db.GitRepositoryConnectionGrants.SingleOrDefaultAsync(x =>
            x.RepositoryConnectionId == connectionId &&
            x.AgentInstallationId == installation.Id, cancellationToken);
        if (existing is null)
        {
            existing = new GitRepositoryConnectionGrant
            {
                Id = Guid.NewGuid(),
                RepositoryConnectionId = connectionId,
                AgentInstallationId = installation.Id,
                GrantedAt = DateTimeOffset.UtcNow
            };
            db.GitRepositoryConnectionGrants.Add(existing);
        }
        existing.CanReadFetch = request.CanReadFetch;
        existing.CanPushTicketBranch = request.CanPushTicketBranch;
        existing.RevokedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCredentialAsync(
        Guid organizationId,
        Guid connectionId,
        Guid applicationUserId,
        SetGitRepositoryCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireOrganizationManagerAsync(organizationId, applicationUserId, cancellationToken);
        var connection = await RequireConnectionAsync(organizationId, connectionId, cancellationToken);
        await RequireInstallationAsync(organizationId, request.AgentInstallationId, cancellationToken);
        var allowed = AllowedCredentialComponents(connection);
        if (!allowed.Contains(request.Component))
            throw new ArgumentException(
                $"Credential component '{request.Component}' is not valid for {connection.AuthenticationMode}.");
        if (string.IsNullOrWhiteSpace(request.Value) || request.Value.Length > 65_536)
            throw new ArgumentException(
                "A credential value of at most 65536 characters is required.");
        await secrets.SetAsync(
            request.AgentInstallationId,
            CredentialKey(connectionId, request.Component),
            request.Value,
            cancellationToken);
    }

    public async Task<WorkBoardItemResponse> AssignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        AssignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.");
        ValidateBrief(request.Development);
        var actor = await RequireBoardUpdateAsync(
            organizationId, boardId, applicationUserId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var previous = await db.AgentPlatformEventOutbox.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (previous is not null)
        {
            var priorAssignment = string.Equals(
                    previous.EventType, WorkItemEvents.Assigned, StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<WorkItemAssignedEvent>(
                    previous.DataJson, JsonOptions)
                : null;
            if (priorAssignment is null ||
                priorAssignment.BoardId != boardId ||
                priorAssignment.ItemId != itemId ||
                priorAssignment.AssignedInstallationId != request.AssignedInstallationId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different assignment.");
            var replay = await LoadAssignedItemAsync(
                organizationId, boardId, itemId, cancellationToken);
            var storedBrief = string.IsNullOrWhiteSpace(replay.Item.DevelopmentBriefJson)
                ? null
                : JsonSerializer.Deserialize<SoftwareDevelopmentBrief>(
                    replay.Item.DevelopmentBriefJson, JsonOptions);
            await transaction.RollbackAsync(cancellationToken);
            return ToItemResponse(replay.Item, replay.Employee, storedBrief);
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == boardId && x.Id == itemId,
            cancellationToken)
            ?? throw new KeyNotFoundException("The work item was not found.");
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The work item changed before it could be assigned.");
        if (item.Status == WorkTaskStatus.Running)
            throw new InvalidOperationException("A running development ticket cannot be reassigned in v1.");

        var boardCategories = await db.WorkBoardColumns.AsNoTracking()
            .Where(x => x.BoardId == boardId)
            .Select(x => x.Category)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (!boardCategories.Contains(WorkBoardColumnCategory.ToDo) ||
            !boardCategories.Contains(WorkBoardColumnCategory.InProgress) ||
            !boardCategories.Contains(WorkBoardColumnCategory.Done))
            throw new InvalidOperationException(
                "The board must contain To Do, In Progress, and Done column categories.");
        var currentCategory = await db.WorkBoardColumns.AsNoTracking()
            .Where(x => x.Id == item.BoardColumnId)
            .Select(x => x.Category)
            .SingleAsync(cancellationToken);
        if (currentCategory != WorkBoardColumnCategory.ToDo)
            throw new InvalidOperationException("A development ticket must be in To Do when assigned.");

        var installation = await RequireInstallationAsync(
            organizationId, request.AssignedInstallationId, cancellationToken);
        var employee = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == installation.Id &&
            x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException(
                "The installation is not linked to an active organization employee.");
        var connection = await RequireConnectionAsync(
            organizationId, request.Development.RepositoryConnectionId, cancellationToken);
        var connectionGrant = await db.GitRepositoryConnectionGrants.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.RepositoryConnectionId == connection.Id &&
                x.AgentInstallationId == installation.Id &&
                x.RevokedAt == null, cancellationToken)
            ?? throw new InvalidOperationException(
                "The repository connection is not granted to the selected installation.");
        if (!connectionGrant.CanReadFetch)
            throw new InvalidOperationException("The repository grant does not allow clone/fetch.");
        if (!connectionGrant.CanPushTicketBranch)
            throw new InvalidOperationException(
                "Development assignments require authority to publish the deterministic ticket branch.");

        await ValidateAgentPackageAsync(installation, cancellationToken);
        await ValidateCredentialsAsync(connection, installation.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (item.AssignedAgentInstallationId.HasValue &&
            item.AssignedAgentInstallationId != installation.Id)
        {
            var undispatched = await db.AgentPlatformEventOutbox.Where(x =>
                x.OrganizationId == organizationId &&
                x.TargetInstallationId == item.AssignedAgentInstallationId &&
                x.EventType == WorkItemEvents.Assigned &&
                x.Status == AgentPlatformEventOutboxStatus.Pending)
                .ToListAsync(cancellationToken);
            foreach (var pending in undispatched)
            {
                var pendingAssignment = JsonSerializer.Deserialize<WorkItemAssignedEvent>(
                    pending.DataJson, JsonOptions);
                if (pendingAssignment?.ItemId != item.Id)
                    continue;
                pending.Status = AgentPlatformEventOutboxStatus.Failed;
                pending.LastError = "Assignment superseded before dispatch.";
            }

            var oldGrants = await db.ScopedActionGrants.Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation &&
                x.SubjectId == item.AssignedAgentInstallationId &&
                x.ScopeKind == GrantScopeKind.WorkItem &&
                x.ScopeId == item.Id &&
                x.RevokedAt == null).ToListAsync(cancellationToken);
            foreach (var grant in oldGrants)
            {
                grant.RevokedAt = now;
                grant.Revision++;
            }
        }

        item.AssignedWorkerId = employee.WorkerId;
        item.AssignedEmployeeId = employee.Id;
        item.AssignedAgentInstallationId = installation.Id;
        item.DevelopmentBriefJson = JsonSerializer.Serialize(request.Development, JsonOptions);
        item.AssignmentRevision++;
        item.Revision++;
        item.UpdatedAt = now;

        foreach (var action in new[]
                 {
                     WorkItemActions.Read,
                     WorkItemActions.Start,
                     WorkItemActions.Comment,
                     WorkItemActions.Complete
                 })
        {
            var grant = await db.ScopedActionGrants.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation &&
                x.SubjectId == installation.Id &&
                x.Action == action &&
                x.ScopeKind == GrantScopeKind.WorkItem &&
                x.ScopeId == item.Id &&
                x.RevokedAt == null, cancellationToken);
            if (grant is not null) continue;
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installation.Id,
                Action = action,
                ScopeKind = GrantScopeKind.WorkItem,
                ScopeId = item.Id,
                CanDelegate = false,
                GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = actor.Id,
                GrantedAt = now
            });
        }

        var assignedEvent = new WorkItemAssignedEvent(
            boardId, item.Id, item.AssignmentRevision, installation.Id);
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TargetInstallationId = installation.Id,
            EventType = WorkItemEvents.Assigned,
            DataJson = JsonSerializer.Serialize(assignedEvent, JsonOptions),
            IdempotencyKey = request.IdempotencyKey,
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = item.Id,
            EventType = "item.assigned",
            Action = "work.item.assign",
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            DataJson = JsonSerializer.Serialize(
                new
                {
                    assignedInstallationId = installation.Id,
                    repositoryConnectionId = connection.Id,
                    item.AssignmentRevision
                },
                JsonOptions),
            OccurredAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await runtimeManager.EnsureRuntimeQueuedAsync(
            installation.Id,
            $"Development ticket {item.Id:D} assigned.",
            cancellationToken: cancellationToken);
        return ToItemResponse(item, employee, request.Development);
    }

    public async Task<WorkBoardItemResponse> UnassignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        UnassignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        var actor = await RequireBoardUpdateAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replay = await db.WorkItemActivities.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.BoardId == boardId &&
            x.WorkItemId == itemId &&
            x.ActorKind == GrantSubjectKind.OrganizationUser &&
            x.ActorSubjectId == actor.Id &&
            x.Action == "work.item.unassign" &&
            x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (replay)
        {
            var replayed = await LoadAssignedItemAsync(
                organizationId, boardId, itemId, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return ToItemResponse(replayed.Item, replayed.Employee, null);
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.BoardId == boardId &&
            x.Id == itemId,
            cancellationToken)
            ?? throw new KeyNotFoundException("The work item was not found.");
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                "The work item changed before it could be unassigned.");
        if (!item.AssignedAgentInstallationId.HasValue)
            throw new InvalidOperationException("The work item is not assigned to a developer.");
        var category = await db.WorkBoardColumns.AsNoTracking()
            .Where(x => x.Id == item.BoardColumnId)
            .Select(x => x.Category)
            .SingleAsync(cancellationToken);
        if (item.Status == WorkTaskStatus.Running ||
            category != WorkBoardColumnCategory.ToDo)
            throw new InvalidOperationException(
                "Active development work cannot be unassigned in v1.");

        var installationId = item.AssignedAgentInstallationId.Value;
        var now = DateTimeOffset.UtcNow;
        var grants = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.ScopeKind == GrantScopeKind.WorkItem &&
            x.ScopeId == item.Id &&
            x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = now;
            grant.Revision++;
        }
        var pendingEvents = await db.AgentPlatformEventOutbox.Where(x =>
            x.OrganizationId == organizationId &&
            x.TargetInstallationId == installationId &&
            x.EventType == WorkItemEvents.Assigned &&
            x.Status == AgentPlatformEventOutboxStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var pending in pendingEvents)
        {
            var assignment = JsonSerializer.Deserialize<WorkItemAssignedEvent>(
                pending.DataJson, JsonOptions);
            if (assignment?.ItemId != item.Id)
                continue;
            pending.Status = AgentPlatformEventOutboxStatus.Failed;
            pending.LastError = "Assignment removed before dispatch.";
        }

        item.AssignedWorkerId = null;
        item.AssignedEmployeeId = null;
        item.AssignedAgentInstallationId = null;
        item.DevelopmentBriefJson = null;
        item.AssignmentRevision++;
        item.Revision++;
        item.UpdatedAt = now;
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = item.Id,
            EventType = "item.unassigned",
            Action = "work.item.unassign",
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            IdempotencyKey = request.IdempotencyKey,
            DataJson = JsonSerializer.Serialize(
                new { priorInstallationId = installationId, item.AssignmentRevision },
                JsonOptions),
            OccurredAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToItemResponse(item, null, null);
    }

    private async Task ValidateAgentPackageAsync(
        AgentInstallation installation,
        CancellationToken cancellationToken)
    {
        var package = await db.AgentPackageVersions.AsNoTracking()
            .SingleAsync(x => x.Id == installation.PackageVersionId, cancellationToken);
        var manifest = JsonSerializer.Deserialize<AgentManifest>(package.ManifestJson, JsonOptions)
            ?? throw new InvalidOperationException("The developer package manifest is invalid.");
        if (!manifest.Events.Subscribes.Contains(WorkItemEvents.Assigned, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"The developer package does not subscribe to '{WorkItemEvents.Assigned}'.");
        var requested = manifest.Requires.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredAgentCapabilities.Where(x => !requested.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"The developer package is missing required capabilities: {string.Join(", ", missing)}.");
        if (!string.Equals(
                manifest.Runtime.EnvironmentProfile,
                "software-development-polyglot-v1",
                StringComparison.Ordinal) ||
            !string.Equals(manifest.Runtime.WorkspaceAccess, "ReadWrite", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The developer package must request software-development-polyglot-v1 and ReadWrite workspace access.");
        if (manifest.Runtime.MaximumConcurrentJobs != 1)
            throw new InvalidOperationException(
                "Software Developer installations must execute assigned tickets sequentially.");
    }

    private async Task ValidateCredentialsAsync(
        GitRepositoryConnection connection,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        foreach (var component in CredentialComponents(connection))
        {
            if (await secrets.GetAsync(
                    installationId, CredentialKey(connection.Id, component), cancellationToken) is null)
                throw new InvalidOperationException(
                    $"The repository connection is missing credential component '{component}'.");
        }
    }

    private async Task<OrganizationUser> RequireOrganizationManagerAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive &&
            x.EmployeeType == EmployeeType.Human &&
            (x.PermissionLevel == OrganizationPermissionLevel.Owner ||
             x.PermissionLevel == OrganizationPermissionLevel.Manager), cancellationToken)
        ?? throw new UnauthorizedAccessException(
            "Repository connections require an organization owner or manager.");

    private async Task<OrganizationUser> RequireBoardUpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive &&
            x.EmployeeType == EmployeeType.Human, cancellationToken)
            ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            WorkItemActions.Update,
            GrantScopeKind.Board,
            boardId,
            cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException("The current user cannot assign work on this board.");
        return member;
    }

    private async Task<GitRepositoryConnection> RequireConnectionAsync(
        Guid organizationId,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        await db.GitRepositoryConnections.SingleOrDefaultAsync(x =>
            x.Id == connectionId && x.OrganizationId == organizationId, cancellationToken)
        ?? throw new KeyNotFoundException("The repository connection was not found.");

    private async Task<AgentInstallation> RequireInstallationAsync(
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var organizationBusinessId = organizationId.ToString("D");
        return await db.AgentInstallations.SingleOrDefaultAsync(x =>
            x.Id == installationId &&
            x.BusinessId == organizationBusinessId &&
            x.IsEnabled &&
            x.RevisionStatus == PluginRevisionStatus.Active, cancellationToken)
        ?? throw new KeyNotFoundException("The active agent installation was not found in this organization.");
    }

    private async Task<(WorkTask Item, OrganizationUser? Employee)> LoadAssignedItemAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == boardId && x.Id == itemId,
            cancellationToken)
            ?? throw new KeyNotFoundException("The work item was not found.");
        var employee = item.AssignedEmployeeId.HasValue
            ? await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == item.AssignedEmployeeId, cancellationToken)
            : null;
        return (item, employee);
    }

    private static WorkBoardItemResponse ToItemResponse(
        WorkTask item,
        OrganizationUser? employee,
        SoftwareDevelopmentBrief? development) =>
        new(
            item.Id,
            item.BoardId!.Value,
            item.BoardColumnId!.Value,
            item.ParentWorkTaskId,
            item.SprintId,
            item.Kind.ToString(),
            item.Title,
            item.Description,
            item.Status.ToString(),
            item.Priority.ToString(),
            item.EstimatePoints,
            item.BoardRank,
            item.Revision,
            item.DueDate,
            item.CreatedAt,
            item.UpdatedAt,
            item.AssignedWorkerId,
            item.AssignedEmployeeId,
            item.AssignedAgentInstallationId,
            employee?.DisplayName,
            development,
            item.AssignmentRevision);

    private static GitRepositoryConnectionResponse ToResponse(GitRepositoryConnection connection) =>
        new(
            connection.Id,
            connection.OrganizationId,
            connection.Name,
            connection.Provider.ToString(),
            connection.CloneUrl,
            connection.PermittedRepositoryPath,
            connection.AuthenticationMode.ToString(),
            connection.AllowedOperations.HasFlag(GitAllowedOperation.ReadFetch),
            connection.AllowedOperations.HasFlag(GitAllowedOperation.PushTicketBranch),
            connection.DefaultBranch,
            connection.PullRequestProvider.ToString(),
            Deserialize<string>(connection.AllowedHostsJson),
            Deserialize<int>(connection.AllowedPortsJson),
            Deserialize<string>(connection.SshHostFingerprintsJson),
            connection.CreatedAt,
            connection.UpdatedAt);

    private static IReadOnlyList<T> Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<T>>(json, JsonOptions) ?? [];

    private static Uri ValidateCloneUrl(string cloneUrl, GitAuthenticationMode authentication)
    {
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Clone URL must be absolute.");
        var expectedScheme = authentication == GitAuthenticationMode.Ssh ? "ssh" : "https";
        if (!string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{authentication} repository connections require {expectedScheme} clone URLs.");
        if (authentication == GitAuthenticationMode.Ssh)
        {
            if (uri.UserInfo.Contains(':', StringComparison.Ordinal))
                throw new ArgumentException(
                    "SSH clone URLs may contain a user name but must not contain a password.");
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("HTTPS clone URLs must not contain credentials.");
        }
        return uri;
    }

    private static string[] NormalizeHosts(IReadOnlyList<string> hosts)
    {
        var normalized = hosts
            .Select(x => x.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0 ||
            normalized.Any(x => x is "localhost" || Uri.CheckHostName(x) == UriHostNameType.Unknown))
            throw new ArgumentException("At least one valid non-local allowed host is required.");
        return normalized;
    }

    private static string NormalizeRepositoryPath(string value)
    {
        value = RequireText(value, nameof(value)).Replace('\\', '/').Trim('/');
        if (value.Split('/').Any(x => x is "." or ".." || x.Length == 0))
            throw new ArgumentException("The permitted repository path is invalid.");
        return value;
    }

    private static string ValidateGitReference(string value, string parameter)
    {
        value = RequireText(value, parameter);
        if (value.StartsWith('-') || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace) || value.Any(x => x is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException("The Git reference is invalid.", parameter);
        return value;
    }

    private static void ValidateBrief(SoftwareDevelopmentBrief brief)
    {
        if (brief.RepositoryConnectionId == Guid.Empty)
            throw new ArgumentException("A repository connection is required.");
        if (!string.Equals(
                brief.EnvironmentProfile,
                "software-development-polyglot-v1",
                StringComparison.Ordinal))
            throw new ArgumentException("The supported environment profile is software-development-polyglot-v1.");
        ValidateBriefList(brief.Requirements, "requirement", required: true);
        ValidateBriefList(
            brief.AcceptanceCriteria, "acceptance criterion", required: true);
        ValidateBriefList(brief.Constraints, "constraint", required: false);
        if (!string.IsNullOrWhiteSpace(brief.BaseBranch))
            ValidateGitReference(brief.BaseBranch, nameof(brief.BaseBranch));
    }

    private static void ValidateBriefList(
        IReadOnlyList<string>? values,
        string label,
        bool required)
    {
        if (values is null || values.Count == 0)
        {
            if (required)
                throw new ArgumentException(
                    $"At least one non-empty {label} is required.");
            return;
        }
        if (values.Count > 100 ||
            values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 4_000))
            throw new ArgumentException(
                $"{label} values must contain at most 100 non-empty items of at most 4000 characters.");
    }

    private static IReadOnlySet<string> CredentialComponents(GitRepositoryConnection connection)
    {
        var components = connection.AuthenticationMode switch
        {
            GitAuthenticationMode.Anonymous => new HashSet<string>(StringComparer.Ordinal),
            GitAuthenticationMode.GitHubApp => new HashSet<string>(
                ["github-app-id", "github-installation-id", "github-private-key"],
                StringComparer.Ordinal),
            GitAuthenticationMode.HttpsCredential => new HashSet<string>(
                ["https-token"],
                StringComparer.Ordinal),
            GitAuthenticationMode.Ssh => new HashSet<string>(
                ["ssh-private-key"],
                StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(connection.AuthenticationMode))
        };
        if (connection.AuthenticationMode == GitAuthenticationMode.Ssh &&
            connection.PullRequestProvider == GitPullRequestProvider.GitHub)
            components.Add("github-api-token");
        return components;
    }

    private static IReadOnlySet<string> AllowedCredentialComponents(
        GitRepositoryConnection connection)
    {
        var components = new HashSet<string>(
            CredentialComponents(connection),
            StringComparer.Ordinal);
        if (connection.AuthenticationMode == GitAuthenticationMode.Ssh)
            components.Add("ssh-key-passphrase");
        return components;
    }

    public static string CredentialKey(Guid connectionId, string component) =>
        $"git.connection.{connectionId:N}.{component}";

    private static T ParseEnum<T>(string value, string parameter) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"The value '{value}' is invalid.", parameter);

    private static string RequireText(string value, string parameter) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", parameter);

    private static string RequireFingerprint(string value)
    {
        value = RequireText(value, nameof(value));
        if (!value.StartsWith("SHA256:", StringComparison.Ordinal) || value.Length < 20)
            throw new ArgumentException("SSH host fingerprints must use SHA256 format.");
        return value;
    }
}
