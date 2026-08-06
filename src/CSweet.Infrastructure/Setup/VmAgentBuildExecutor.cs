using System.Security.Cryptography;
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
    IBuilderArtifactResultStore results,
    IOptions<AgentRuntimeManagerOptions> options) : IPluginBuildExecutor
{
    public Task<AgentBuildWorkspace> CloneAsync(
        AgentBuildExecutionRequest request,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentBuildWorkspace(
            $"broker-source:{request.BuildJobId:N}",
            $"broker-artifact:{request.BuildJobId:N}",
            $"broker-log:{request.BuildJobId:N}"));
    }

    public async Task<AgentBuildExecutionResult> BuildAsync(
        AgentBuildExecutionRequest request,
        AgentBuildWorkspace workspace,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var configured = options.Value;
        var guestDigest = NormalizeDigest(configured.BuilderGuestImageDigest, "builder guest image");
        var guest = new GuestImageReference(
            Required(configured.BuilderGuestImageId, "builder guest image id"),
            Required(configured.BuilderGuestImageVersion, "builder guest image version"),
            guestDigest,
            configured.BuilderGuestOperatingSystem,
            configured.BuilderGuestArchitecture);
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
                request.MemoryMb,
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
        var selection = await selector.SelectAsync(new IsolationSelectionRequest(
            AgentTrustLevel.UntrustedRepository,
            new IsolationCapabilityRequirements(IsolationAssurance.CertifiedHardwareVirtualMachine),
            guestDigest,
            "1.0",
            configured.PreferredIsolationProviderId), cancellationToken);
        IsolationWorkloadHandle? handle = null;
        try
        {
            handle = await selection.Provider.CreateAsync(workload, cancellationToken);
            await selection.Provider.StartAsync(handle, cancellationToken);
            while (true)
            {
                var status = await selection.Provider.InspectAsync(handle, cancellationToken)
                    ?? throw new AgentBuildException("The builder VM disappeared before returning an artifact.");
                if (status.State is IsolationWorkloadState.Failed)
                    throw new AgentBuildException(status.SanitizedError ?? "The builder VM failed.");
                if (status.State is IsolationWorkloadState.Stopped or IsolationWorkloadState.Destroyed) break;
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            var artifact = await results.WaitAsync(workload.WorkloadId, cancellationToken);
            return new AgentBuildExecutionResult(
                artifact.OpaqueLocator,
                artifact.Artifact.Digest[7..],
                workspace.LogPath,
                artifact.Artifact.Signature,
                artifact.Artifact.FormatVersion,
                artifact.Artifact.OperatingSystem,
                artifact.Artifact.Architecture);
        }
        catch (IsolationUnavailableException exception)
        {
            throw new AgentBuildException(exception.Message, AgentBuildStepKeys.Isolate, exception);
        }
        finally
        {
            if (handle is not null)
            {
                try { await selection.Provider.DestroyAsync(handle, CancellationToken.None); }
                catch (Exception) { }
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
    }

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new AgentBuildException($"The {name} is not configured.");
    private static string NormalizeDigest(string value, string name)
    {
        var normalized = value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
        if (normalized.Length != 71 || normalized.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new AgentBuildException($"The {name} must be an immutable lowercase SHA-256 digest.");
        return normalized;
    }
}
