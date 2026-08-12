using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

/// <summary>
/// Resolves the guest image identity from the active provider certification. A configured
/// digest remains an optional pin, but the control plane does not duplicate RuntimeHost's
/// certified image configuration.
/// </summary>
public sealed class CertifiedGuestImageRegistry(IAgentIsolationProviderSelector selector) : IGuestImageRegistry
{
    public async Task<GuestImageReference> ResolveAsync(
        GuestImageResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logicalImageId = Required(request.LogicalImageId, nameof(request.LogicalImageId));
        var operatingSystem = Required(request.OperatingSystem, nameof(request.OperatingSystem));
        var architecture = Required(request.Architecture, nameof(request.Architecture));
        var brokerProtocolVersion = Required(request.BrokerProtocolVersion, nameof(request.BrokerProtocolVersion));
        var expectedDigest = string.IsNullOrWhiteSpace(request.ExpectedDigest)
            ? null
            : NormalizeDigest(request.ExpectedDigest);

        var selection = await selector.SelectAsync(new IsolationSelectionRequest(
            request.TrustLevel,
            new IsolationCapabilityRequirements(IsolationAssurance.CertifiedHardwareVirtualMachine),
            expectedDigest,
            brokerProtocolVersion,
            request.PreferredProviderId), cancellationToken);
        var certification = selection.Probe.Certification
            ?? throw new IsolationUnavailableException("The selected isolation provider did not return certification evidence.");
        var digest = NormalizeDigest(certification.GuestImageDigest);
        var certifiedVersion = Required(
            certification.CertificationSuiteVersion,
            nameof(certification.CertificationSuiteVersion));
        if (!string.IsNullOrWhiteSpace(request.RequiredCertificationSuiteVersion) &&
            !string.Equals(
                request.RequiredCertificationSuiteVersion.Trim(),
                certifiedVersion,
                StringComparison.Ordinal))
            throw new IsolationUnavailableException(
                $"The installed secure agent runtime is out of date (installed: {certifiedVersion}; " +
                $"required: {request.RequiredCertificationSuiteVersion.Trim()}). " +
                "Open Agent Execution setup and prepare the secure agent runtime before retrying.");
        var version = string.IsNullOrWhiteSpace(request.Version)
            ? certifiedVersion
            : request.Version.Trim();

        return new GuestImageReference(logicalImageId, version, digest, operatingSystem, architecture);
    }

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required.", name);

    private static string NormalizeDigest(string value)
    {
        var normalized = value.StartsWith("sha256:", StringComparison.Ordinal)
            ? value
            : $"sha256:{value}";
        if (normalized.Length != 71 || normalized.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new IsolationUnavailableException(
                "The certified guest image identity is not an immutable lowercase SHA-256 digest.");
        return normalized;
    }
}
