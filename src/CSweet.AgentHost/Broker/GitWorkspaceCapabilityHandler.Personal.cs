using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.SourceControl;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed partial class GitWorkspaceCapabilityHandler
{
    internal const string PersonalReserveCapability = "source-control.personal-work.reserve.v1";
    internal const string PersonalPrepareCapability = "source-control.personal-work.prepare.v1";
    private sealed record PersonalPrepareInput(Guid ItemId, string IdempotencyKey, Guid? SourceWorkItemId = null, Guid? TaskItemId = null);
    // Server-written source binding. Never accept repository IDs or commits from the caller.
    private sealed record PersonalSourceBinding(Guid RepositoryId, string EnvironmentProfile,
        IReadOnlyList<string> Requirements, IReadOnlyList<string> AcceptanceCriteria,
        Guid? PersonalProjectRootId = null, Guid? PersonalSourceWorkItemId = null, string? PersonalSourceCommitSha = null, Guid? PersonalPlanRootId = null);

    private async Task<PersonalRepositoryReservation> ReservePersonalAsync(Guid business, Guid installation,
        ReservePersonalRepositoryRequest input, CancellationToken ct)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (input.ExpectedRevision < 1 || string.IsNullOrWhiteSpace(input.SuggestedName))
            throw new ArgumentException("A repository reservation requires a suggested name and expected ticket revision.");
        var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == input.ItemId && x.OrganizationId == business, ct)
            ?? throw new KeyNotFoundException("Personal ticket not found.");
        await RequirePersonalReservationOwnerAsync(business, installation, item, input.ExpectedRevision, ct);
        var repositoryId = PersonalRepositoryId(item.Id);
        var repository = await db.SourceControlRepositories.Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == repositoryId, ct);
        var created = repository is null;
        if (repository is null)
        {
            var connection = await InternalGitProvisioningDefaults.EnsureAsync(db, business, ct);
            var policy = await db.RepositoryProvisioningPolicies.SingleAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id, ct);
            if (!policy.IsEnabled || policy.RequiresManagerApproval || connection.Status != SourceControlConnectionStatus.Connected)
                throw new InvalidOperationException("The business repository policy requires approval or has disabled automatic creation.");
            var approvedTemplates = JsonSerializer.Deserialize<Guid[]>(policy.ApprovedTemplatesJson, JsonOptions) ?? [];
            var template = await db.SourceControlRepositoryTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == business &&
                x.ConnectionId == connection.Id && x.IsEnabled && approvedTemplates.Contains(x.Id) && x.Name == "empty", ct)
                ?? throw new UnauthorizedAccessException("An empty internal repository template must be approved by the business.");
            var reserved = await db.RepositoryProvisioningRequests.CountAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id &&
                x.RepositoryId == null && (x.Status == RepositoryProvisioningStatus.Pending || x.Status == RepositoryProvisioningStatus.Provisioning ||
                    x.Status == RepositoryProvisioningStatus.AwaitingApproval), ct);
            if (reserved + await db.SourceControlRepositories.CountAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id && x.ArchivedAt == null, ct) >= policy.MaximumRepositories)
                throw new InvalidOperationException("The business repository quota has been reached.");
            var name = await ResolvePersonalRepositoryNameAsync(
                business, connection.Id, repositoryId, item, input.SuggestedName, ct);
            var now = DateTimeOffset.UtcNow;
            repository = new SourceControlRepository { Id = repositoryId, OrganizationId = business, ConnectionId = connection.Id,
                Name = name, Owner = business.ToString("N"), CanonicalPath = $"internal/{business:N}/{name}",
                ExternalRepositoryId = repositoryId.ToString("N"), ProviderRepositoryKey = $"internal:{repositoryId:N}",
                DefaultBranch = template.DefaultBranch, IsPrivate = true, IsManaged = true, Status = SourceControlRepositoryStatus.Provisioning,
                CreatedAt = now, UpdatedAt = now, Connection = connection };
            db.SourceControlRepositories.Add(repository);
            connection.Revision++;
            await db.SaveChangesAsync(ct);
        }
        if (repository.OrganizationId != business || repository.Connection?.Provider != SourceControlProvider.InternalGit || repository.ArchivedAt is not null)
            throw new UnauthorizedAccessException("The personal repository is unavailable.");
        if (repository.Status == SourceControlRepositoryStatus.Provisioning)
        {
            await gitHost.CreatePersonalRepositoryAsync(new(business, installation, item.Id, input.IdempotencyKey), ct);
            repository.Status = SourceControlRepositoryStatus.Ready;
            repository.LastVerifiedAt = DateTimeOffset.UtcNow;
            repository.Revision++;
            await db.SaveChangesAsync(ct);
        }
        return new PersonalRepositoryReservation(repository.Id, repository.Name, repository.Status.ToString(), created);
    }

    private async Task RequirePersonalReservationOwnerAsync(Guid business, Guid installation, WorkTask item, long expectedRevision, CancellationToken ct)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == business &&
            x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
        var approved = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == installation &&
            x.BusinessId == business.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
        var capabilities = JsonSerializer.Deserialize<HashSet<string>>(approved?.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (actor is null || !capabilities.Contains(PersonalReserveCapability) || item.Board is not { Kind: WorkBoardKind.Personal, ArchivedAt: null } board ||
            board.OwnerOrganizationUserId != actor.Id || item.AssignedEmployeeId != actor.Id || item.AssignedAgentInstallationId != installation ||
            item.ArchivedAt is not null || item.Status != WorkTaskStatus.Ready || !item.IsExecutable || item.Revision != expectedRevision ||
            item.SourceConversationId is null || item.SourceMessageId is null)
            throw new UnauthorizedAccessException("An owned Ready personal ticket at its expected revision is required.");
    }

    private static string NormalizePersonalRepositoryName(string value)
    {
        var builder = new StringBuilder(50);
        var separator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separator && builder.Length > 0 && builder.Length < 50) builder.Append('-');
                if (builder.Length < 50) builder.Append(character);
                separator = false;
            }
            else separator = true;
        }
        var name = builder.ToString().Trim('-');
        return name.Length >= 3 ? name : "app";
    }
    // A personal ticket is already a WorkTask. Bind a private repository to that ticket;
    // never allow the caller to choose a repository, provider credential, ref or another owner.
    private async Task<GitWorkspaceResult> PreparePersonalAsync(Guid business, Guid installation,
        PersonalPrepareInput input, CancellationToken ct)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (input.TaskItemId is { } taskId)
            return await PreparePersonalPlanTaskAsync(business, installation, input, taskId, ct);
        var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == input.ItemId && x.OrganizationId == business, ct) ?? throw new KeyNotFoundException("Personal ticket not found.");
        await RequirePersonalOwnerAsync(business, installation, item, item.Board, ct);
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == business && x.AgentInstallationId == installation && x.IsActive, ct);
        var binding = item.AssignmentRevision > 0
            ? JsonSerializer.Deserialize<PersonalSourceBinding>(item.DevelopmentBriefJson!, JsonOptions)
                ?? throw new InvalidOperationException("The retained project source binding is missing.")
            : await ResolvePersonalSourceAsync(business, installation, item, input.SourceWorkItemId, ct);
        if (item.AssignmentRevision > 0 && binding.PersonalSourceWorkItemId != input.SourceWorkItemId)
            throw new UnauthorizedAccessException("A prepared task cannot change projects. Resume it with its original source task.");
        var repositoryId = binding.RepositoryId;
        var now = DateTimeOffset.UtcNow;
        var repository = await db.SourceControlRepositories.Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == repositoryId, ct);
        if (repository is null)
        {
            if (input.SourceWorkItemId is not null)
                throw new InvalidOperationException("The existing project repository is unavailable; restore its access before retrying.");
            var connection = await InternalGitProvisioningDefaults.EnsureAsync(db, business, ct);
            var policy = await db.RepositoryProvisioningPolicies.SingleAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id, ct);
            if (!policy.IsEnabled || policy.RequiresManagerApproval || connection.Status != SourceControlConnectionStatus.Connected)
                throw new InvalidOperationException("The business repository policy requires approval or has disabled automatic creation.");
            var approvedTemplates = JsonSerializer.Deserialize<Guid[]>(policy.ApprovedTemplatesJson, JsonOptions) ?? [];
            var template = await db.SourceControlRepositoryTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == business &&
                x.ConnectionId == connection.Id && x.IsEnabled && approvedTemplates.Contains(x.Id) && x.Name == "empty", ct)
                ?? throw new UnauthorizedAccessException("An empty internal repository template must be approved by the business.");
            var reserved = await db.RepositoryProvisioningRequests.CountAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id &&
                x.RepositoryId == null && (x.Status == RepositoryProvisioningStatus.Pending || x.Status == RepositoryProvisioningStatus.Provisioning ||
                    x.Status == RepositoryProvisioningStatus.AwaitingApproval), ct);
            if (reserved + await db.SourceControlRepositories.CountAsync(x => x.OrganizationId == business && x.ConnectionId == connection.Id && x.ArchivedAt == null, ct) >= policy.MaximumRepositories)
                throw new InvalidOperationException("The business repository quota has been reached.");
            // Older/direct execution paths can reach preparation without a separate reservation.
            // Preserve the same product-title naming policy instead of leaking an opaque ticket ID
            // into the repository list.
            var name = await ResolvePersonalRepositoryNameAsync(
                business, connection.Id, repositoryId, item, suggestedName: null, ct);
            repository = new SourceControlRepository { Id = repositoryId, OrganizationId = business, ConnectionId = connection.Id,
                Name = name, Owner = business.ToString("N"), CanonicalPath = $"internal/{business:N}/{name}",
                ExternalRepositoryId = repositoryId.ToString("N"), ProviderRepositoryKey = $"internal:{repositoryId:N}",
                DefaultBranch = template.DefaultBranch, IsPrivate = true, IsManaged = true, Status = SourceControlRepositoryStatus.Provisioning,
                CreatedAt = now, UpdatedAt = now, Connection = connection };
            db.SourceControlRepositories.Add(repository);
            connection.Revision++; // Serialize quota reservation with other repository requests.
            await db.SaveChangesAsync(ct);
        }
        if (repository.OrganizationId != business || repository.Connection?.Provider != SourceControlProvider.InternalGit || repository.ArchivedAt is not null)
            throw new UnauthorizedAccessException("The personal repository is unavailable.");
        if (repository.Status == SourceControlRepositoryStatus.Provisioning)
        {
            var currentPolicy = await db.RepositoryProvisioningPolicies.AsNoTracking().SingleAsync(x =>
                x.OrganizationId == business && x.ConnectionId == repository.ConnectionId, ct);
            if (!currentPolicy.IsEnabled || currentPolicy.RequiresManagerApproval || repository.Connection.Status != SourceControlConnectionStatus.Connected)
                throw new UnauthorizedAccessException("Automatic repository creation is no longer approved.");
            await gitHost.CreatePersonalRepositoryAsync(new(business, installation, item.Id, input.IdempotencyKey), ct);
            repository.Status = SourceControlRepositoryStatus.Ready;
            repository.LastVerifiedAt = now;
            repository.Revision++;
        }
        if (repository.Status != SourceControlRepositoryStatus.Ready) throw new InvalidOperationException("The repository is not ready.");

        var membership = await db.TeamMemberships.SingleOrDefaultAsync(x => x.OrganizationId == business && x.OrganizationUserId == employee.Id && x.EndedAt == null, ct);
        Guid teamId;
        if (membership is not null) teamId = membership.TeamId;
        else
        {
            // Respect the existing lifetime membership boundary: never resurrect a removed member.
            if (await db.TeamMemberships.AnyAsync(x => x.ExclusiveAgentEmployeeId == employee.Id, ct))
                throw new UnauthorizedAccessException("The developer's team membership was removed.");
            teamId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-development:{installation:N}")).AsSpan(0, 16));
            if (await db.OrganizationTeams.AnyAsync(x => x.Id == teamId, ct))
                throw new UnauthorizedAccessException("The personal development team requires review.");
            db.OrganizationTeams.Add(new() { Id = teamId, OrganizationId = business, Name = employee.DisplayName + " development",
                NormalizedName = (employee.DisplayName + " development " + installation.ToString("N")).ToUpperInvariant(),
                TeamKey = "dev-" + installation.ToString("N"), LeadOrganizationUserId = employee.Id, CreatedAt = now, UpdatedAt = now });
            db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = teamId, OrganizationUserId = employee.Id,
                ExclusiveAgentEmployeeId = employee.Id, SourceType = "PersonalDevelopment", SourceId = item.Id, JoinedAt = now });
        }
        var board = item.Board!;
        if (board.TeamId is { } priorTeam && priorTeam != teamId) throw new UnauthorizedAccessException("The ticket's team changed.");
        board.TeamId = teamId;
        if (!await db.TeamRepositoryPolicies.AnyAsync(x => x.OrganizationId == business && x.TeamId == teamId && x.RepositoryId == repository.Id, ct))
            db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = teamId, RepositoryId = repository.Id,
                CreatedAt = now, UpdatedAt = now });
        if (item.AssignmentRevision == 0)
        {
            item.AssignmentRevision = 1;
            item.AssignedAgentInstallationId = installation;
            item.DevelopmentBriefJson = JsonSerializer.Serialize(binding, JsonOptions);
        }
        // Queue revision and claim disposition remain owned by the personal-task SDK callback.
        await db.SaveChangesAsync(ct);
        return await PrepareAsync(business, installation, new(item.Id, item.AssignmentRevision, input.IdempotencyKey), ct);
    }

    private async Task RequirePersonalOwnerAsync(Guid business, Guid installation, WorkTask item, WorkBoard? board, CancellationToken ct)
    {
        var planning = string.IsNullOrWhiteSpace(item.PlanningSpecificationJson) ? null
            : JsonSerializer.Deserialize<WorkItemPlanningSpecification>(item.PlanningSpecificationJson, JsonOptions)?.PersonalPlan;
        if (planning is not null && planning.RootItemId != item.Id)
        {
            var root = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x =>
                x.Id == planning.RootItemId && x.OrganizationId == business && x.BoardId == item.BoardId, ct)
                ?? throw new UnauthorizedAccessException("The task coordinator is unavailable.");
            if (item.Status != WorkTaskStatus.Running || item.ArchivedAt is not null ||
                item.AssignedAgentInstallationId != installation)
                throw new UnauthorizedAccessException("Only the current running task may use its development workspace.");
            await RequirePersonalOwnerAsync(business, installation, root, root.Board, ct);
            return;
        }
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == business &&
            x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null, ct);
        var approved = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == installation &&
            x.BusinessId == business.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
        var capabilities = JsonSerializer.Deserialize<HashSet<string>>(approved?.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        if (actor is null || !capabilities.Contains(PersonalPrepareCapability) || board is not { Kind: WorkBoardKind.Personal, ArchivedAt: null } ||
            board.OrganizationId != business || board.OwnerOrganizationUserId != actor.Id || item.ArchivedAt is not null ||
            item.Status != WorkTaskStatus.Running || item.ClaimEventId is null || item.ClaimExpiresAt is null || item.ClaimExpiresAt <= DateTimeOffset.UtcNow ||
            item.SourceConversationId is null || item.SourceMessageId is null)
            throw new UnauthorizedAccessException("An active, owned personal ticket with a retained chat request and live claim is required.");
    }

    private async Task<AssignmentContext> RequirePersonalAssignmentAsync(Guid business, Guid installation, WorkTask item,
        WorkBoard board, long revision, string action, CancellationToken ct)
    {
        await RequirePersonalOwnerAsync(business, installation, item, board, ct);
        if (revision != item.AssignmentRevision || item.AssignedAgentInstallationId != installation ||
            action is not (GitWorkspaceCapabilities.Prepare or GitWorkspaceCapabilities.Inspect or GitWorkspaceCapabilities.Publish or
                GitWorkspaceCapabilities.Refresh or GitWorkspaceCapabilities.Cleanup)) throw new UnauthorizedAccessException("Personal assignment is stale or action is not allowed.");
        var binding = JsonSerializer.Deserialize<PersonalSourceBinding>(item.DevelopmentBriefJson!, JsonOptions)
            ?? throw new UnauthorizedAccessException("The project source binding is missing.");
        var id = PersonalRepositoryId(binding.PersonalProjectRootId ?? binding.PersonalPlanRootId ?? item.Id);
        if (binding.PersonalPlanRootId is { } coordinatorId)
        {
            var coordinator = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == coordinatorId && x.OrganizationId == business, ct);
            var project = JsonSerializer.Deserialize<PersonalSourceBinding>(coordinator.DevelopmentBriefJson!, JsonOptions)!;
            if (project.RepositoryId != binding.RepositoryId || coordinator.BoardId != item.BoardId)
                throw new UnauthorizedAccessException("The task project binding changed.");
        }
        if (binding.RepositoryId != id)
            throw new UnauthorizedAccessException("Personal work cannot select another repository.");
        if (binding.PersonalProjectRootId is { } rootId)
            await RequirePersonalSourceTaskAsync(business, installation, item, rootId, ct);
        var repository = await db.SourceControlRepositories.AsNoTracking().Include(x => x.Connection).SingleAsync(x =>
            x.Id == id && x.OrganizationId == business && x.Status == SourceControlRepositoryStatus.Ready && x.ArchivedAt == null, ct);
        var team = board.TeamId ?? throw new InvalidOperationException("Personal workspace is not prepared.");
        await RequireActiveTeamMemberAsync(business, installation, team, ct);
        var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleAsync(x => x.OrganizationId == business && x.TeamId == team &&
            x.RepositoryId == id && x.DisabledAt == null, ct);
        return new(item, team, repository, policy, binding.PersonalSourceCommitSha);
    }
    private async Task<GitWorkspaceResult> PreparePersonalPlanTaskAsync(Guid business, Guid installation,
        PersonalPrepareInput input, Guid taskId, CancellationToken ct)
    {
        if (taskId == input.ItemId) throw new ArgumentException("A task branch needs a child task.");
        // Bind/provision the project once. The coordinator snapshot is never used for task publication.
        var root = await db.CoreWorkTasks.Include(x => x.Board).SingleAsync(x => x.Id == input.ItemId && x.OrganizationId == business, ct);
        if (root.AssignmentRevision == 0)
            await PreparePersonalAsync(business, installation, input with { TaskItemId = null }, ct);
        else
        {
            await RequirePersonalOwnerAsync(business, installation, root, root.Board, ct);
            var retainedProject = JsonSerializer.Deserialize<PersonalSourceBinding>(root.DevelopmentBriefJson!, JsonOptions)!;
            if (retainedProject.PersonalSourceWorkItemId != input.SourceWorkItemId)
                throw new UnauthorizedAccessException("The project source binding cannot change during a retry.");
        }
        var task = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == taskId && x.OrganizationId == business, ct)
            ?? throw new KeyNotFoundException("The planned task was not found.");
        var plan = string.IsNullOrWhiteSpace(task.PlanningSpecificationJson) ? null
            : JsonSerializer.Deserialize<WorkItemPlanningSpecification>(task.PlanningSpecificationJson, JsonOptions)?.PersonalPlan;
        if (plan?.RootItemId != root.Id || task.Kind != WorkItemKind.Task || task.BoardId != root.BoardId)
            throw new UnauthorizedAccessException("The task is not part of this project plan.");
        await RequirePersonalOwnerAsync(business, installation, task, task.Board, ct);
        if (task.AssignmentRevision == 0)
        {
            var project = JsonSerializer.Deserialize<PersonalSourceBinding>(root.DevelopmentBriefJson!, JsonOptions)!;
            var hasMergedTask = await db.SourceControlPublications.AnyAsync(x => x.OrganizationId == business &&
                x.RepositoryId == project.RepositoryId && x.Status == SourceControlPublicationStatus.Merged, ct);
            var hasTaskReview = await db.TaskDeliveryReviews.AnyAsync(x => x.OrganizationId == business && x.EpicId == root.Id, ct);
            var legacyCheckpoint = hasTaskReview ? null : await (from publication in db.SourceControlPublications.AsNoTracking()
                join workspace in db.SourceControlWorkspaces.AsNoTracking() on publication.WorkspaceId equals workspace.Id
                where publication.OrganizationId == business && publication.RepositoryId == project.RepositoryId &&
                    workspace.WorkItemId == root.Id && workspace.AgentInstallationId == installation &&
                    publication.Status != SourceControlPublicationStatus.Failed && publication.Status != SourceControlPublicationStatus.Superseded
                orderby publication.CreatedAt descending
                select publication.CommitSha).FirstOrDefaultAsync(ct);
            task.AssignmentRevision = 1;
            task.DevelopmentBriefJson = JsonSerializer.Serialize(project with {
                Requirements = [task.Description], PersonalPlanRootId = root.Id,
                PersonalSourceCommitSha = legacyCheckpoint ?? (hasMergedTask ? null : project.PersonalSourceCommitSha) }, JsonOptions);
            await db.SaveChangesAsync(ct);
        }
        return await PrepareAsync(business, installation, new(task.Id, task.AssignmentRevision, input.IdempotencyKey), ct);
    }

    private async Task<WorkTask> RequirePersonalSourceTaskAsync(Guid business, Guid installation,
        WorkTask current, Guid sourceId, CancellationToken ct)
    {
        var source = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == sourceId && x.OrganizationId == business, ct);
        if (source is null || source.Id == current.Id || source.ArchivedAt is not null ||
            source.Status != WorkTaskStatus.Completed || source.AssignedAgentInstallationId != installation ||
            source.Board is not { Kind: WorkBoardKind.Personal, ArchivedAt: null } ||
            source.BoardId != current.BoardId ||
            source.SourceConversationId is null || source.SourceMessageId is null || source.AssignmentRevision < 1)
            throw new UnauthorizedAccessException("Select a completed project from this developer's personal board before continuing its source.");
        return source;
    }

    private async Task<PersonalSourceBinding> ResolvePersonalSourceAsync(Guid business, Guid installation,
        WorkTask item, Guid? sourceId, CancellationToken ct)
    {
        if (sourceId is null)
            return new(PersonalRepositoryId(item.Id), "software-development-polyglot-v1",
                [item.Description], ["Implement and test the requested application."]);
        var source = await RequirePersonalSourceTaskAsync(business, installation, item, sourceId.Value, ct);
        var prior = JsonSerializer.Deserialize<PersonalSourceBinding>(source.DevelopmentBriefJson!, JsonOptions)
            ?? throw new InvalidOperationException("The selected project has no retained source binding.");
        var rootId = prior.PersonalProjectRootId ?? source.Id;
        if (rootId != source.Id) await RequirePersonalSourceTaskAsync(business, installation, item, rootId, ct);
        if (prior.RepositoryId != PersonalRepositoryId(rootId))
            throw new UnauthorizedAccessException("The selected project's repository binding is invalid.");
        // Personal delivery publishes task branches, so default-branch HEAD may still be empty.
        // Start from the latest completed delivery in this project and retain that exact commit.
        var commit = await (from publication in db.SourceControlPublications.AsNoTracking()
            join workspace in db.SourceControlWorkspaces.AsNoTracking() on publication.WorkspaceId equals workspace.Id
            join completed in db.CoreWorkTasks.AsNoTracking() on workspace.WorkItemId equals completed.Id
            where publication.OrganizationId == business && publication.RepositoryId == prior.RepositoryId &&
                publication.Status != SourceControlPublicationStatus.Failed && publication.Status != SourceControlPublicationStatus.Superseded &&
                workspace.OrganizationId == business && workspace.RepositoryId == prior.RepositoryId &&
                workspace.AgentInstallationId == installation && completed.OrganizationId == business &&
                completed.BoardId == source.BoardId && completed.ArchivedAt == null && completed.Status == WorkTaskStatus.Completed
            orderby publication.CreatedAt descending, publication.Id descending
            select publication.CommitSha).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The selected project has no published source. Finish its delivery before starting follow-up work.");
        var hasMergedSource = await db.SourceControlPublications.AnyAsync(x => x.OrganizationId == business &&
            x.RepositoryId == prior.RepositoryId && x.Status == SourceControlPublicationStatus.Merged, ct);
        return new(prior.RepositoryId, "software-development-polyglot-v1", [item.Description],
            ["Implement and test the requested change while preserving the existing application."], rootId, sourceId,
            hasMergedSource ? null : ValidateCommitSha(commit));
    }

    private static Guid PersonalRepositoryId(Guid item) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-repository:{item:N}")).AsSpan(0, 16));

    private async Task<string> ResolvePersonalRepositoryNameAsync(
        Guid business,
        Guid connectionId,
        Guid repositoryId,
        WorkTask item,
        string? suggestedName,
        CancellationToken ct)
    {
        var productTitle = suggestedName;
        if (string.IsNullOrWhiteSpace(productTitle))
        {
            productTitle = await db.CoreWorkTasks.AsNoTracking()
                .Where(x => x.OrganizationId == business && x.ParentWorkTaskId == item.Id &&
                    x.Kind == WorkItemKind.Epic && x.ArchivedAt == null)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.Title)
                .FirstOrDefaultAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(productTitle)) productTitle = item.Title;

        var name = NormalizePersonalRepositoryName(productTitle);
        var collision = await db.SourceControlRepositories.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == business && x.ConnectionId == connectionId && x.Id != repositoryId &&
            x.ArchivedAt == null && x.Name == name, ct);
        if (!collision) return name;

        return name[..Math.Min(name.Length, 41)].TrimEnd('-') + "-" + item.Id.ToString("N")[..8];
    }
}
