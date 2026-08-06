using A = CSweet.AgentRuntime.Abstractions;
using P = CSweet.AgentRuntime.Protocol;

namespace CSweet.AgentRuntime.LocalRpc;

public static class RuntimeHostProtocolMapper
{
    public static P.CreateWorkloadRequest ToProtocol(string providerId, A.IsolationWorkloadSpec workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        workload.ResourceLimits.Validate();
        ValidateDigest(workload.GuestImage.Digest, nameof(workload.GuestImage.Digest));
        if (workload.BrokerLease.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("The broker lease must not be expired.", nameof(workload));
        if (!string.Equals(
                workload.BrokerLease.ExpectedGuestImageDigest,
                workload.GuestImage.Digest,
                StringComparison.Ordinal))
            throw new ArgumentException("The broker lease is not bound to the requested guest image.", nameof(workload));

        var request = new P.CreateWorkloadRequest
        {
            ProviderId = Required(providerId, nameof(providerId), 100),
            WorkloadId = workload.WorkloadId.ToString("D"),
            WorkloadKind = (int)workload.Kind,
            GuestImageId = Required(workload.GuestImage.ImageId, nameof(workload.GuestImage.ImageId), 200),
            GuestImageVersion = Required(workload.GuestImage.Version, nameof(workload.GuestImage.Version), 100),
            GuestImageDigest = workload.GuestImage.Digest,
            GuestOperatingSystem = Required(workload.GuestImage.OperatingSystem, nameof(workload.GuestImage.OperatingSystem), 50),
            GuestArchitecture = Required(workload.GuestImage.Architecture, nameof(workload.GuestImage.Architecture), 50),
            ResourceLimits = ToProtocol(workload.ResourceLimits),
            BrokerLease = ToProtocol(workload.BrokerLease)
        };
        switch (workload)
        {
            case A.BuilderWorkloadSpec builder:
                ValidateRepository(builder.Repository);
                if (builder.MaximumArtifactBytes is < 1 or > 10L * 1024 * 1024 * 1024)
                    throw new ArgumentOutOfRangeException(nameof(builder.MaximumArtifactBytes));
                request.Builder = new P.BuilderSpec
                {
                    RepositoryUrl = builder.Repository.RepositoryUrl,
                    CommitSha = builder.Repository.CommitSha,
                    IncludeSubmodules = builder.Repository.IncludeSubmodules,
                    BuildProfileId = builder.Repository.BuildProfileId,
                    BuildProfileVersion = builder.Repository.BuildProfileVersion,
                    MaximumArtifactBytes = builder.MaximumArtifactBytes
                };
                break;
            case A.RuntimeWorkloadSpec runtime:
                ValidateDigest(runtime.Artifact.Digest, nameof(runtime.Artifact.Digest));
                if (runtime.Identity.InstallationId == Guid.Empty || runtime.Identity.TickId == Guid.Empty)
                    throw new ArgumentException("The runtime agent identity is incomplete.", nameof(workload));
                if (!string.Equals(
                        workload.BrokerLease.ExpectedArtifactDigest,
                        runtime.Artifact.Digest,
                        StringComparison.Ordinal))
                    throw new ArgumentException("The broker lease is not bound to the requested artifact.", nameof(workload));
                if (runtime.Entrypoint.Count is < 1 or > 32 || runtime.Entrypoint.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 1024))
                    throw new ArgumentException("The runtime entrypoint is invalid.", nameof(workload));
                request.Runtime = new P.RuntimeSpec
                {
                    ArtifactDigest = runtime.Artifact.Digest,
                    ArtifactSignature = Required(runtime.Artifact.Signature, nameof(runtime.Artifact.Signature), 4096),
                    ArtifactFormatVersion = Required(runtime.Artifact.FormatVersion, nameof(runtime.Artifact.FormatVersion), 50),
                    ArtifactOperatingSystem = Required(runtime.Artifact.OperatingSystem, nameof(runtime.Artifact.OperatingSystem), 50),
                    ArtifactArchitecture = Required(runtime.Artifact.Architecture, nameof(runtime.Artifact.Architecture), 50),
                    InstallationId = runtime.Identity.InstallationId.ToString("D"),
                    BusinessId = Required(runtime.Identity.BusinessId, nameof(runtime.Identity.BusinessId), 200),
                    TickId = runtime.Identity.TickId.ToString("D")
                };
                request.Runtime.Entrypoint.Add(runtime.Entrypoint);
                break;
            default:
                throw new ArgumentException("The isolation workload type is unsupported.", nameof(workload));
        }
        return request;
    }

    public static A.IsolationWorkloadSpec FromProtocol(P.CreateWorkloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.WorkloadId, out var workloadId))
            throw new InvalidDataException("The workload identifier is invalid.");
        if (!Enum.IsDefined(typeof(A.IsolationWorkloadKind), request.WorkloadKind))
            throw new InvalidDataException("The workload kind is invalid.");
        var image = new A.GuestImageReference(
            Required(request.GuestImageId, nameof(request.GuestImageId), 200),
            Required(request.GuestImageVersion, nameof(request.GuestImageVersion), 100),
            request.GuestImageDigest,
            Required(request.GuestOperatingSystem, nameof(request.GuestOperatingSystem), 50),
            Required(request.GuestArchitecture, nameof(request.GuestArchitecture), 50));
        ValidateDigest(image.Digest, nameof(request.GuestImageDigest));
        var limits = FromProtocol(request.ResourceLimits ?? throw new InvalidDataException("Resource limits are required."));
        var lease = FromProtocol(request.BrokerLease ?? throw new InvalidDataException("A broker lease is required."));
        if (!string.Equals(lease.ExpectedGuestImageDigest, image.Digest, StringComparison.Ordinal))
            throw new InvalidDataException("The broker lease guest image binding is invalid.");

        return (A.IsolationWorkloadKind)request.WorkloadKind switch
        {
            A.IsolationWorkloadKind.Builder when request.Builder is not null && request.Runtime is null =>
                Builder(workloadId, image, limits, lease, request.Builder),
            A.IsolationWorkloadKind.Runtime when request.Runtime is not null && request.Builder is null =>
                Runtime(workloadId, image, limits, lease, request.Runtime),
            _ => throw new InvalidDataException("The workload body does not match its declared kind.")
        };
    }

    public static P.WorkloadOperationRequest ToProtocol(A.IsolationWorkloadHandle handle) => new()
    {
        ProviderId = Required(handle.ProviderId, nameof(handle.ProviderId), 100),
        WorkloadId = handle.WorkloadId.ToString("D"),
        ProviderInstanceId = Required(handle.ProviderInstanceId, nameof(handle.ProviderInstanceId), 200),
        WorkloadKind = (int)handle.Kind
    };

    public static A.IsolationWorkloadHandle FromProtocol(P.WorkloadOperationRequest handle)
    {
        if (!Guid.TryParse(handle.WorkloadId, out var workloadId) ||
            !Enum.IsDefined(typeof(A.IsolationWorkloadKind), handle.WorkloadKind))
            throw new InvalidDataException("The workload handle is invalid.");
        return new A.IsolationWorkloadHandle(
            Required(handle.ProviderId, nameof(handle.ProviderId), 100),
            workloadId,
            Required(handle.ProviderInstanceId, nameof(handle.ProviderInstanceId), 200),
            (A.IsolationWorkloadKind)handle.WorkloadKind);
    }

    private static A.BuilderWorkloadSpec Builder(Guid id, A.GuestImageReference image, A.IsolationResourceLimits limits, A.BrokerChannelLease lease, P.BuilderSpec builder)
    {
        var repository = new A.RepositoryDescriptor(
            builder.RepositoryUrl,
            builder.CommitSha,
            builder.IncludeSubmodules,
            builder.BuildProfileId,
            builder.BuildProfileVersion);
        ValidateRepository(repository);
        if (builder.MaximumArtifactBytes is < 1 or > 10L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The artifact size limit is invalid.");
        return new A.BuilderWorkloadSpec(id, image, limits, lease, repository, builder.MaximumArtifactBytes);
    }

    private static A.RuntimeWorkloadSpec Runtime(Guid id, A.GuestImageReference image, A.IsolationResourceLimits limits, A.BrokerChannelLease lease, P.RuntimeSpec runtime)
    {
        ValidateDigest(runtime.ArtifactDigest, nameof(runtime.ArtifactDigest));
        if (!string.Equals(lease.ExpectedArtifactDigest, runtime.ArtifactDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The broker lease artifact binding is invalid.");
        var entrypoint = runtime.Entrypoint.ToArray();
        if (entrypoint.Length is < 1 or > 32 || entrypoint.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 1024))
            throw new InvalidDataException("The runtime entrypoint is invalid.");
        if (!Guid.TryParse(runtime.InstallationId, out var installationId) || installationId == Guid.Empty ||
            !Guid.TryParse(runtime.TickId, out var tickId) || tickId == Guid.Empty)
            throw new InvalidDataException("The runtime agent identity is invalid.");
        var artifact = new A.AgentArtifactReference(
            runtime.ArtifactDigest,
            Required(runtime.ArtifactSignature, nameof(runtime.ArtifactSignature), 4096),
            Required(runtime.ArtifactFormatVersion, nameof(runtime.ArtifactFormatVersion), 50),
            Required(runtime.ArtifactOperatingSystem, nameof(runtime.ArtifactOperatingSystem), 50),
            Required(runtime.ArtifactArchitecture, nameof(runtime.ArtifactArchitecture), 50));
        var identity = new A.RuntimeAgentIdentity(
            installationId,
            Required(runtime.BusinessId, nameof(runtime.BusinessId), 200),
            tickId);
        return new A.RuntimeWorkloadSpec(id, image, limits, lease, artifact, identity, entrypoint);
    }

    private static P.ResourceLimits ToProtocol(A.IsolationResourceLimits limits) => new()
    {
        VirtualCpuCount = limits.VirtualCpuCount,
        CpuPercent = limits.CpuPercent,
        MemoryMegabytes = limits.MemoryMegabytes,
        WritableDiskMegabytes = limits.WritableDiskMegabytes,
        MaximumProcessCount = limits.MaximumProcessCount,
        MaximumLogBytes = limits.MaximumLogBytes,
        MaximumDurationSeconds = checked((long)limits.MaximumDuration.TotalSeconds)
    };

    private static A.IsolationResourceLimits FromProtocol(P.ResourceLimits limits)
    {
        var result = new A.IsolationResourceLimits(
            limits.VirtualCpuCount,
            limits.CpuPercent,
            limits.MemoryMegabytes,
            limits.WritableDiskMegabytes,
            limits.MaximumProcessCount,
            limits.MaximumLogBytes,
            TimeSpan.FromSeconds(limits.MaximumDurationSeconds));
        result.Validate();
        return result;
    }

    private static P.BrokerLease ToProtocol(A.BrokerChannelLease lease) => new()
    {
        ChannelId = lease.ChannelId.ToString("D"),
        ProtocolVersion = Required(lease.ProtocolVersion, nameof(lease.ProtocolVersion), 50),
        BootToken = Required(lease.BootToken, nameof(lease.BootToken), 4096),
        ExpectedGuestImageDigest = lease.ExpectedGuestImageDigest,
        ExpectedArtifactDigest = lease.ExpectedArtifactDigest ?? string.Empty,
        ExpiresAtUnixSeconds = lease.ExpiresAt.ToUnixTimeSeconds()
    };

    private static A.BrokerChannelLease FromProtocol(P.BrokerLease lease)
    {
        if (!Guid.TryParse(lease.ChannelId, out var channelId))
            throw new InvalidDataException("The broker channel identifier is invalid.");
        DateTimeOffset expiry;
        try { expiry = DateTimeOffset.FromUnixTimeSeconds(lease.ExpiresAtUnixSeconds); }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("The broker lease expiry is invalid.", exception); }
        if (expiry <= DateTimeOffset.UtcNow) throw new InvalidDataException("The broker lease is expired.");
        return new A.BrokerChannelLease(
            channelId,
            Required(lease.ProtocolVersion, nameof(lease.ProtocolVersion), 50),
            Required(lease.BootToken, nameof(lease.BootToken), 4096),
            lease.ExpectedGuestImageDigest,
            string.IsNullOrEmpty(lease.ExpectedArtifactDigest) ? null : lease.ExpectedArtifactDigest,
            expiry);
    }

    private static void ValidateRepository(A.RepositoryDescriptor repository)
    {
        if (!Uri.TryCreate(repository.RepositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The repository URL must be an HTTPS URL without embedded credentials.");
        if (repository.CommitSha.Length != 40 || repository.CommitSha.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The repository commit must be an exact SHA-1 identifier.");
        _ = Required(repository.BuildProfileId, nameof(repository.BuildProfileId), 100);
        _ = Required(repository.BuildProfileVersion, nameof(repository.BuildProfileVersion), 100);
    }

    private static void ValidateDigest(string digest, string name)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || digest[7..].Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{name} must be a sha256 digest.");
    }

    private static string Required(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new InvalidDataException($"{name} is missing or invalid.");
        return value;
    }
}
