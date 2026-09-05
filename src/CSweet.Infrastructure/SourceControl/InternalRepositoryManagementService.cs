using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed class InternalRepositoryManagementService(CSweetDbContext db, ITrustedSourceControlHostClient host,
    IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<SourceControlRepositorySummary>> ListAsync(Guid business, Guid user, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, false, ct);
        var repositories = await db.SourceControlRepositories.AsNoTracking().Include(r => r.Connection)
            .Where(r => r.OrganizationId == business && r.Connection!.Provider == SourceControlProvider.InternalGit)
            .OrderBy(r => r.Name).ToListAsync(ct);
        return repositories.Select(Summary).ToList();
    }

    public async Task<SourceControlRepositorySummary> CreateAsync(Guid business, Guid user,
        CreateInternalRepositoryRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var name = ValidateName(request.Name);
        InternalGitRepositoryStore.ValidateBranch(request.DefaultBranch);
        if (request.TeamId.HasValue && !await db.OrganizationTeams.AnyAsync(t => t.Id == request.TeamId && t.OrganizationId == business, ct))
            throw new ArgumentException("Choose a team belonging to this business.");
        var connection = await EnsureConnectionAsync(business, ct);
        var path = $"internal/{business:N}/{name.ToLowerInvariant()}";
        var repository = await db.SourceControlRepositories.SingleOrDefaultAsync(r => r.OrganizationId == business && r.CanonicalPath == path, ct);
        if (repository is not null && repository.Status != SourceControlRepositoryStatus.Provisioning)
            throw new ArgumentException("A repository with this name already exists, including archived repositories.");
        var now = clock.GetUtcNow();
        if (repository is null)
        {
            repository = new SourceControlRepository
            {
                Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id, Name = name,
                Owner = business.ToString("N"), CanonicalPath = path, DefaultBranch = request.DefaultBranch,
                IsPrivate = true, IsManaged = true, CreatedAt = now, UpdatedAt = now,
                Status = SourceControlRepositoryStatus.Provisioning
            };
            repository.ExternalRepositoryId = repository.Id.ToString("N");
            repository.ProviderRepositoryKey = $"internal:{repository.Id:N}";
            repository.CloneUrl = ""; // A URL is exposed only once authenticated transport is available.
            db.SourceControlRepositories.Add(repository);
            await db.SaveChangesAsync(ct);
        }
        await AuditAsync(business, user, repository.Id, "Create", "Started", request, ct);
        try
        {
            var result = await host.ExecuteInternalAsync(new(business, repository.Id, "create", repository.DefaultBranch), ct);
            repository.DefaultBranch = result.DefaultBranch;
            repository.Status = SourceControlRepositoryStatus.Ready;
            repository.LastVerifiedAt = clock.GetUtcNow();
            repository.LastHealthError = null;
            repository.Revision++;
            if (request.TeamId.HasValue && !await db.TeamRepositoryPolicies.AnyAsync(p => p.RepositoryId == repository.Id && p.TeamId == request.TeamId, ct))
                db.TeamRepositoryPolicies.Add(new TeamRepositoryPolicy
                {
                    Id = Guid.NewGuid(), OrganizationId = business, TeamId = request.TeamId.Value, RepositoryId = repository.Id,
                    IsPrimary = !await db.TeamRepositoryPolicies.AnyAsync(p => p.TeamId == request.TeamId && p.IsPrimary, ct),
                    MergeApprovalMode = TeamMergeApprovalMode.LeadAuthorizedAutoMerge, CreatedAt = now, UpdatedAt = now
                });
            await db.SaveChangesAsync(ct);
            await AuditAsync(business, user, repository.Id, "Create", "Completed", request, ct);
            return Summary(repository);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            repository.LastHealthError = "Creation did not complete. Check GitHost health and retry the same repository name.";
            await db.SaveChangesAsync(CancellationToken.None);
            await AuditAsync(business, user, repository.Id, "Create", "Failed", request, CancellationToken.None);
            throw;
        }
    }

    public async Task<InternalRepositoryDetails> InspectAsync(Guid business, Guid user, Guid id, string? reference, string? file, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, false, ct);
        var repository = await FindAsync(business, id, ct);
        var inspection = await host.ExecuteInternalAsync(new(business, id, "inspect", Ref: string.IsNullOrWhiteSpace(reference) ? null : reference, Path: string.IsNullOrEmpty(file) ? null : file), ct);
        return new(Summary(repository), repository.Revision, inspection);
    }

    public async Task<SourceControlRepositorySummary> UpdateAsync(Guid business, Guid user, Guid id,
        UpdateInternalRepositoryRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, id, ct);
        if (repository.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Repository changed; reload before saving.");
        var name = ValidateName(request.Name);
        InternalGitRepositoryStore.ValidateBranch(request.DefaultBranch);
        var canonical = $"internal/{business:N}/{name.ToLowerInvariant()}";
        if (await db.SourceControlRepositories.AnyAsync(r => r.OrganizationId == business && r.Id != id && r.CanonicalPath == canonical, ct))
            throw new ArgumentException("A repository with that name already exists.");
        if (request.Archived && await db.SourceControlWorkspaces.AnyAsync(w => w.RepositoryId == id &&
            (w.Status == SourceControlWorkspaceStatus.Ready || w.Status == SourceControlWorkspaceStatus.Preparing || w.Status == SourceControlWorkspaceStatus.Pending), ct))
            throw new InvalidOperationException("Finish or close active workspaces before archiving.");
        await AuditAsync(business, user, id, "Update", "Started", request, ct);
        if (repository.DefaultBranch != request.DefaultBranch)
            await host.ExecuteInternalAsync(new(business, id, "default-branch", request.DefaultBranch), ct);
        repository.Name = name;
        repository.CanonicalPath = canonical;
        repository.DefaultBranch = request.DefaultBranch;
        repository.ArchivedAt = request.Archived ? clock.GetUtcNow() : null;
        repository.Status = request.Archived ? SourceControlRepositoryStatus.Archived : SourceControlRepositoryStatus.Ready;
        repository.UpdatedAt = clock.GetUtcNow();
        repository.Revision++;
        await db.SaveChangesAsync(ct);
        await AuditAsync(business, user, id, "Update", "Completed", request, ct);
        return Summary(repository);
    }

    public async Task<bool> DeleteAsync(Guid business, Guid user, Guid id, DeleteInternalRepositoryRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, id, ct);
        if (repository.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Repository changed; reload before deleting.");
        if (repository.ArchivedAt is null || request.ConfirmName != repository.Name)
            throw new ArgumentException("Archive the repository and confirm its exact name before deletion.");
        if (await db.TeamRepositoryPolicies.AnyAsync(p => p.RepositoryId == id, ct) ||
            await db.SourceControlWorkspaces.AnyAsync(w => w.RepositoryId == id, ct) ||
            await db.SourceControlPublications.AnyAsync(p => p.RepositoryId == id, ct) ||
            await db.RepositoryProvisioningRequests.AnyAsync(r => r.RepositoryId == id, ct))
            throw new InvalidOperationException("This repository has team assignments or work history. Keep it archived to preserve those records.");
        await AuditAsync(business, user, id, "Delete", "Started", request, ct);
        await host.ExecuteInternalAsync(new(business, id, "delete"), ct);
        db.SourceControlRepositories.Remove(repository);
        await db.SaveChangesAsync(ct);
        await AuditAsync(business, user, id, "Delete", "Completed", request, ct);
        return true;
    }

    public async Task<InternalGitRepositoryInspection> ChangeRefAsync(Guid business, Guid user, Guid id, InternalGitRefRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, id, ct);
        if (repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready)
            throw new InvalidOperationException("Repository must be active to change refs.");
        InternalGitRepositoryStore.ValidateRef(request.Ref);
        InternalGitRepositoryStore.ValidateSha(request.ExpectedSha);
        if (request.Operation is not ("create" or "delete")) throw new ArgumentException("Choose create or delete.");
        if (request.Ref == "refs/heads/" + repository.DefaultBranch)
            throw new InvalidOperationException("The default branch cannot be changed through ref administration.");
        if (request.Operation == "delete" && request.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) &&
            await db.SourceControlWorkspaces.AnyAsync(w => w.RepositoryId == id && w.BranchName == request.Ref.Substring(11) &&
                w.Status != SourceControlWorkspaceStatus.Removed && w.Status != SourceControlWorkspaceStatus.Failed, ct))
            throw new InvalidOperationException("Finish or remove workspaces using this branch before deleting it.");
        if (request.Operation == "create" && request.ExpectedSha != new string('0', 40))
            throw new ArgumentException("Creating a ref requires an absent previous ref; force updates use the governed workflow.");
        if (request.Operation == "create") InternalGitRepositoryStore.ValidateSha(request.TargetSha);
        await AuditAsync(business, user, id, "Ref", "Started", request, ct);
        var result = await host.ExecuteInternalAsync(new(business, id,
            request.Operation == "create" ? "update-ref" : "delete-ref", Ref: request.Ref,
            ExpectedSha: request.ExpectedSha, TargetSha: request.TargetSha), ct);
        await AuditAsync(business, user, id, "Ref", "Completed", request, ct);
        return result;
    }

    public async Task<IReadOnlyList<InternalGitProposalSummary>> ProposalsAsync(Guid business, Guid user, Guid repositoryId, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, false, ct);
        await FindAsync(business, repositoryId, ct);
        return await db.SourceControlPublications.AsNoTracking().Where(p => p.OrganizationId == business && p.RepositoryId == repositoryId)
            .OrderByDescending(p => p.CreatedAt).Take(100).Select(p => new InternalGitProposalSummary(p.Id, p.RepositoryId,
                p.CommitSha, p.TicketBranch, p.TargetBranch, p.Status.ToString(), p.CreatedAt,
                db.SourceControlValidations.Any(v => v.PublicationId == p.Id && v.CommitSha == p.CommitSha &&
                    v.Status == SourceControlValidationStatus.Passed && v.SupersededAt == null))).ToListAsync(ct);
    }

    public async Task<InternalGitRepositoryInspection> ProposalDiffAsync(Guid business, Guid user, Guid repositoryId, Guid proposalId, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, false, ct);
        await FindAsync(business, repositoryId, ct);
        var proposal = await db.SourceControlPublications.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == business &&
            p.RepositoryId == repositoryId && p.Id == proposalId, ct) ?? throw new KeyNotFoundException("Proposed change not found.");
        var merged = await db.SourceControlMergeJobs.AsNoTracking().Where(j => j.OrganizationId == business && j.PublicationId == proposal.Id &&
            j.Status == SourceControlMergeStatus.Merged).Select(j => j.MergeCommitSha).FirstOrDefaultAsync(ct);
        return await host.ExecuteInternalAsync(new(business, repositoryId, "compare", Name: proposal.TargetBranch, ExpectedSha: proposal.CommitSha, TargetSha: merged), ct);
    }

    public async Task<IReadOnlyList<InternalGitTeamAccess>> TeamAccessAsync(Guid business, Guid user, Guid repositoryId, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, false, ct);
        await FindAsync(business, repositoryId, ct);
        return await (from policy in db.TeamRepositoryPolicies.AsNoTracking()
            join team in db.OrganizationTeams.AsNoTracking() on policy.TeamId equals team.Id
            where policy.OrganizationId == business && team.OrganizationId == business && policy.RepositoryId == repositoryId
            orderby team.Name
            select new InternalGitTeamAccess(team.Id, team.Name, policy.IsPrimary, policy.MergeApprovalMode.ToString(), policy.Revision, policy.DisabledAt != null)).ToListAsync(ct);
    }

    public async Task<bool> SetTeamAsync(Guid business, Guid user, Guid repositoryId, SetInternalRepositoryTeamRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, repositoryId, ct);
        if (!request.Disabled && (repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready)) throw new InvalidOperationException("Repository must be ready before assigning a team.");
        if (!await db.OrganizationTeams.AnyAsync(t => t.OrganizationId == business && t.Id == request.TeamId && (request.Disabled || t.ArchivedAt == null), ct))
            throw new ArgumentException("Choose an active team in this business.");
        if (!Enum.TryParse<TeamMergeApprovalMode>(request.MergeApprovalMode, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException("Choose a supported merge approval policy.");
        await AuditAsync(business, user, repositoryId, "Team", "Started", request, ct);
        var policies = await db.TeamRepositoryPolicies.Where(p => p.OrganizationId == business && p.TeamId == request.TeamId).ToListAsync(ct);
        var policy = policies.SingleOrDefault(p => p.RepositoryId == repositoryId);
        if ((policy?.Revision ?? 0) != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("Team access changed; reload before saving.");
        if (request.Disabled && policy is null) throw new KeyNotFoundException("Team access not found.");
        var now = clock.GetUtcNow();
        if (request.IsPrimary && !request.Disabled)
            foreach (var other in policies.Where(p => p.IsPrimary && p != policy))
            { other.IsPrimary = false; other.Revision++; other.UpdatedAt = now; }
        if (policy is null)
        {
            policy = new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = request.TeamId, RepositoryId = repositoryId, CreatedAt = now };
            db.TeamRepositoryPolicies.Add(policy);
        }
        policy.IsPrimary = request.IsPrimary && !request.Disabled; policy.DisabledAt = request.Disabled ? now : null; policy.MergeApprovalMode = mode; policy.Revision++; policy.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await AuditAsync(business, user, repositoryId, "Team", "Completed", request, ct);
        return true;
    }

    public async Task<InternalGitProvisioningSettings> ProvisioningSettingsAsync(Guid business, Guid user, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var connection = await EnsureConnectionAsync(business, ct);
        var policy = await db.RepositoryProvisioningPolicies.AsNoTracking().SingleAsync(p => p.OrganizationId == business && p.ConnectionId == connection.Id, ct);
        var template = await db.SourceControlRepositoryTemplates.AsNoTracking().SingleAsync(t => t.OrganizationId == business && t.ConnectionId == connection.Id && t.Name == "empty", ct);
        var jobs = await db.RepositoryProvisioningRequests.AsNoTracking().Where(r => r.OrganizationId == business && r.ConnectionId == connection.Id)
            .OrderByDescending(r => r.CreatedAt).Take(30).Select(r => new InternalGitProvisioningJob(r.Id, r.RepositoryName, r.Status.ToString(), r.RepositoryId, r.FailureMessage)).ToListAsync(ct);
        return new(template.Id, policy.IsEnabled, policy.RequiresManagerApproval, policy.MaximumRepositories, policy.DefaultTeamId,
            policy.NamePrefix, template.DefaultBranch, policy.Revision, jobs);
    }

    public async Task<InternalGitProvisioningSettings> UpdateProvisioningSettingsAsync(Guid business, Guid user, UpdateInternalGitProvisioningSettings request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        if (request.MaximumRepositories is < 1 or > 10000 || request.NamePrefix is null || request.NamePrefix.Length > 40 ||
            (request.NamePrefix.Length > 0 && !Regex.IsMatch(request.NamePrefix, "\\A[a-zA-Z0-9][a-zA-Z0-9-]*\\z")))
            throw new ArgumentException("Choose a quota from 1 to 10000 and a name prefix of up to 40 letters, numbers or hyphens.");
        InternalGitRepositoryStore.ValidateBranch(request.DefaultBranch);
        if (request.DefaultTeamId is { } team && !await db.OrganizationTeams.AnyAsync(t => t.OrganizationId == business && t.Id == team && t.ArchivedAt == null, ct))
            throw new ArgumentException("Choose an active team in this business.");
        var connection = await EnsureConnectionAsync(business, ct);
        var policy = await db.RepositoryProvisioningPolicies.SingleAsync(p => p.OrganizationId == business && p.ConnectionId == connection.Id, ct);
        if (policy.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Provisioning settings changed; reload before saving.");
        var template = await db.SourceControlRepositoryTemplates.SingleAsync(t => t.OrganizationId == business && t.ConnectionId == connection.Id && t.Name == "empty", ct);
        await AuditAsync(business, user, connection.Id, "ProvisioningPolicy", "Started", request, ct);
        policy.IsEnabled = request.Enabled; policy.RequiresManagerApproval = request.RequiresApproval; policy.MaximumRepositories = request.MaximumRepositories;
        policy.DefaultTeamId = request.DefaultTeamId; policy.NamePrefix = request.NamePrefix; policy.Revision++; policy.UpdatedAt = clock.GetUtcNow();
        template.DefaultBranch = request.DefaultBranch; template.Revision++; template.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await AuditAsync(business, user, connection.Id, "ProvisioningPolicy", "Completed", request, ct);
        return await ProvisioningSettingsAsync(business, user, ct);
    }

    public Task<SourceControlConnection> EnsureConnectionAsync(Guid business, CancellationToken ct) =>
        InternalGitProvisioningDefaults.EnsureAsync(db, business, ct);

    private async Task AuthorizeAsync(Guid business, Guid user, bool write, CancellationToken ct)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(u => u.OrganizationId == business &&
            u.ApplicationUserId == user && u.IsActive, ct) ?? throw new UnauthorizedAccessException("Active business membership is required.");
        if (write && actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Business manager permission is required to administer repositories.");
    }

    private async Task<SourceControlRepository> FindAsync(Guid business, Guid id, CancellationToken ct) =>
        await db.SourceControlRepositories.Include(r => r.Connection).SingleOrDefaultAsync(r => r.OrganizationId == business && r.Id == id &&
            r.Connection!.Provider == SourceControlProvider.InternalGit, ct) ?? throw new KeyNotFoundException("Internal repository not found.");

    private Task<Guid> AuditAsync(Guid business, Guid user, Guid id, string operation, string outcome, object details, CancellationToken ct) =>
        audit.AppendAsync(new AuditEventWriteRequest("SourceControl.Repository." + operation, Category: "SourceControl", Outcome: outcome,
            OrganizationId: business, EntityType: "SourceControlRepository", EntityId: id,
            MetadataJson: JsonSerializer.Serialize(details), Actor: new AuditActor("User", ApplicationUserId: user)), ct);

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "\\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}\\z") || name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use 1–100 letters, numbers, dots, underscores or hyphens; omit the .git suffix.");
        return name;
    }

    private static SourceControlRepositorySummary Summary(SourceControlRepository r) =>
        new(r.Id, r.ConnectionId, r.Name, r.CanonicalPath, r.DefaultBranch, r.Status.ToString(), r.IsPrivate, r.IsManaged, r.LastVerifiedAt, r.LastHealthError);
}
