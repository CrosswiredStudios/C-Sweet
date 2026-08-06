using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

public sealed class FailClosedIsolationProviderSelector(
    IEnumerable<IAgentIsolationProvider> providers,
    TimeProvider timeProvider) : IAgentIsolationProviderSelector
{
    private readonly IReadOnlyList<IAgentIsolationProvider> _providers = providers.ToArray();

    public async Task<IsolationProviderSelection> SelectAsync(
        IsolationSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requirements = EnforcePlatformMinimum(request);
        var candidates = _providers
            .Where(provider => request.PreferredProviderId is null ||
                string.Equals(
                    provider.Descriptor.ProviderId,
                    request.PreferredProviderId,
                    StringComparison.Ordinal))
            .OrderByDescending(provider => provider.Descriptor.Capabilities.Assurance)
            .ThenByDescending(provider => provider.Descriptor.Priority)
            .ThenBy(provider => provider.Descriptor.ProviderId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new IsolationUnavailableException(request.PreferredProviderId is null
                ? "No agent isolation provider is registered."
                : $"The requested isolation provider '{request.PreferredProviderId}' is not registered.");
        }

        var failures = new List<string>();
        foreach (var provider in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.Descriptor.Capabilities.Satisfies(requirements))
            {
                failures.Add($"{provider.Descriptor.ProviderId}: required isolation capabilities are unavailable");
                continue;
            }

            var probe = await provider.ProbeAsync(cancellationToken);
            if (!probe.IsAvailable)
            {
                failures.Add($"{provider.Descriptor.ProviderId}: {probe.UnavailableReason ?? "provider probe failed"}");
                continue;
            }

            if (!ProviderIdentityMatches(provider.Descriptor, probe.Descriptor))
            {
                failures.Add($"{provider.Descriptor.ProviderId}: probe identity did not match the registered provider");
                continue;
            }

            var certification = probe.Certification;
            if (certification is null ||
                !certification.IsActiveAt(timeProvider.GetUtcNow()) ||
                !CertificationMatches(probe.Descriptor, certification, request))
            {
                failures.Add($"{provider.Descriptor.ProviderId}: no active matching certification");
                continue;
            }

            return new IsolationProviderSelection(provider, probe);
        }

        throw new IsolationUnavailableException(
            "No certified hardware-backed agent isolation provider is available. " +
            string.Join("; ", failures));
    }

    private static IsolationCapabilityRequirements EnforcePlatformMinimum(
        IsolationSelectionRequest request)
    {
        // C-Sweet intentionally applies the same certified VM floor to every executable plugin.
        // Trust classification remains part of policy so a stronger floor can be introduced later,
        // but it can never lower this baseline.
        var minimum = request.Requirements.MinimumAssurance < IsolationAssurance.CertifiedHardwareVirtualMachine
            ? IsolationAssurance.CertifiedHardwareVirtualMachine
            : request.Requirements.MinimumAssurance;
        return request.Requirements with { MinimumAssurance = minimum };
    }

    private static bool ProviderIdentityMatches(
        IsolationProviderDescriptor registered,
        IsolationProviderDescriptor probed) =>
        string.Equals(registered.ProviderId, probed.ProviderId, StringComparison.Ordinal) &&
        string.Equals(registered.ProviderVersion, probed.ProviderVersion, StringComparison.Ordinal) &&
        string.Equals(registered.HostOperatingSystem, probed.HostOperatingSystem, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(registered.HostArchitecture, probed.HostArchitecture, StringComparison.OrdinalIgnoreCase);

    private static bool CertificationMatches(
        IsolationProviderDescriptor descriptor,
        IsolationProviderCertification certification,
        IsolationSelectionRequest request) =>
        string.Equals(certification.ProviderId, descriptor.ProviderId, StringComparison.Ordinal) &&
        string.Equals(certification.ProviderVersion, descriptor.ProviderVersion, StringComparison.Ordinal) &&
        string.Equals(certification.HostOperatingSystem, descriptor.HostOperatingSystem, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(certification.HostArchitecture, descriptor.HostArchitecture, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(certification.GuestImageDigest, request.GuestImageDigest, StringComparison.Ordinal) &&
        string.Equals(certification.BrokerProtocolVersion, request.BrokerProtocolVersion, StringComparison.Ordinal);
}
