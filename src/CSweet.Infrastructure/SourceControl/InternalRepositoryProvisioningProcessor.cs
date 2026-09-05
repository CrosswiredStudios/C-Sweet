using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class RepositoryProvisioningProcessor
{
    private async Task<bool> ProcessInternalAsync(RepositoryProvisioningRequest request, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.RequestedByOrganizationUserId &&
            u.OrganizationId == request.OrganizationId && u.IsActive && u.AgentInstallationId == request.RequestedByAgentInstallationId, ct);
        if (employee is null || request.RequestedByAgentInstallationId is null || request.TeamId is null ||
            !await db.AgentInstallations.AnyAsync(i => i.Id == request.RequestedByAgentInstallationId && i.IsEnabled && i.BusinessId == request.OrganizationId.ToString("D"), ct) ||
            !await db.TeamMemberships.AnyAsync(m => m.OrganizationId == request.OrganizationId && m.TeamId == request.TeamId && m.OrganizationUserId == employee.Id && m.EndedAt == null, ct) ||
            !await db.OrganizationTeams.AnyAsync(t => t.Id == request.TeamId && t.OrganizationId == request.OrganizationId && t.ArchivedAt == null, ct))
        {
            Fail(request, "assignment_revoked", "The requesting agent must remain an active member of the provisioning team.", now);
            await db.SaveChangesAsync(ct); return true;
        }
        var grant = authorization is null ? null : await authorization.AuthorizeAsync(request.OrganizationId,
            CSweet.Domain.Security.GrantSubjectKind.AgentInstallation, request.RequestedByAgentInstallationId.Value,
            CSweet.Agent.SDK.SourceControlCapabilities.ProvisionRepository, CSweet.Domain.Security.GrantScopeKind.Organization, request.OrganizationId, ct);
        if (grant?.Allowed != true)
        {
            Fail(request, "grant_revoked", "Repository creation permission is no longer granted.", now);
            await db.SaveChangesAsync(ct); return true;
        }
        var canonical = $"internal/{request.OrganizationId:N}/{request.RepositoryName.ToLowerInvariant()}";
        var existing = await db.SourceControlRepositories.SingleOrDefaultAsync(r => r.OrganizationId == request.OrganizationId && r.CanonicalPath == canonical, ct);
        if (existing is not null && existing.Id != request.Id)
        {
            Fail(request, "name_conflict", "A repository with this name already exists.", now);
            await db.SaveChangesAsync(ct); return true;
        }
        var repository = existing ?? new SourceControlRepository { Id = request.Id, OrganizationId = request.OrganizationId, ConnectionId = request.ConnectionId,
            Name = request.RepositoryName, Owner = request.OrganizationId.ToString("N"), CanonicalPath = canonical,
            ExternalRepositoryId = request.Id.ToString("N"), ProviderRepositoryKey = $"internal:{request.Id:N}",
            DefaultBranch = request.Template!.DefaultBranch, IsPrivate = true, IsManaged = true, Status = SourceControlRepositoryStatus.Provisioning,
            CreatedAt = now, UpdatedAt = now };
        if (existing is null) db.SourceControlRepositories.Add(repository);
        request.RepositoryId = repository.Id;
        request.Status = RepositoryProvisioningStatus.Provisioning; request.UpdatedAt = now; request.Revision++;
        await db.SaveChangesAsync(ct);
        try
        {
            if (gitHost is null) throw new InvalidOperationException("Internal GitHost is unavailable.");
            await gitHost.ExecuteInternalAsync(new(request.OrganizationId, repository.Id, "create", repository.DefaultBranch), ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            // Keep the durable identity and resume after the recovery delay; never create a replacement repository.
            request.FailureCode = "internal_store_unavailable"; request.FailureMessage = "Internal storage is unavailable; creation will resume automatically.";
            repository.LastHealthError = request.FailureMessage;
            await db.SaveChangesAsync(ct); return true;
        }
        if (!await db.TeamRepositoryPolicies.AnyAsync(p => p.OrganizationId == request.OrganizationId && p.RepositoryId == repository.Id && p.TeamId == request.TeamId, ct))
            db.TeamRepositoryPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId, RepositoryId = repository.Id, TeamId = request.TeamId.Value,
                IsPrimary = !await db.TeamRepositoryPolicies.AnyAsync(p => p.OrganizationId == request.OrganizationId && p.TeamId == request.TeamId && p.IsPrimary && p.DisabledAt == null, ct),
                CreatedAt = now, UpdatedAt = now });
        repository.Status = SourceControlRepositoryStatus.Ready; repository.LastHealthError = null; repository.LastVerifiedAt = timeProvider.GetUtcNow(); repository.Revision++;
        request.Status = RepositoryProvisioningStatus.Completed; request.CompletedAt = timeProvider.GetUtcNow(); request.UpdatedAt = request.CompletedAt.Value;
        request.FailureCode = null; request.FailureMessage = null; request.Revision++;
        await db.SaveChangesAsync(ct); return true;
    }
}
