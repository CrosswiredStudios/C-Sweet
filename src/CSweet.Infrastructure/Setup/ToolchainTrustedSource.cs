using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

internal static class ToolchainTrustedSource
{
    internal static string ArchiveUri(Guid buildId, string revision) => $"csweet-source://build/{buildId:N}/{revision}";

    internal static async Task<ToolchainSourceArchive> PrepareAsync(ITrustedSourceControlHostClient host,
        SourceControlRepository repository, DeliveryBuildRecord build, long maximumBytes, CancellationToken ct)
    {
        var connection = repository.Connection;
        if (repository.Id != build.RepositoryId || repository.OrganizationId != build.OrganizationId ||
            connection is null || connection.OrganizationId != build.OrganizationId ||
            connection.Status != SourceControlConnectionStatus.Connected ||
            repository.Status != SourceControlRepositoryStatus.Ready || repository.ArchivedAt is not null ||
            !repository.IsPrivate || maximumBytes <= 0 || build.SourceRevision.Length != 40 || !build.SourceRevision.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The exact private build source is unavailable.");
        var branch = $"build/{build.Id:N}";
        var key = $"delivery-build-source:{build.Id:N}:{build.SourceRevision}";
        TrustedWorkspaceSnapshot snapshot;
        if (connection.Provider == SourceControlProvider.InternalGit)
            snapshot = await host.PrepareInternalWorkspaceAsync(new(build.OrganizationId, repository.Id, build.Id,
                repository.DefaultBranch, branch, build.SourceRevision, key), ct);
        else if (connection.Provider == SourceControlProvider.GitHub && connection.SourceAccessInstallationId is > 0 &&
            long.TryParse(repository.ExternalRepositoryId, out var externalId) && externalId > 0)
            snapshot = await host.PrepareWorkspaceAsync(new(connection.SourceAccessInstallationId.Value, externalId,
                repository.Owner, repository.Name, repository.DefaultBranch, build.Id, branch, build.SourceRevision, key), ct);
        else throw new InvalidOperationException("The private build source provider is unavailable.");
        if (!string.Equals(snapshot.BaseCommitSha, build.SourceRevision, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Archive.LongLength > maximumBytes || snapshot.TotalBytes < 0 || snapshot.TotalBytes > maximumBytes ||
            snapshot.ArtifactSha256.Length != 64 || snapshot.ArtifactSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("GitHost did not return the exact bounded source revision requested by the build.");
        return new(snapshot.Archive, snapshot.ArtifactSha256.ToLowerInvariant());
    }
}
