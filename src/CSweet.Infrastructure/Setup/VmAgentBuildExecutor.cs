using System.Security.Cryptography;
using System.Text;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.Application.Setup;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Orchestrates repository materialization and builds entirely inside a disposable
/// builder VM. Source and artifact bytes travel through the broker and are represented
/// here only by opaque locators; the control plane never checks out untrusted source.
/// </summary>
public sealed class VmAgentBuildExecutor(
    IAgentIsolationProviderSelector selector,
    IGuestImageRegistry guestImages,
    IBuilderGuestSessionCoordinator guestSessions,
    IBuilderArtifactResultStore results,
    IOptions<AgentRuntimeManagerOptions> options) : IPluginBuildExecutor
{
    private const int MinimumBuilderMemoryMegabytes = 4096;

    public Task<AgentBuildWorkspace> CloneAsync(
        AgentBuildExecutionRequest request,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var logRoot = Path.GetFullPath(options.Value.BuildLogStorePath);
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, $"build-{request.BuildJobId:N}.log");
        File.WriteAllText(
            logPath,
            $"[{DateTimeOffset.UtcNow:O}] Build {request.BuildJobId:D} queued for isolated execution.{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.FromResult(new AgentBuildWorkspace(
            $"broker-source:{request.BuildJobId:N}",
            $"broker-artifact:{request.BuildJobId:N}",
            logPath));
    }

    public async Task<AgentBuildExecutionResult> BuildAsync(
        AgentBuildExecutionRequest request,
        AgentBuildWorkspace workspace,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var maximumLogBytes = checked(request.MaximumBuildLogMb * 1024L * 1024L);
        await TryAppendLogAsync(
            workspace.LogPath,
            $"Resolving certified builder guest and provider for build {request.BuildJobId:D}.",
            maximumLogBytes,
            cancellationToken);
        await progress.ReportAsync(
            new AgentBuildProgressUpdate(
                AgentBuildStepKeys.Isolate,
                AgentBuildStepStatuses.InProgress,
                "Resolving the certified builder image and hardware-isolation provider."),
            cancellationToken);
        var configured = options.Value;
        GuestImageReference guest;
        try
        {
            guest = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
                configured.BuilderGuestImageId,
                configured.BuilderGuestImageVersion,
                configured.BuilderGuestOperatingSystem,
                configured.BuilderGuestArchitecture,
                AgentTrustLevel.UntrustedRepository,
                "1.0",
                configured.PreferredIsolationProviderId,
                configured.BuilderGuestImageDigest,
                configured.RequiredCertificationSuiteVersion), cancellationToken);
        }
        catch (IsolationUnavailableException exception)
        {
            await TryAppendLogAsync(workspace.LogPath, exception.ToString(), maximumLogBytes, CancellationToken.None);
            throw new AgentBuildException(exception.Message, AgentBuildStepKeys.Isolate, exception);
        }
        var guestDigest = guest.Digest;
        var bootToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var lease = new BrokerChannelLease(
            Guid.NewGuid(), "1.0", bootToken, guestDigest, null,
            DateTimeOffset.UtcNow.AddSeconds(request.TimeoutSeconds).AddMinutes(5));
        var workload = new BuilderWorkloadSpec(
            request.BuildJobId,
            guest,
            new IsolationResourceLimits(
                Math.Max(1, (int)Math.Ceiling(request.CpuPercent / 100d)),
                request.CpuPercent,
                Math.Max(request.MemoryMb, MinimumBuilderMemoryMegabytes),
                Math.Max(request.MaximumRepositorySizeMb * 3, 512),
                request.PidsLimit,
                checked(request.MaximumBuildLogMb * 1024 * 1024),
                TimeSpan.FromSeconds(request.TimeoutSeconds)),
            lease,
            new RepositoryDescriptor(
                request.RepositoryUrl,
                request.CommitSha,
                false,
                request.BuildProfileId,
                "1.0"),
            checked((long)request.MaximumRepositorySizeMb * 1024 * 1024));
        IsolationProviderSelection selection;
        try
        {
            selection = await selector.SelectAsync(new IsolationSelectionRequest(
                AgentTrustLevel.UntrustedRepository,
                new IsolationCapabilityRequirements(IsolationAssurance.CertifiedHardwareVirtualMachine),
                guestDigest,
                "1.0",
                configured.PreferredIsolationProviderId), cancellationToken);
        }
        catch (IsolationUnavailableException exception)
        {
            await TryAppendLogAsync(workspace.LogPath, exception.ToString(), maximumLogBytes, CancellationToken.None);
            throw new AgentBuildException(exception.Message, AgentBuildStepKeys.Isolate, exception);
        }
        IsolationWorkloadHandle? handle = null;
        try
        {
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Isolate,
                    AgentBuildStepStatuses.InProgress,
                    "Creating the disposable hardware-isolated builder VM."),
                cancellationToken);
            await TryAppendLogAsync(
                workspace.LogPath,
                $"Creating disposable builder workload {workload.WorkloadId:D} with provider {selection.Provider.Descriptor.ProviderId}.",
                maximumLogBytes,
                cancellationToken);
            handle = await selection.Provider.CreateAsync(workload, cancellationToken);
            await TryAppendLogAsync(
                workspace.LogPath,
                $"Created provider instance {handle.ProviderInstanceId}; starting the builder guest.",
                maximumLogBytes,
                cancellationToken);
            await progress.ReportAsync(
                new AgentBuildProgressUpdate(
                    AgentBuildStepKeys.Isolate,
                    AgentBuildStepStatuses.InProgress,
                    "Starting the disposable builder guest and establishing its authenticated session."),
                cancellationToken);
            await selection.Provider.StartAsync(handle, cancellationToken);
            await using var session = await guestSessions.StartAsync(
                handle, workload, request, progress, cancellationToken);
            await TryAppendLogAsync(
                workspace.LogPath,
                "The authenticated builder broker session started.",
                maximumLogBytes,
                cancellationToken);
            var artifactTask = results.WaitAsync(workload.WorkloadId, cancellationToken);
            await session.Completion.WaitAsync(cancellationToken);
            var artifact = await artifactTask;
            await TryAppendLogAsync(
                workspace.LogPath,
                $"Builder completed and produced immutable artifact {artifact.Artifact.Digest}.",
                maximumLogBytes,
                cancellationToken);
            return new AgentBuildExecutionResult(
                artifact.OpaqueLocator,
                artifact.Artifact.Digest[7..],
                workspace.LogPath,
                artifact.Artifact.Signature,
                artifact.Artifact.FormatVersion,
                artifact.Artifact.OperatingSystem,
                artifact.Artifact.Architecture);
        }
        catch (Exception exception)
        {
            await TryAppendLogAsync(workspace.LogPath, exception.ToString(), maximumLogBytes, CancellationToken.None);
            if (exception is GuestWorkloadExitedException { SanitizedDetail: { Length: > 0 } detail })
                await TryAppendLogAsync(
                    workspace.LogPath,
                    "Complete guest diagnostic tail:" + Environment.NewLine + detail,
                    maximumLogBytes,
                    CancellationToken.None);
            throw;
        }
        finally
        {
            if (handle is not null)
            {
                await TryCollectGuestLogsAsync(
                    selection.Provider,
                    handle,
                    workspace.LogPath,
                    maximumLogBytes);
                try
                {
                    await selection.Provider.DestroyAsync(handle, CancellationToken.None);
                    await TryAppendLogAsync(
                        workspace.LogPath,
                        $"Destroyed disposable provider instance {handle.ProviderInstanceId}.",
                        maximumLogBytes,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    await TryAppendLogAsync(
                        workspace.LogPath,
                        $"Cleanup failed: {exception}",
                        maximumLogBytes,
                        CancellationToken.None);
                }
            }
        }
    }

    public Task CleanupWorkspaceAsync(AgentBuildWorkspace workspace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static void ValidateRequest(AgentBuildExecutionRequest request)
    {
        if (request.BuildJobId == Guid.Empty || request.PackageVersionId == Guid.Empty ||
            !Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute, out var repository) || repository.Scheme != Uri.UriSchemeHttps ||
            repository.UserInfo.Length != 0 || request.CommitSha.Length != 40 ||
            request.CommitSha.Any(character => !Uri.IsHexDigit(character)) ||
            request.BuildProfileId.Length is < 3 or > 80 ||
            request.BuildProfileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-') ||
            request.TimeoutSeconds < 1 || request.MemoryMb < 128 || request.CpuPercent < 1 ||
            request.PidsLimit < 1 || request.MaximumRepositorySizeMb < 1 || request.MaximumBuildLogMb < 1)
            throw new AgentBuildException("The brokered builder request is invalid.");
        if (request.TargetFramework is not null && request.TargetFramework is not ("net8.0" or "net9.0" or "net10.0"))
            throw new AgentBuildException("The approved target framework is not supported by the Linux build profile.");
    }

    private static async Task TryCollectGuestLogsAsync(
        IAgentIsolationProvider provider,
        IsolationWorkloadHandle handle,
        string logPath,
        long maximumBytes)
    {
        try
        {
            await TryAppendLogAsync(logPath, "Guest output:", maximumBytes, CancellationToken.None);
            // RuntimeHost intentionally caps a single log response at 1 MiB. Build logs can be
            // larger, so collect the provider tail without asking the privileged service for an
            // invalid response size.
            var remaining = (int)Math.Clamp(maximumBytes - new FileInfo(logPath).Length, 1, 1024 * 1024);
            await foreach (var chunk in provider.StreamLogsAsync(handle, remaining, CancellationToken.None))
            {
                var content = Encoding.UTF8.GetString(chunk.Content.Span);
                await TryAppendLogAsync(
                    logPath,
                    $"[{chunk.OccurredAt:O}] [{chunk.Stream}] {content}",
                    maximumBytes,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            await TryAppendLogAsync(
                logPath,
                $"Guest log collection failed: {exception}",
                maximumBytes,
                CancellationToken.None);
        }
    }

    private static async Task TryAppendLogAsync(
        string path,
        string message,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length >= maximumBytes) return;
            var line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(line);
            var remaining = maximumBytes - info.Length;
            var count = (int)Math.Min(bytes.LongLength, remaining);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Build diagnostics must never mask or alter the isolated build result.
        }
    }

}
