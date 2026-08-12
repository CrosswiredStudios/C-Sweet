using System.Runtime.InteropServices;
using CSweet.AgentRuntime.Abstractions;
using CSweet.Contracts.Setup;

namespace CSweet.ExecutionNode;

public sealed class RuntimeHostInventory(IEnumerable<IAgentIsolationProvider> providers)
{
    public async Task<IReadOnlyList<RegisterExecutionNodeProviderRequest>> ProbeAsync(CancellationToken cancellationToken)
    {
        var inventory = new List<RegisterExecutionNodeProviderRequest>();
        foreach (var provider in providers.Where(IsCurrentPlatformProvider))
        {
            try
            {
                var probe = await provider.ProbeAsync(cancellationToken);
                var certification = probe.Certification;
                inventory.Add(new RegisterExecutionNodeProviderRequest(
                    probe.Descriptor.ProviderId,
                    probe.Descriptor.ProviderVersion,
                    certification?.BrokerProtocolVersion ?? "",
                    certification?.GuestImageDigest ?? "",
                    certification?.CertificationSuiteVersion ?? "",
                    certification?.EvidenceDigest ?? "",
                    certification?.CertifiedAt ?? DateTimeOffset.MinValue,
                    certification?.ExpiresAt,
                    SupportsBuilderWorkloads: true,
                    SupportsRuntimeWorkloads: true,
                    probe.IsAvailable && certification?.IsActiveAt(DateTimeOffset.UtcNow) == true,
                    probe.UnavailableReason ?? (certification is null ? "Provider certification is unavailable." : null)));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or IsolationUnavailableException)
            {
                inventory.Add(new RegisterExecutionNodeProviderRequest(
                    provider.Descriptor.ProviderId, provider.Descriptor.ProviderVersion, "", "", "", "",
                    DateTimeOffset.MinValue, null, true, true, false,
                    $"RuntimeHost probe failed: {exception.GetType().Name}."));
            }
        }
        return inventory;
    }

    private static bool IsCurrentPlatformProvider(IAgentIsolationProvider provider) =>
        string.Equals(provider.Descriptor.HostOperatingSystem, Platform(), StringComparison.OrdinalIgnoreCase);

    public static string Platform() => OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" :
        RuntimeInformation.OSDescription;
}
