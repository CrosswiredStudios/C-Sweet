using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class GitWorkspaceRetentionCleanupService(
    CSweetDbContext db,
    IDockerCommandExecutor docker,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<GitWorkspaceRetentionCleanupService> logger)
{
    private const int BatchSize = 25;

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await db.GitTicketWorkspaces
            .Where(x =>
                x.Status == GitTicketWorkspaceStatus.Failed &&
                x.RetainUntil != null &&
                x.RetainUntil <= now)
            .OrderBy(x => x.RetainUntil)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var workspace in due)
        {
            var installationKey = await db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == workspace.AgentInstallationId)
                .Select(x => (Guid?)x.InstallationKey)
                .SingleOrDefaultAsync(cancellationToken);
            if (!installationKey.HasValue)
                continue;
            var volume = $"csweet-workspace-{installationKey.Value:N}";
            var inspect = await docker.ExecuteAsync(
                ["volume", "inspect", volume],
                cancellationToken);
            if (inspect.ExitCode != 0 &&
                !inspect.StandardError.Contains(
                    "no such volume", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Could not inspect the volume for expired Git workspace {WorkspaceId}: {Error}",
                    workspace.Id,
                    inspect.StandardError.Length <= 1000
                        ? inspect.StandardError
                        : inspect.StandardError[..1000]);
                continue;
            }
            if (inspect.ExitCode == 0)
            {
                var path =
                    $"/workspace/{workspace.WorkItemId:N}/{workspace.AssignmentRevision}";
                var script =
                    $"set -euo pipefail; test \"$(realpath -m '{path}')\" = '{path}'; " +
                    $"if [ -d '{path}' ]; then find '{path}' -depth -mindepth 1 -delete; rmdir '{path}'; fi";
                var result = await docker.ExecuteAsync(
                    [
                        "run", "--rm", "--network", "none", "--read-only",
                        "--cap-drop", "ALL",
                        "--security-opt", "no-new-privileges=true",
                        "--user", "1654:1654",
                        "--mount", $"type=volume,source={volume},target=/workspace",
                        options.Value.SoftwareDevelopmentPolyglotImage,
                        "/bin/bash", "-c", script
                    ],
                    cancellationToken);
                if (result.ExitCode != 0)
                {
                    logger.LogWarning(
                        "Could not remove expired Git workspace {WorkspaceId}: {Error}",
                        workspace.Id,
                        result.StandardError.Length <= 1000
                            ? result.StandardError
                            : result.StandardError[..1000]);
                    continue;
                }
            }
            workspace.Status = GitTicketWorkspaceStatus.Removed;
            workspace.RetainUntil = null;
            workspace.LastError = null;
            workspace.UpdatedAt = now;
            removed++;
        }
        if (removed > 0)
            await db.SaveChangesAsync(cancellationToken);
        return removed;
    }
}
