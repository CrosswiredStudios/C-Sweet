using System.Globalization;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Office.Contracts.Workloads;
using CSweet.ExecutionArtifacts;
using CSweet.Office.Contracts.Guest;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

/// <summary>Terminates all guest broker sessions centrally after node tunnel authorization.</summary>
public sealed class ExecutionBrokerSessionRunner(
    CSweetDbContext dbContext,
    IAgentBrokerOperationHandler runtimeOperations,
    IAgentArtifactStore artifacts,
    IBuilderArtifactResultStore builderResults,
    IBuilderArtifactResultPublisher builderPublisher,
    ITrustedSourceControlHostClient sourceControlHost,
    ArtifactStoreOptions artifactOptions,
    TimeProvider timeProvider,
    ILogger<ExecutionBrokerSessionRunner> logger) : IExecutionBrokerSessionRunner
{
    public async Task RunAsync(
        Guid assignmentId,
        Stream duplexStream,
        CancellationToken cancellationToken = default)
    {
        var assignment = await dbContext.ExecutionWorkloadAssignments
            .Include(x => x.AgentBuildJob)!.ThenInclude(x => x!.PackageVersion)!.ThenInclude(x => x!.PackageSource)
            .Include(x => x.AgentRuntimeInstance)
            .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken)
            ?? throw new InvalidDataException("The tunnel assignment does not exist.");
        if (assignment.WorkloadKind == ExecutionWorkloadKind.Builder)
            await RunBuilderAsync(assignment, duplexStream, cancellationToken);
        else if (assignment.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild)
            await RunToolchainBuildAsync(assignment, duplexStream, cancellationToken);
        else
            await RunRuntimeAsync(assignment, duplexStream, cancellationToken);
    }

    private async Task RunRuntimeAsync(
        ExecutionWorkloadAssignment assignment,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var workload = JsonSerializer.Deserialize<RuntimeWorkloadSpecification>(assignment.SpecificationJson)
            ?? throw new InvalidDataException("The runtime workload specification is empty.");
        // The node opens its tunnel before the WorkerHost call that submitted the assignment
        // returns. Do not permit the guest MCP handshake to race the durable runtime transition.
        while (true)
        {
            var status = await dbContext.AgentRuntimeInstances.AsNoTracking()
                .Where(x => x.Id == workload.WorkloadId)
                .Select(x => x.Status)
                .SingleAsync(cancellationToken);
            if (status == AgentRuntimeStatus.WaitingForMcpSession) break;
            if (AgentRuntimeInstance.IsTerminal(status))
                throw new InvalidOperationException("The runtime ended before its broker session could start.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        var grant = new AgentBrokerGrant(
            workload.WorkloadId,
            workload.BrokerLease.ChannelId,
            workload.Identity.InstallationId,
            workload.GuestImage.Digest,
            workload.Artifact.Digest,
            workload.BrokerLease.ProtocolVersion,
            workload.BrokerLease.BootToken,
            workload.BrokerLease.ExpiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "mcp.runtime" },
            100_000, 1024 * 1024, 16 * 1024 * 1024, 16 * 1024 * 1024);
        var boot = new GuestBootConfiguration
        {
            WorkloadId = workload.WorkloadId.ToString("D"),
            ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
            ProtocolVersion = workload.BrokerLease.ProtocolVersion,
            GuestImageDigest = workload.GuestImage.Digest,
            ArtifactDigest = workload.Artifact.Digest,
            BootToken = workload.BrokerLease.BootToken,
            LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
            ArtifactRoot = "/run/csweet/artifact/payload",
            WorkloadKind = (int)WorkloadKind.Runtime,
            InstallationId = workload.Identity.InstallationId.ToString("D"),
            BusinessId = workload.Identity.BusinessId,
            TickId = workload.Identity.TickId.ToString("D"),
            LocalBrokerSocketPath = "/run/csweet/broker.sock",
            WorkloadTokenPath = "/run/csweet/workload-token",
            MaximumFrameBytes = 16 * 1024 * 1024
        };
        var start = new StartCommand
        {
            WorkloadKind = (int)WorkloadKind.Runtime,
            MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
        };
        start.Entrypoint.AddRange(workload.Entrypoint);
        var diagnostics = new RuntimeDiagnosticBrokerStreamHandler(
            workload.WorkloadId, workload.Identity.InstallationId);
        var session = new GuestBrokerHostSession(
            grant, runtimeOperations, timeProvider, diagnostics, boot, start);
        logger.LogInformation(
            "Starting authenticated runtime broker session for assignment {AssignmentId}, workload {WorkloadId}, installation {InstallationId}.",
            assignment.Id, workload.WorkloadId, workload.Identity.InstallationId);
        try
        {
            await session.RunAsync(stream, stream, cancellationToken);
            logger.LogInformation(
                "Authenticated runtime broker session completed for assignment {AssignmentId}, workload {WorkloadId}.",
                assignment.Id, workload.WorkloadId);
        }
        finally
        {
            await PersistRuntimeDiagnosticsAsync(
                assignment, workload.WorkloadId, diagnostics.Latest);
        }
    }

    private async Task PersistRuntimeDiagnosticsAsync(
        ExecutionWorkloadAssignment assignment,
        Guid workloadId,
        string? diagnosticExcerpt)
    {
        if (string.IsNullOrWhiteSpace(diagnosticExcerpt)) return;
        try
        {
            // The assignment is intentionally fenced/cancelled concurrently when a runtime
            // startup deadline expires. Persist diagnostics independently of that stale
            // tracked assignment so its concurrency token cannot roll back the runtime log.
            await dbContext.AgentRuntimeInstances
                .Where(x => x.Id == workloadId)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(x => x.LogExcerpt, diagnosticExcerpt),
                    CancellationToken.None);
            await dbContext.ExecutionWorkloadAssignments
                .Where(x => x.Id == assignment.Id)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(x => x.ResultLogExcerpt, diagnosticExcerpt),
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Diagnostics must never obscure the workload result or replace the original failure.
            logger.LogWarning(exception,
                "Could not persist the bounded runtime diagnostic excerpt for assignment {AssignmentId}, workload {WorkloadId}.",
                assignment.Id, workloadId);
        }
    }

    private async Task RunBuilderAsync(
        ExecutionWorkloadAssignment assignment,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var workload = JsonSerializer.Deserialize<BuilderWorkloadSpecification>(assignment.SpecificationJson)
            ?? throw new InvalidDataException("The builder workload specification is empty.");
        logger.LogInformation(
            "Starting authenticated builder broker session for assignment {AssignmentId}, workload {WorkloadId}.",
            assignment.Id, workload.WorkloadId);
        var job = assignment.AgentBuildJob
            ?? throw new InvalidDataException("The builder assignment is not bound to a build job.");
        var package = job.PackageVersion
            ?? throw new InvalidDataException("The builder package was not loaded.");
        var source = package.PackageSource
            ?? throw new InvalidDataException("The builder source was not loaded.");
        var settings = await dbContext.AgentRuntimeGlobalSettings.AsNoTracking().SingleAsync(cancellationToken);
        var request = new AgentBuildExecutionRequest(
            job.Id, package.Id, source.RepositoryUrl, package.CommitSha, package.ProjectPath ?? string.Empty,
            package.TargetFramework, workload.Repository.BuildProfileId,
            (int)workload.ResourceLimits.MaximumDuration.TotalSeconds,
            workload.ResourceLimits.MemoryMegabytes, workload.ResourceLimits.CpuPercent,
            workload.ResourceLimits.MaximumProcessCount, settings.MaximumRepositorySizeMb,
            Math.Max(1, workload.ResourceLimits.MaximumLogBytes / (1024 * 1024)));
        var progress = new PersistedAgentBuildProgressReporter(dbContext, job);
        var provenance = JsonSerializer.Serialize(new
        {
            request.RepositoryUrl,
            request.CommitSha,
            request.ProjectPath,
            request.BuildProfileId,
            workload.GuestImage.Digest,
            brokerProtocolVersion = workload.BrokerLease.ProtocolVersion
        });
        var artifact = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workload.WorkloadId, request.PackageVersionId, "agent-artifact",
                workload.MaximumArtifactBytes, "1.0", "linux", workload.GuestImage.Architecture, provenance),
            artifacts,
            builderPublisher,
            Path.Combine(artifactOptions.ValidatedRootPath(), ".builder-streams"));
        await using (artifact)
        {
            var operations = new BuilderBrokerOperationHandler(
                workload, request, artifact, progress, logger);
            var grant = new AgentBrokerGrant(
                workload.WorkloadId,
                workload.BrokerLease.ChannelId,
                request.PackageVersionId,
                workload.GuestImage.Digest,
                null,
                workload.BrokerLease.ProtocolVersion,
                workload.BrokerLease.BootToken,
                workload.BrokerLease.ExpiresAt,
                new HashSet<string>(StringComparer.Ordinal)
                    { "build.fetch", "build.artifact", "build.progress" },
                100_000, 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024);
            var boot = new GuestBootConfiguration
            {
                WorkloadId = workload.WorkloadId.ToString("D"),
                ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
                ProtocolVersion = workload.BrokerLease.ProtocolVersion,
                GuestImageDigest = workload.GuestImage.Digest,
                BootToken = workload.BrokerLease.BootToken,
                LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
                ArtifactRoot = "/usr/lib/csweet/builder",
                WorkloadKind = (int)WorkloadKind.Builder,
                LocalBrokerSocketPath = "/run/csweet/broker.sock",
                WorkloadTokenPath = "/run/csweet/workload-token",
                MaximumFrameBytes = 16 * 1024 * 1024
            };
            var start = new StartCommand
            {
                WorkloadKind = (int)WorkloadKind.Builder,
                MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
            };
            start.Entrypoint.AddRange([
                "/usr/lib/csweet/builder/CSweet.Office.BuilderGuest",
                "--repository", request.RepositoryUrl,
                "--commit", request.CommitSha,
                "--project", request.ProjectPath,
                "--maximum-repository-bytes", checked(request.MaximumRepositorySizeMb * 1024L * 1024L).ToString(CultureInfo.InvariantCulture),
                "--maximum-artifact-bytes", workload.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture),
                "--broker-socket", "/run/csweet/broker.sock"
            ]);
            if (!string.IsNullOrWhiteSpace(request.TargetFramework))
                start.Entrypoint.AddRange(["--target-framework", request.TargetFramework]);
            var diagnostics = new RuntimeDiagnosticBrokerStreamHandler(
                workload.WorkloadId,
                request.PackageVersionId);
            var session = new GuestBrokerHostSession(
                grant,
                operations,
                timeProvider,
                streamHandler: diagnostics,
                bootConfiguration: boot,
                startCommand: start);
            var resultTask = builderResults.WaitAsync(workload.WorkloadId, cancellationToken);
            try
            {
                await session.RunAsync(stream, stream, cancellationToken);
            }
            finally
            {
                await PersistBuilderDiagnosticsAsync(assignment.Id, diagnostics.Latest);
            }
            var result = await resultTask;
            assignment.ResultArtifactLocator = result.OpaqueLocator;
            assignment.ResultArtifactDigest = result.Artifact.Digest;
            assignment.ResultArtifactSignature = result.Artifact.Signature;
            assignment.ResultArtifactFormatVersion = result.Artifact.FormatVersion;
            assignment.ResultArtifactOperatingSystem = result.Artifact.OperatingSystem;
            assignment.ResultArtifactArchitecture = result.Artifact.Architecture;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Authenticated builder broker session completed for assignment {AssignmentId}, workload {WorkloadId}, artifact {ArtifactDigest}.",
                assignment.Id, workload.WorkloadId, result.Artifact.Digest);
        }
    }

    private async Task PersistBuilderDiagnosticsAsync(Guid assignmentId, string? diagnosticExcerpt)
    {
        if (string.IsNullOrWhiteSpace(diagnosticExcerpt)) return;
        try
        {
            await dbContext.ExecutionWorkloadAssignments
                .Where(x => x.Id == assignmentId)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(x => x.ResultLogExcerpt, diagnosticExcerpt),
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not persist the bounded builder diagnostic excerpt for assignment {AssignmentId}.",
                assignmentId);
        }
    }

    private async Task RunToolchainBuildAsync(
        ExecutionWorkloadAssignment assignment,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var workload = JsonSerializer.Deserialize<ToolchainBuildWorkloadSpecification>(assignment.SpecificationJson)
            ?? throw new InvalidDataException("The toolchain build workload specification is empty.");
        if (assignment.DeliveryBuildId != workload.DeliveryBuildId ||
            assignment.AgentRuntimeInstanceId != workload.WorkloadId)
            throw new InvalidDataException("The toolchain workload is not bound to its exact build and runtime.");
        var runtime = assignment.AgentRuntimeInstance
            ?? throw new InvalidDataException("The toolchain runtime instance was not loaded.");
        var deliveryBuild = await dbContext.DeliveryBuilds.AsNoTracking()
            .SingleAsync(x => x.Id == workload.DeliveryBuildId, cancellationToken);
        var sourceRepository = await dbContext.SourceControlRepositories.AsNoTracking()
            .Include(x => x.Connection)
            .SingleAsync(x => x.Id == deliveryBuild.RepositoryId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (runtime.Status == AgentRuntimeStatus.Queued)
            runtime.TransitionTo(AgentRuntimeStatus.Starting, now, "Starting the assigned certified toolchain VM.");
        if (runtime.Status == AgentRuntimeStatus.Starting)
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now,
                "The certified toolchain VM is awaiting its authenticated provider session.");
        runtime.RuntimeDeadlineAt ??= now.Add(workload.ResourceLimits.MaximumDuration);
        await dbContext.SaveChangesAsync(cancellationToken);

        var provenance = JsonSerializer.Serialize(new
        {
            workload.DeliveryBuildId,
            workload.SourceRepository.CommitSha,
            workload.RecipeKey,
            workload.TargetKey,
            guestImageDigest = workload.GuestImage.Digest,
            adapterArtifactDigest = workload.AdapterArtifact.Digest,
            brokerProtocolVersion = workload.BrokerLease.ProtocolVersion
        });
        var artifact = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workload.WorkloadId,
                workload.Identity.InstallationId,
                "toolchain-output",
                workload.MaximumOutputBytes,
                "1.0",
                "linux",
                workload.GuestImage.Architecture,
                provenance),
            artifacts,
            builderPublisher,
            Path.Combine(artifactOptions.ValidatedRootPath(), ".toolchain-streams"));
        await using (artifact)
        {
            var resultTask = builderResults.WaitAsync(workload.WorkloadId, cancellationToken);
            async Task PersistResultAsync(CancellationToken token)
            {
                var result = await resultTask;
                assignment.ResultArtifactLocator = result.OpaqueLocator;
                assignment.ResultArtifactDigest = result.Artifact.Digest;
                assignment.ResultArtifactSignature = result.Artifact.Signature;
                assignment.ResultArtifactFormatVersion = result.Artifact.FormatVersion;
                assignment.ResultArtifactOperatingSystem = result.Artifact.OperatingSystem;
                assignment.ResultArtifactArchitecture = result.Artifact.Architecture;
                await dbContext.SaveChangesAsync(token);
            }
            var operations = new ToolchainBuildBrokerOperationHandler(
                workload, runtimeOperations, artifact, PersistResultAsync,
                sourceRepository.IsPrivate
                    ? token => PrepareTrustedSourceAsync(sourceRepository, deliveryBuild, workload, token)
                    : null,
                logger);
            var grant = new AgentBrokerGrant(
                workload.WorkloadId,
                workload.BrokerLease.ChannelId,
                workload.Identity.InstallationId,
                workload.GuestImage.Digest,
                workload.AdapterArtifact.Digest,
                workload.BrokerLease.ProtocolVersion,
                workload.BrokerLease.BootToken,
                workload.BrokerLease.ExpiresAt,
                new HashSet<string>(StringComparer.Ordinal)
                    { "mcp.runtime", "build.fetch", "build.artifact", "build.progress" },
                100_000, 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024);
            var boot = new GuestBootConfiguration
            {
                WorkloadId = workload.WorkloadId.ToString("D"),
                ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
                ProtocolVersion = workload.BrokerLease.ProtocolVersion,
                GuestImageDigest = workload.GuestImage.Digest,
                ArtifactDigest = workload.AdapterArtifact.Digest,
                BootToken = workload.BrokerLease.BootToken,
                LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
                ArtifactRoot = "/run/csweet/artifact/payload",
                WorkloadKind = (int)WorkloadKind.ToolchainBuild,
                InstallationId = workload.Identity.InstallationId.ToString("D"),
                BusinessId = workload.Identity.BusinessId,
                TickId = workload.Identity.TickId.ToString("D"),
                LocalBrokerSocketPath = "/run/csweet/broker.sock",
                WorkloadTokenPath = "/run/csweet/workload-token",
                MaximumFrameBytes = 16 * 1024 * 1024
            };
            var start = new StartCommand
            {
                WorkloadKind = (int)WorkloadKind.ToolchainBuild,
                MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes,
                MaximumOutputBytes = workload.MaximumOutputBytes
            };
            start.Entrypoint.AddRange([
                "/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest",
                "--adapter-entrypoint",
                "/run/csweet/artifact/payload/" + workload.Entrypoint[0]
            ]);
            start.Environment.Add("CSWEET_BUILD_ID", workload.DeliveryBuildId.ToString("D"));
            start.Environment.Add("CSWEET_BUILD_EXPECTED_REVISION", workload.ExpectedBuildRevision.ToString(CultureInfo.InvariantCulture));
            start.Environment.Add("CSWEET_BUILD_RECIPE_KEY", workload.RecipeKey);
            start.Environment.Add("CSWEET_BUILD_TARGET_KEY", workload.TargetKey);
            start.Environment.Add("CSWEET_BUILD_CONFIGURATION_JSON", workload.ConfigurationJson);
            start.Environment.Add("CSWEET_BUILD_SOURCE_URL", workload.SourceRepository.RepositoryUrl);
            start.Environment.Add("CSWEET_BUILD_SOURCE_COMMIT", workload.SourceRepository.CommitSha);
            start.Environment.Add("CSWEET_BUILD_INPUT_ROOT", "/run/csweet/workload/source");
            start.Environment.Add("CSWEET_BUILD_OUTPUT_ROOT", "/run/csweet/workload/output");
            start.Environment.Add("CSWEET_BUILD_MAXIMUM_SOURCE_BYTES", workload.MaximumSourceBytes.ToString(CultureInfo.InvariantCulture));
            start.Environment.Add("CSWEET_BUILD_MAXIMUM_OUTPUT_BYTES", workload.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture));
            start.Environment.Add("CSWEET_CERTIFIED_IMAGE_DIGEST", workload.GuestImage.Digest);
            start.Environment.Add("CSWEET_ALLOWED_DEPENDENCY_REGISTRIES", string.Join(',', workload.AllowedDependencyRegistryHosts));
            if (!string.IsNullOrWhiteSpace(deliveryBuild.CertificationFixtureResource))
                start.Environment.Add("CSWEET_CERTIFICATION_FIXTURE_RESOURCE", deliveryBuild.CertificationFixtureResource);
            var diagnostics = new RuntimeDiagnosticBrokerStreamHandler(
                workload.WorkloadId, workload.Identity.InstallationId);
            var session = new GuestBrokerHostSession(
                grant, operations, timeProvider, diagnostics, boot, start);
            try
            {
                await session.RunAsync(stream, stream, cancellationToken);
            }
            finally
            {
                await PersistBuilderDiagnosticsAsync(assignment.Id, diagnostics.Latest);
            }
            var result = await resultTask;
            logger.LogInformation(
                "Toolchain broker session completed for assignment {AssignmentId}, build {BuildId}, artifact {ArtifactDigest}.",
                assignment.Id, workload.DeliveryBuildId, result.Artifact.Digest);
        }
    }

    private async Task<ToolchainSourceArchive?> PrepareTrustedSourceAsync(
        SourceControlRepository repository,
        CSweet.Domain.Core.DeliveryBuildRecord build,
        ToolchainBuildWorkloadSpecification workload,
        CancellationToken cancellationToken)
    {
        var connection = repository.Connection
            ?? throw new InvalidOperationException("The private source repository connection is unavailable.");
        if (connection.SourceAccessInstallationId is not > 0)
            throw new InvalidOperationException("The private source repository has no active credential-isolated GitHub App installation.");
        var snapshot = await sourceControlHost.PrepareWorkspaceAsync(new TrustedWorkspaceSnapshotRequest(
            connection.SourceAccessInstallationId.Value,
            repository.Owner,
            repository.Name,
            repository.DefaultBranch,
            build.Id,
            $"build/{build.Id:N}",
            build.SourceRevision,
            $"delivery-build-source:{build.Id:N}:{build.SourceRevision}"), cancellationToken);
        if (!string.Equals(snapshot.BaseCommitSha, build.SourceRevision, StringComparison.Ordinal) ||
            snapshot.Archive.LongLength > workload.MaximumSourceBytes ||
            snapshot.TotalBytes > workload.MaximumSourceBytes ||
            snapshot.ArtifactSha256.Length != 64 || snapshot.ArtifactSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("GitHost did not return the exact bounded source revision requested by the build.");
        return new ToolchainSourceArchive(snapshot.Archive, snapshot.ArtifactSha256.ToLowerInvariant());
    }
}
