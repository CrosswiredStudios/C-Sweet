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
    internal const string PersonalPrepareCapability = "source-control.personal-work.prepare.v1";
    private sealed record PersonalPrepareInput(Guid ItemId, string IdempotencyKey);

    // A personal ticket is already a WorkTask. Bind a private repository to that ticket;
    // never allow the caller to choose a repository, provider credential, ref or another owner.
    private async Task<GitWorkspaceResult> PreparePersonalAsync(Guid business, Guid installation,
        PersonalPrepareInput input, CancellationToken ct)
    {
        ValidateIdempotencyKey(input.IdempotencyKey);
        var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == input.ItemId && x.OrganizationId == business, ct) ?? throw new KeyNotFoundException("Personal ticket not found.");
        await RequirePersonalOwnerAsync(business, installation, item, item.Board, ct);
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == business && x.AgentInstallationId == installation && x.IsActive, ct);
        var repositoryId = PersonalRepositoryId(item.Id);
        var now = DateTimeOffset.UtcNow;
        var repository = await db.SourceControlRepositories.Include(x => x.Connection).SingleOrDefaultAsync(x => x.Id == repositoryId, ct);
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
            var name = $"app-{item.Id:N}";
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
            item.DevelopmentBriefJson = JsonSerializer.Serialize(new SoftwareDevelopmentBrief(repository.Id,
                "software-development-polyglot-v1", [item.Description], ["Implement and test the requested application."]), JsonOptions);
        }
        // Queue revision and claim disposition remain owned by the personal-task SDK callback.
        await db.SaveChangesAsync(ct);
        return await PrepareAsync(business, installation, new(item.Id, item.AssignmentRevision, input.IdempotencyKey), ct);
    }

    private async Task RequirePersonalOwnerAsync(Guid business, Guid installation, WorkTask item, WorkBoard? board, CancellationToken ct)
    {
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
        var id = PersonalRepositoryId(item.Id);
        if (ResolveDeliveryRepository(item.DevelopmentBriefJson, item.DeliverySpecificationJson) != id)
            throw new UnauthorizedAccessException("Personal work cannot select another repository.");
        var repository = await db.SourceControlRepositories.AsNoTracking().Include(x => x.Connection).SingleAsync(x =>
            x.Id == id && x.OrganizationId == business && x.Status == SourceControlRepositoryStatus.Ready && x.ArchivedAt == null, ct);
        var team = board.TeamId ?? throw new InvalidOperationException("Personal workspace is not prepared.");
        await RequireActiveTeamMemberAsync(business, installation, team, ct);
        var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleAsync(x => x.OrganizationId == business && x.TeamId == team &&
            x.RepositoryId == id && x.DisabledAt == null, ct);
        return new(item, team, repository, policy, null);
    }
    private static Guid PersonalRepositoryId(Guid item) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-repository:{item:N}")).AsSpan(0, 16));
}
