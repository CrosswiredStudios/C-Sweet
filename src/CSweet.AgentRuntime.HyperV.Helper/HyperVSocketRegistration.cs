using System.Runtime.Versioning;
using CSweet.AgentRuntime.HyperV;
using Microsoft.Win32;

namespace CSweet.AgentRuntime.HyperV.Helper;

internal static class HyperVSocketRegistration
{
    public static bool IsConfigured()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsConfiguredWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsConfiguredWindows()
    {
        var configured = Environment.GetEnvironmentVariable("CSWEET_HYPERV_BROKER_SERVICE_ID");
        if (!Guid.TryParse(configured, out var serviceId) || serviceId == Guid.Empty) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                WindowsHyperVSocketServiceRegistration.ServiceKeyPath(serviceId),
                writable: false);
            return key is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
