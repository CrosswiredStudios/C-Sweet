using CSweet.Isolation.HyperV;
using Microsoft.Win32;

namespace CSweet.Compute.HyperV;

internal static class ComputeGuestServiceRegistration
{
    public const string ElementName = "C-Sweet Compute Guest";
    public static string RegistryPath => WindowsHyperVSocketServiceRegistration.ServiceKeyPath(
        HyperVSocketTransportOptions.LinuxVsockServiceId(HyperVGuestReadinessTransport.GuestPort));

    public static void Validate()
    {
        Validate(path =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Hyper-V guest registration requires Windows.");
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return key?.GetValue("ElementName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        });
    }

    internal static void Validate(Func<string, string?> readElementName)
    {
        if (!string.Equals(readElementName(RegistryPath), ElementName, StringComparison.Ordinal))
            throw new InvalidDataException("The compute guest communication service is missing or conflicts with installed registration.");
    }
}
