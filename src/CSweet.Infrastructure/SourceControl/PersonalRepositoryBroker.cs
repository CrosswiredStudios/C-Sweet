using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

/// <summary>Core-only GitHost operation for a repository already reserved by an owned personal ticket.</summary>
public sealed class PersonalRepositoryBroker(CSweetDbContext db, ITrustedSourceControlHostClient sourceHost)
{
    public async Task CreateAsync(AgentBrokerPersonalRepositoryRequest request, CancellationToken ct)
    {
        if (request.OrganizationId == Guid.Empty || request.AgentInstallationId == Guid.Empty || request.WorkItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("A personal ticket and stable request key are required.");
        var business = request.OrganizationId;
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
            x.Id == request.AgentInstallationId && x.BusinessId == business.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
        var approved = JsonSerializer.Deserialize<HashSet<string>>(installation?.Grant?.RequiredCapabilitiesJson ?? "[]") ?? [];
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == business &&
            x.AgentInstallationId == request.AgentInstallationId && x.IsActive && x.ArchivedAt == null, ct);
        var item = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x =>
            x.Id == request.WorkItemId && x.OrganizationId == business, ct);
        if (actor is null || !(approved.Contains("source-control.personal-work.prepare.v1") || approved.Contains("source-control.personal-work.reserve.v1")) || item is null ||
            item.Board is not { ArchivedAt: null } board || board.OrganizationId != business ||
            (board.Kind == WorkBoardKind.Personal ? board.OwnerOrganizationUserId != actor.Id : item.AssignedEmployeeId != actor.Id) || item.ArchivedAt is not null ||
            !((item.Status == WorkTaskStatus.Running && item.ClaimEventId is not null && item.ClaimExpiresAt > DateTimeOffset.UtcNow) ||
              (item.Status == WorkTaskStatus.Ready && item.IsExecutable)) ||
            item.SourceConversationId is null || item.SourceMessageId is null)
            throw new UnauthorizedAccessException("An approved installation and owned Ready ticket or live claim are required.");

        await new CSweet.Infrastructure.Core.ProjectWorkPolicy(db, TimeProvider.System).RequireIfConfiguredAsync(item, ct);
        var repositoryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"personal-repository:{item.Id:N}")).AsSpan(0, 16));
        if (board.WorkstreamId is { } projectId)
            repositoryId = await db.ProjectDeliveryBindings.Where(x => x.OrganizationId == business && x.WorkstreamId == projectId).Select(x => x.RepositoryId).SingleAsync(ct)
                ?? throw new InvalidOperationException("The project repository has not been reserved.");
        var repository = await db.SourceControlRepositories.AsNoTracking().Include(x => x.Connection).SingleOrDefaultAsync(x =>
            x.Id == repositoryId && x.OrganizationId == business, ct);
        if (repository is not { IsPrivate: true, IsManaged: true, ArchivedAt: null, Status: SourceControlRepositoryStatus.Provisioning } ||
            repository.Connection is not { Provider: SourceControlProvider.InternalGit, Status: SourceControlConnectionStatus.Connected })
            throw new UnauthorizedAccessException("The personal repository reservation is unavailable.");
        var policy = await db.RepositoryProvisioningPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == business && x.ConnectionId == repository.ConnectionId, ct);
        if (policy is not { IsEnabled: true, RequiresManagerApproval: false })
            throw new UnauthorizedAccessException("Automatic repository creation is no longer approved.");
        var templates = JsonSerializer.Deserialize<Guid[]>(policy.ApprovedTemplatesJson) ?? [];
        if (!await db.SourceControlRepositoryTemplates.AsNoTracking().AnyAsync(x => x.OrganizationId == business &&
            x.ConnectionId == repository.ConnectionId && templates.Contains(x.Id) && x.IsEnabled && x.Name == "empty" &&
            x.DefaultBranch == repository.DefaultBranch, ct))
            throw new UnauthorizedAccessException("The reserved repository template is no longer approved.");

        // GitHost's create operation is idempotent for this exact repository identity.
        // A lost response leaves the reservation intact for the same ticket's retry.
        await sourceHost.ExecuteInternalAsync(new(business, repository.Id, "create", repository.DefaultBranch), ct);
    }
}
