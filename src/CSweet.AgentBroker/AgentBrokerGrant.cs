namespace CSweet.AgentBroker;

public sealed record AgentBrokerGrant(
    Guid WorkloadId,
    Guid ChannelId,
    Guid InstallationId,
    string GuestImageDigest,
    string? ArtifactDigest,
    string ProtocolVersion,
    string BootToken,
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string> AllowedPurposes,
    int MaximumRequestCount,
    int MaximumRequestBodyBytes,
    int MaximumResponseBodyBytes,
    int MaximumFrameBytes)
{
    public void Validate(TimeProvider timeProvider)
    {
        if (WorkloadId == Guid.Empty || ChannelId == Guid.Empty || InstallationId == Guid.Empty)
            throw new InvalidOperationException("Broker grant identity is incomplete.");
        if (ProtocolVersion != "1.0" || BootToken.Length < 16 || ExpiresAt <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Broker grant authentication is invalid or expired.");
        if (!IsDigest(GuestImageDigest) || (ArtifactDigest is not null && !IsDigest(ArtifactDigest)))
            throw new InvalidOperationException("Broker grant digests are invalid.");
        if (AllowedPurposes.Count is < 1 or > 128 || AllowedPurposes.Any(p => !IsPurpose(p)))
            throw new InvalidOperationException("Broker grant purposes are invalid.");
        if (MaximumRequestCount is < 1 or > 1_000_000 ||
            MaximumRequestBodyBytes is < 0 or > 16 * 1024 * 1024 ||
            MaximumResponseBodyBytes is < 0 or > 16 * 1024 * 1024 ||
            MaximumFrameBytes is < 4096 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("Broker grant limits are invalid.");
    }

    private static bool IsPurpose(string value) => value.Length is >= 3 and <= 160 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ':' or '_');
    private static bool IsDigest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
