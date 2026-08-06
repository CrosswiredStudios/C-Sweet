using System.Runtime.InteropServices;

namespace CSweet.AgentRuntime.Abstractions;

public static class IsolationProviderCatalog
{
    public static IsolationProviderDescriptor HyperV(string? architecture = null) => new(
        "hyperv-gen2", "Hyper-V Generation 2", "1.0.0", "windows", architecture ?? HostArchitecture(), 300,
        new IsolationProviderCapabilities(
            IsolationAssurance.CertifiedHardwareVirtualMachine,
            UsesDedicatedKernel: true,
            SupportsBrokerSocket: true,
            SupportsReadOnlyBaseDisk: true,
            SupportsReadOnlyArtifact: true,
            SupportsEphemeralWritableDisk: true,
            SupportsCpuLimits: true,
            SupportsMemoryLimits: true,
            SupportsDiskLimits: true,
            SupportsProcessLimits: false,
            SupportsNoNetworkDevice: true,
            SupportsSecureBoot: true,
            SupportsMeasuredOrVerifiedBoot: false));

    public static IsolationProviderDescriptor Firecracker(string? architecture = null) => new(
        "firecracker-kvm", "Firecracker on KVM", "1.0.0", "linux", architecture ?? HostArchitecture(), 200,
        new IsolationProviderCapabilities(
            IsolationAssurance.CertifiedHardwareVirtualMachine,
            true, true, true, true, true, true, true, true, true, true, false, true));

    public static IsolationProviderDescriptor AppleVirtualization(string? architecture = null) => new(
        "apple-virtualization", "Apple Virtualization.framework", "1.0.0", "macos", architecture ?? HostArchitecture(), 100,
        new IsolationProviderCapabilities(
            IsolationAssurance.CertifiedHardwareVirtualMachine,
            true, true, true, true, true, true, true, true, true, true, false, true));

    private static string HostArchitecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}
