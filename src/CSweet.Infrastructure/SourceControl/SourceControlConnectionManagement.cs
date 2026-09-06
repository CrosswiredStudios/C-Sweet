using CSweet.Application.Setup;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class InternalRepositoryManagementService
{
    public async Task<SourceControlConnectionDetails> ConnectionAsync(Guid business, Guid user, Guid id, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var connection = await ConnectionRecordAsync(business, id, ct);
        var defaultTemplate = await db.SourceControlBusinessSettings.AsNoTracking().Where(s => s.OrganizationId == business)
            .Select(s => s.DefaultTemplateId).SingleOrDefaultAsync(ct);
        var isDefault = defaultTemplate is null ? connection.Provider == SourceControlProvider.InternalGit
            : await db.SourceControlRepositoryTemplates.AnyAsync(t => t.OrganizationId == business && t.Id == defaultTemplate && t.ConnectionId == id, ct);
        return new(connection.Id, connection.Name, connection.Provider.ToString(), connection.Mode.ToString(), connection.Status.ToString(),
            connection.AccountLogin, connection.AccountType, connection.Revision, connection.LastVerifiedAt,
            await db.SourceControlRepositories.CountAsync(r => r.OrganizationId == business && r.ConnectionId == id, ct),
            await db.SourceControlWorkspaces.CountAsync(w => w.OrganizationId == business && w.Repository!.ConnectionId == id &&
                w.Status != SourceControlWorkspaceStatus.Removed && w.Status != SourceControlWorkspaceStatus.Failed, ct),
            await db.SourceControlRepositoryTemplates.CountAsync(t => t.OrganizationId == business && t.ConnectionId == id, ct), isDefault);
    }

    public async Task<SourceControlConnectionDetails> RenameConnectionAsync(Guid business, Guid user, Guid id,
        RenameSourceControlConnectionRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Use a connection name of 1–100 characters without control characters.");
        var connection = await db.SourceControlConnections.AsTracking().SingleOrDefaultAsync(c => c.OrganizationId == business && c.Id == id, ct)
            ?? throw new KeyNotFoundException("Source-control connection not found.");
        if (connection.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Connection changed; reload before saving.");
        if (connection.Name == name) return await ConnectionAsync(business, user, id, ct);
        await RecordConnectionAsync("Rename", "Started");
        connection.Name = name; connection.Revision++; connection.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await RecordConnectionAsync("Rename", "Completed");
        return await ConnectionAsync(business, user, id, ct);

        Task<Guid> RecordConnectionAsync(string operation, string outcome) => audit.AppendAsync(new(
            "SourceControl.Connection." + operation, Category: "SourceControl", Outcome: outcome, OrganizationId: business,
            EntityType: "SourceControlConnection", EntityId: id, Actor: new AuditActor("User", ApplicationUserId: user)), ct);
    }

    public async Task<SourceControlConnectionHealth> CheckConnectionAsync(Guid business, Guid user, Guid id, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var connection = await ConnectionRecordAsync(business, id, ct);
        if (connection.Status == SourceControlConnectionStatus.Disconnected) return Result(false, "Connection", "This connection is disconnected. Complete setup before checking source access.");
        try
        {
            if (connection.Provider == SourceControlProvider.InternalGit)
            {
                var storage = await host.GetInternalStorageStatusAsync(ct);
                return Result(storage.Ready, "Internal Git storage", storage.Ready
                    ? "GitHost can access the configured repository store. LFS and backup recovery are separate checks."
                    : "GitHost cannot access the configured repository store. Check source-control settings and the storage mount.");
            }
            if (connection.Provider != SourceControlProvider.GitHub || connection.SourceAccessInstallationId is not > 0)
                return Result(false, "Source access", "Source access is not configured for this connection.");
            var installation = await host.DescribeInstallationAsync(connection.SourceAccessInstallationId.Value, ct);
            if (installation.InstallationId != connection.SourceAccessInstallationId || installation.Suspended ||
                installation.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture) != connection.ProviderAccountId ||
                !string.Equals(installation.AccountLogin, connection.AccountLogin, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(installation.AccountType, connection.AccountType, StringComparison.Ordinal))
                return Result(false, "GitHub source access", "GitHub did not confirm this connection's active account identity. Review its GitHub App installation.");
            var available = await host.ListRepositoriesAsync(connection.SourceAccessInstallationId.Value, ct);
            var selected = await db.SourceControlRepositories.AsNoTracking().Where(r => r.OrganizationId == business && r.ConnectionId == id && r.ArchivedAt == null)
                .Select(r => new { r.ExternalRepositoryId, r.Owner, r.Name }).ToListAsync(ct);
            var missing = selected.Count(r => !available.Any(a => a.RepositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture) == r.ExternalRepositoryId &&
                string.Equals(a.Owner, r.Owner, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase) && a.IsPrivate && !a.IsArchived));
            return Result(missing == 0, "GitHub source access", missing == 0
                ? $"GitHub confirmed the account and access to {selected.Count} selected private repositories. Repository creation permissions are checked separately."
                : $"GitHub could not confirm access to {missing} selected private repositories. Review the Source Access App's repository selection.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Result(false, "Source access", "The trusted host could not verify source access. Check host health and connection setup, then retry.");
        }
        SourceControlConnectionHealth Result(bool available, string scope, string message) => new(available, scope, message, clock.GetUtcNow());
    }

    private async Task<SourceControlConnection> ConnectionRecordAsync(Guid business, Guid id, CancellationToken ct) =>
        await db.SourceControlConnections.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == business && c.Id == id, ct)
            ?? throw new KeyNotFoundException("Source-control connection not found.");
}
