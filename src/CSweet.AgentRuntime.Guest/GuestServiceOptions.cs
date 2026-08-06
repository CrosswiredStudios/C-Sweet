namespace CSweet.AgentRuntime.Guest;

using CSweet.AgentRuntime.Protocol;

public sealed record GuestServiceOptions(
    Guid WorkloadId,
    Guid ChannelId,
    string ProtocolVersion,
    string GuestImageDigest,
    string? ArtifactDigest,
    string BootToken,
    DateTimeOffset LeaseExpiresAt,
    string ArtifactRoot,
    int WorkloadKind,
    Guid? InstallationId,
    string? BusinessId,
    Guid? TickId,
    string LocalBrokerSocketPath = "/run/csweet/broker.sock",
    string WorkloadTokenPath = "/run/csweet/workload-token",
    int MaximumFrameBytes = 1_048_576)
{
    public static GuestServiceOptions FromEnvironment()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required guest setting {name} is missing.");

        var artifactRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CSWEET_GUEST_ARTIFACT_ROOT")
            ?? "/opt/csweet/artifact/payload");
        return new GuestServiceOptions(
            Guid.Parse(Required("CSWEET_GUEST_WORKLOAD_ID")),
            Guid.Parse(Required("CSWEET_GUEST_CHANNEL_ID")),
            Required("CSWEET_GUEST_PROTOCOL_VERSION"),
            Required("CSWEET_GUEST_IMAGE_DIGEST"),
            Environment.GetEnvironmentVariable("CSWEET_GUEST_ARTIFACT_DIGEST"),
            Required("CSWEET_GUEST_BOOT_TOKEN"),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(
                Required("CSWEET_GUEST_LEASE_EXPIRES_AT"),
                System.Globalization.CultureInfo.InvariantCulture)),
            artifactRoot,
            int.Parse(Required("CSWEET_GUEST_WORKLOAD_KIND"), System.Globalization.CultureInfo.InvariantCulture),
            Guid.TryParse(Environment.GetEnvironmentVariable("CSWEET_GUEST_INSTALLATION_ID"), out var installationId) ? installationId : null,
            Environment.GetEnvironmentVariable("CSWEET_GUEST_BUSINESS_ID"),
            Guid.TryParse(Environment.GetEnvironmentVariable("CSWEET_GUEST_TICK_ID"), out var tickId) ? tickId : null,
            Environment.GetEnvironmentVariable("CSWEET_GUEST_LOCAL_BROKER_SOCKET") ?? "/run/csweet/broker.sock",
            Environment.GetEnvironmentVariable("CSWEET_GUEST_WORKLOAD_TOKEN_PATH") ?? "/run/csweet/workload-token");
    }

    public static GuestServiceOptions FromBootConfiguration(GuestBootConfiguration boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        return new GuestServiceOptions(
            Guid.Parse(boot.WorkloadId),
            Guid.Parse(boot.ChannelId),
            boot.ProtocolVersion,
            boot.GuestImageDigest,
            string.IsNullOrWhiteSpace(boot.ArtifactDigest) ? null : boot.ArtifactDigest,
            boot.BootToken,
            DateTimeOffset.FromUnixTimeSeconds(boot.LeaseExpiresAtUnixSeconds),
            boot.ArtifactRoot,
            boot.WorkloadKind,
            Guid.TryParse(boot.InstallationId, out var installationId) ? installationId : null,
            string.IsNullOrWhiteSpace(boot.BusinessId) ? null : boot.BusinessId,
            Guid.TryParse(boot.TickId, out var tickId) ? tickId : null,
            boot.LocalBrokerSocketPath,
            boot.WorkloadTokenPath,
            boot.MaximumFrameBytes);
    }

    public void Validate(TimeProvider timeProvider)
    {
        if (WorkloadId == Guid.Empty || ChannelId == Guid.Empty)
            throw new InvalidOperationException("Guest workload and channel identifiers are required.");
        if (!string.Equals(ProtocolVersion, "1.0", StringComparison.Ordinal))
            throw new InvalidOperationException("The guest broker protocol version is unsupported.");
        if (!IsSha256(GuestImageDigest) || (ArtifactDigest is not null && !IsSha256(ArtifactDigest)))
            throw new InvalidOperationException("Guest image and artifact references must be immutable SHA-256 digests.");
        if (BootToken.Length < 16)
            throw new InvalidOperationException("The guest boot token is too short.");
        if (LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("The guest lease has already expired.");
        if (!Path.IsPathFullyQualified(ArtifactRoot))
            throw new InvalidOperationException("The guest artifact root must be absolute.");
        if (WorkloadKind == 1 && !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArtifactRoot)),
                "/run/csweet/artifact/payload",
                StringComparison.Ordinal))
            throw new InvalidOperationException("The runtime artifact root must use the fixed disposable guest location.");
        if (WorkloadKind is not 0 and not 1)
            throw new InvalidOperationException("The guest workload kind is invalid.");
        if (WorkloadKind == 1 &&
            (InstallationId is null || InstallationId == Guid.Empty || TickId is null || TickId == Guid.Empty ||
             string.IsNullOrWhiteSpace(BusinessId) || !Guid.TryParse(BusinessId, out _)))
            throw new InvalidOperationException("The runtime guest identity is incomplete.");
        ValidateGuestRuntimePath(LocalBrokerSocketPath, "local broker socket");
        ValidateGuestRuntimePath(WorkloadTokenPath, "workload token");
        if (MaximumFrameBytes is < 4096 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("The guest frame limit is invalid.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void ValidateGuestRuntimePath(string path, string name)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath("/run/csweet"));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidOperationException($"The guest {name} path must remain beneath /run/csweet.");
    }
}
