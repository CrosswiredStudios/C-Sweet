using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentBuildService : IAgentBuildService
{
    private const int MaximumAutomaticSourceAttempts = 3;
    private static readonly string[] TransientSourceFailureMarkers =
    [
        "connection was reset",
        "recv failure",
        "could not resolve host",
        "failed to connect",
        "connection timed out",
        "network is unreachable",
        "remote end hung up",
        "early eof",
        "http/2 stream",
        "tls connection"
    ];

    private readonly CSweetDbContext _dbContext;
    private readonly IAgentBuildExecutor _executor;
    private readonly IAuditEventWriter _auditWriter;
    private readonly ILogger<AgentBuildService> _logger;
    private readonly IExecutionFleetService? _executionFleet;

    public AgentBuildService(
        CSweetDbContext dbContext,
        IAgentBuildExecutor executor,
        IAuditEventWriter auditWriter,
        ILogger<AgentBuildService> logger,
        IExecutionFleetService? executionFleet = null)
    {
        _dbContext = dbContext;
        _executor = executor;
        _auditWriter = auditWriter;
        _logger = logger;
        _executionFleet = executionFleet;
    }

    public async Task<Guid> QueueAsync(
        Guid packageVersionId,
        CancellationToken cancellationToken = default)
    {
        var packageVersion = await _dbContext.AgentPackageVersions
            .SingleOrDefaultAsync(x => x.Id == packageVersionId, cancellationToken)
            ?? throw new AgentBuildException("The agent package version was not found.");

        if (packageVersion.Status is not (AgentPackageVersionStatus.Approved or AgentPackageVersionStatus.Failed))
        {
            throw new AgentBuildException("Only approved or failed agent package versions can be queued for build.");
        }

        var activeJob = await _dbContext.AgentBuildJobs
            .Where(x => x.PackageVersionId == packageVersionId)
            .OrderByDescending(x => x.Attempt)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeJob?.Status is AgentBuildStatus.Queued or AgentBuildStatus.Cloning or AgentBuildStatus.Building)
        {
            return activeJob.Id;
        }

        var job = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = packageVersionId,
            Attempt = (activeJob?.Attempt ?? 0) + 1,
            QueuedAt = DateTimeOffset.UtcNow
        };
        job.ExecutionPoolId = await _dbContext.AgentRuntimeGlobalSettings.AsNoTracking()
            .Select(x => x.DefaultBuildExecutionPoolId)
            .SingleOrDefaultAsync(cancellationToken);
        job.StepsJson = AgentBuildStepStore.CreateInitialJson(job.QueuedAt);
        packageVersion.Status = AgentPackageVersionStatus.Approved;
        _dbContext.AgentBuildJobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(job, "agent-build.queued", "Queued agent package build.", cancellationToken);
        return job.Id;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (_executionFleet is not null && !await _executionFleet.IsReadyAsync(cancellationToken))
            return false;
        var job = await _dbContext.AgentBuildJobs
            .Include(x => x.PackageVersion)
                .ThenInclude(x => x!.PackageSource)
            .Where(x => x.Status == AgentBuildStatus.Queued &&
                        x.QueuedAt <= DateTimeOffset.UtcNow)
            .OrderBy(x => x.QueuedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        var settings = await _dbContext.AgentRuntimeGlobalSettings
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentBuildException("Agent runtime settings have not been seeded.");
        var package = job.PackageVersion
            ?? throw new AgentBuildException("The build job package version was not loaded.");
        var source = package.PackageSource
            ?? throw new AgentBuildException("The build job package source was not loaded.");
        if (string.IsNullOrWhiteSpace(job.StepsJson) || job.StepsJson == "[]")
        {
            job.StepsJson = AgentBuildStepStore.CreateInitialJson(job.QueuedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        var progress = new PersistedAgentBuildProgressReporter(_dbContext, job);
        if (package.Status != AgentPackageVersionStatus.Approved)
        {
            job.FailureMessage = $"Build cancelled because the package version is {package.Status}.";
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Queued,
                    AgentBuildStepStatuses.Cancelled,
                    Error: job.FailureMessage),
                cancellationToken);
            job.TransitionTo(AgentBuildStatus.Cancelled, DateTimeOffset.UtcNow);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(
                job,
                "agent-build.cancelled",
                job.FailureMessage,
                cancellationToken);
            return true;
        }
        if (string.IsNullOrWhiteSpace(package.ProjectPath))
        {
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(AgentBuildStepKeys.Queued, AgentBuildStepStatuses.Succeeded),
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Source,
                    AgentBuildStepStatuses.Failed,
                    Error: "The approved manifest does not define a .NET project path."),
                cancellationToken);
            await FailAsync(job, package, "The approved manifest does not define a .NET project path.");
            return true;
        }

        var request = new AgentBuildExecutionRequest(
            job.Id,
            package.Id,
            source.RepositoryUrl,
            package.CommitSha,
            package.ProjectPath,
            package.TargetFramework,
            "dotnet-publish-v1",
            settings.BuildTimeoutSeconds,
            settings.BuildMemoryMb,
            settings.BuildCpuPercent,
            settings.DefaultWorkloadProcessLimit,
            settings.MaximumRepositorySizeMb,
            settings.MaximumBuildLogMb);

        AgentBuildWorkspace? workspace = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.BuildTimeoutSeconds));

        try
        {
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(AgentBuildStepKeys.Queued, AgentBuildStepStatuses.Succeeded),
                timeout.Token);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Source,
                    AgentBuildStepStatuses.InProgress,
                    "Fetching and validating the approved commit."),
                timeout.Token);
            job.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(job, "agent-build.started", "Started cloning the approved commit.", cancellationToken);

            workspace = await _executor.CloneAsync(request, progress, timeout.Token);
            job.SourceWorkspacePath = workspace.SourcePath;
            job.LogPath = workspace.LogPath;
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Source,
                    AgentBuildStepStatuses.Succeeded,
                    "Approved source is ready."),
                timeout.Token);
            job.TransitionTo(AgentBuildStatus.Building, DateTimeOffset.UtcNow);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = await _executor.BuildAsync(request, workspace, progress, timeout.Token);
            await AgentBuildStepStore.CompleteRemainingAsync(_dbContext, job, timeout.Token);
            job.PackagePath = result.PackagePath;
            job.PackageDigest = result.PackageDigest;
            job.LogPath = result.LogPath;
            job.FailureMessage = null;
            job.TransitionTo(AgentBuildStatus.Succeeded, DateTimeOffset.UtcNow);
            package.PackagePath = result.PackagePath;
            package.PackageDigest = result.PackageDigest;
            package.ArtifactSignature = result.ArtifactSignature;
            package.ArtifactFormatVersion = result.ArtifactFormatVersion;
            package.ArtifactOperatingSystem = result.ArtifactOperatingSystem;
            package.ArtifactArchitecture = result.ArtifactArchitecture;
            package.BuiltAt = job.CompletedAt;
            package.Status = AgentPackageVersionStatus.Built;
            await UpdateDefinitionBuildStateAsync(package, buildSucceeded: true, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            try
            {
                await new AgentDefinitionInstallationSynchronizer(_dbContext, _auditWriter)
                    .SynchronizeAsync(cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                // The immutable package build succeeded. Deployment reconciliation is durable and
                // retried by AgentRuntimeManager, so a temporarily unavailable Office/control-plane
                // path must not relabel the package as a failed build.
                _logger.LogError(exception,
                    "Agent package {PackageVersionId} built successfully, but existing hire deployment will be retried by runtime reconciliation.",
                    package.Id);
            }
            await WriteAuditAsync(
                job,
                "agent-build.succeeded",
                $"Built immutable agent package {result.PackageDigest}.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AgentBuildStepStore.FailCurrentAsync(_dbContext, job, "The build worker was stopped.");
            await CancelAsync(job, package, "The build worker was stopped.");
            throw;
        }
        catch (OperationCanceledException)
        {
            var message = $"The build exceeded the {settings.BuildTimeoutSeconds}-second timeout.";
            await AgentBuildStepStore.FailCurrentAsync(_dbContext, job, message);
            await FailAsync(job, package, message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Agent build {BuildJobId} failed.", job.Id);
            await AgentBuildStepStore.FailCurrentAsync(
                _dbContext,
                job,
                exception.Message,
                (exception as AgentBuildException)?.StepKey);
            if (job.Status == AgentBuildStatus.Cloning &&
                job.Attempt < MaximumAutomaticSourceAttempts &&
                IsTransientSourceFailure(exception))
            {
                await QueueAutomaticSourceRetryAsync(job, package, exception.Message);
            }
            else
            {
                await FailAsync(job, package, exception.Message);
            }
        }
        finally
        {
            var shouldRemoveWorkspace = workspace is not null &&
                (job.Status == AgentBuildStatus.Succeeded
                    ? settings.RemoveWorkspacesAfterCompletion
                    : !settings.KeepFailedBuildWorkspaces);
            if (shouldRemoveWorkspace)
            {
                try
                {
                    await _executor.CleanupWorkspaceAsync(workspace!, CancellationToken.None);
                    job.SourceWorkspacePath = null;
                    await _dbContext.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not clean build workspace for job {BuildJobId}.",
                        job.Id);
                }
            }
        }

        return true;
    }

    private async Task FailAsync(AgentBuildJob job, AgentPackageVersion package, string failureMessage)
    {
        job.FailureMessage = Truncate(failureMessage, 2048);
        if (job.Status is AgentBuildStatus.Queued or AgentBuildStatus.Cloning or AgentBuildStatus.Building)
        {
            job.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        }
        package.Status = AgentPackageVersionStatus.Failed;
        await UpdateDefinitionBuildStateAsync(package, buildSucceeded: false, CancellationToken.None);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        await WriteAuditAsync(job, "agent-build.failed", job.FailureMessage, CancellationToken.None);
    }

    private async Task UpdateDefinitionBuildStateAsync(
        AgentPackageVersion package,
        bool buildSucceeded,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.AgentDefinitions.Include(x => x.Configuration)
            .SingleOrDefaultAsync(x => x.PackageVersionId == package.Id, cancellationToken);
        if (definition is null)
            return;

        if (!buildSucceeded)
        {
            definition.Status = AgentDefinitionStatus.BuildFailed;
            definition.IsAvailableForHire = false;
        }
        else
        {
            var manifest = AgentConfigurationRules.DeserializeManifest(package.ManifestJson);
            var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                               definition.Configuration?.SettingsJson ?? "{}")
                           ?? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
            var complete = AgentConfigurationRules.HasAllRequired(manifest, settings) &&
                           !string.IsNullOrWhiteSpace(package.PackageDigest) &&
                           !string.IsNullOrWhiteSpace(package.ArtifactSignature);
            definition.Status = complete ? AgentDefinitionStatus.Available : AgentDefinitionStatus.NeedsConfiguration;
            definition.IsAvailableForHire = complete;
        }
        definition.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task QueueAutomaticSourceRetryAsync(
        AgentBuildJob job,
        AgentPackageVersion package,
        string failureMessage)
    {
        job.FailureMessage = Truncate(failureMessage, 2048);
        job.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);

        var retry = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            Attempt = job.Attempt + 1,
            QueuedAt = DateTimeOffset.UtcNow.AddSeconds(5 * job.Attempt)
        };
        retry.StepsJson = AgentBuildStepStore.CreateInitialJson(retry.QueuedAt);
        package.Status = AgentPackageVersionStatus.Approved;
        _dbContext.AgentBuildJobs.Add(retry);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        await WriteAuditAsync(
            job,
            "agent-build.transient-source-failure",
            $"Source fetch attempt {job.Attempt} failed and will be retried automatically: {job.FailureMessage}",
            CancellationToken.None);
        await WriteAuditAsync(
            retry,
            "agent-build.retry-queued",
            $"Queued automatic source fetch attempt {retry.Attempt} of {MaximumAutomaticSourceAttempts}.",
            CancellationToken.None);
    }

    private async Task CancelAsync(AgentBuildJob job, AgentPackageVersion package, string reason)
    {
        job.FailureMessage = reason;
        if (job.Status is AgentBuildStatus.Queued or AgentBuildStatus.Cloning or AgentBuildStatus.Building)
        {
            job.TransitionTo(AgentBuildStatus.Cancelled, DateTimeOffset.UtcNow);
        }
        package.Status = AgentPackageVersionStatus.Approved;
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        await WriteAuditAsync(job, "agent-build.cancelled", reason, CancellationToken.None);
    }

    private Task WriteAuditAsync(
        AgentBuildJob job,
        string eventType,
        string? summary,
        CancellationToken cancellationToken) =>
        _auditWriter.WriteAsync(
            eventType,
            nameof(AgentBuildJob),
            job.Id,
            summary,
            null,
            cancellationToken);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static bool IsTransientSourceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (TransientSourceFailureMarkers.Any(marker =>
                    current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
