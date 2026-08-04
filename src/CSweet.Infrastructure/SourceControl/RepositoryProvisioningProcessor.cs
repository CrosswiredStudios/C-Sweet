using CSweet.Application.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

/// <summary>
/// Executes one Core-authorized provisioning job. Provider coordinates come only from persisted
/// connection/template records; the agent-provided request cannot select an owner or repository.
/// </summary>
public sealed class RepositoryProvisioningProcessor(
    CSweetDbContext db,
    ITrustedProvisioningHostClient provisioner,
    TimeProvider timeProvider)
{
    public async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var request = await db.RepositoryProvisioningRequests
            .Include(candidate => candidate.Connection)
            .Include(candidate => candidate.Policy)
            .Include(candidate => candidate.Template)
            .Where(candidate => candidate.Status == RepositoryProvisioningStatus.Pending)
            .OrderBy(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (request is null)
            return false;

        var now = timeProvider.GetUtcNow();
        var blocker = Validate(request);
        if (blocker is not null)
        {
            Fail(request, "authorization_invalidated", blocker, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        request.Status = RepositoryProvisioningStatus.Provisioning;
        request.UpdatedAt = now;
        request.Revision++;
        await db.SaveChangesAsync(cancellationToken);

        TrustedRepositoryProvisioningResult result;
        try
        {
            result = await provisioner.ProvisionAsync(
                new TrustedRepositoryProvisioningRequest(
                    request.OrganizationId,
                    request.ConnectionId,
                    request.Id,
                    request.Connection!.ProvisionerInstallationId!.Value,
                    request.Connection.AccountLogin,
                    request.RepositoryName,
                    request.Description,
                    request.Template!.Owner,
                    request.Template.Name,
                    request.Template.DefaultBranch,
                    request.IdempotencyKey),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            now = timeProvider.GetUtcNow();
            Fail(request, "trusted_provisioner_failed", exception.Message, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        now = timeProvider.GetUtcNow();
        if (!result.Created || !result.ExternalRepositoryId.HasValue ||
            string.IsNullOrWhiteSpace(result.Owner) || string.IsNullOrWhiteSpace(result.Repository) ||
            string.IsNullOrWhiteSpace(result.DefaultBranch))
        {
            Fail(
                request,
                result.FailureCode ?? "repository_creation_rejected",
                result.FailureMessage ?? "The provider did not confirm private repository creation.",
                now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var repository = await db.SourceControlRepositories.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == request.OrganizationId &&
            candidate.ConnectionId == request.ConnectionId &&
            candidate.ExternalRepositoryId == result.ExternalRepositoryId.Value.ToString(),
            cancellationToken);
        repository ??= new SourceControlRepository
        {
            Id = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            ConnectionId = request.ConnectionId,
            ExternalRepositoryId = result.ExternalRepositoryId.Value.ToString(),
            Owner = result.Owner,
            Name = result.Repository,
            CanonicalPath = $"{result.Owner}/{result.Repository}".ToLowerInvariant(),
            CloneUrl = $"https://github.com/{Uri.EscapeDataString(result.Owner)}/{Uri.EscapeDataString(result.Repository)}.git",
            DefaultBranch = result.DefaultBranch,
            IsPrivate = true,
            IsManaged = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        repository.Status = result.Quarantined
            ? SourceControlRepositoryStatus.AttentionRequired
            : SourceControlRepositoryStatus.Ready;
        repository.LastHealthError = result.Quarantined ? result.FailureMessage : null;
        repository.LastVerifiedAt = result.Quarantined ? null : now;
        if (db.Entry(repository).State == EntityState.Detached)
            db.SourceControlRepositories.Add(repository);

        if (request.TeamId.HasValue && !result.Quarantined &&
            !await db.TeamRepositoryPolicies.AnyAsync(candidate =>
                candidate.OrganizationId == request.OrganizationId &&
                candidate.TeamId == request.TeamId.Value &&
                candidate.RepositoryId == repository.Id,
                cancellationToken))
        {
            db.TeamRepositoryPolicies.Add(new TeamRepositoryPolicy
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                TeamId = request.TeamId.Value,
                RepositoryId = repository.Id,
                IsPrimary = false,
                MergeApprovalMode = TeamMergeApprovalMode.LeadAuthorizedAutoMerge,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        request.RepositoryId = repository.Id;
        request.Status = result.Quarantined
            ? RepositoryProvisioningStatus.Quarantined
            : RepositoryProvisioningStatus.Completed;
        request.FailureCode = result.FailureCode;
        request.FailureMessage = result.FailureMessage;
        request.CompletedAt = now;
        request.UpdatedAt = now;
        request.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Validate(RepositoryProvisioningRequest request)
    {
        if (request.Connection is null ||
            request.Connection.Mode != SourceControlConnectionMode.ManagedGitHub ||
            request.Connection.Provider != SourceControlProvider.GitHub ||
            request.Connection.Status != SourceControlConnectionStatus.Connected ||
            !string.Equals(request.Connection.AccountType, "Organization", StringComparison.OrdinalIgnoreCase) ||
            !request.Connection.ProvisionerInstallationId.HasValue)
            return "The Managed GitHub provisioner connection is no longer ready.";
        if (request.Policy is null || !request.Policy.IsEnabled ||
            request.Policy.Revision != request.PolicyRevision)
            return "The provisioning policy changed after this request was authorized.";
        if (request.Template is null || !request.Template.IsEnabled ||
            request.Template.ConnectionId != request.ConnectionId)
            return "The approved repository template is no longer available.";
        return null;
    }

    private static void Fail(
        RepositoryProvisioningRequest request,
        string code,
        string message,
        DateTimeOffset now)
    {
        request.Status = RepositoryProvisioningStatus.Failed;
        request.FailureCode = code;
        request.FailureMessage = message.Length <= 2048 ? message : message[..2048];
        request.CompletedAt = now;
        request.UpdatedAt = now;
        request.Revision++;
    }
}
