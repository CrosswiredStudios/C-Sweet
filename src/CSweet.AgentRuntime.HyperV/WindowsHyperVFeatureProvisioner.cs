using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace CSweet.AgentRuntime.HyperV;

public sealed class WindowsHyperVFeatureProvisioner(
    IWindowsHyperVHostProbe hostProbe) : IWindowsHyperVFeatureProvisioner
{
    public async Task<WindowsHyperVEnablementResult> LaunchEnablementAsync(
        CancellationToken cancellationToken = default)
    {
        var readiness = await hostProbe.ProbeAsync(cancellationToken);
        if (!readiness.IsWindows)
            return Failure("unsupported_host", "Hyper-V can only be enabled on Windows.");
        if (!readiness.IsSupportedEdition)
            return Failure("unsupported_edition", "This Windows edition does not include Hyper-V.");
        if (!readiness.HardwareRequirementsSatisfied)
            return Failure("hardware_requirements", "Enable hardware virtualization in UEFI/BIOS and verify the Hyper-V hardware requirements first.");
        if (readiness.FeatureState is WindowsOptionalFeatureState.Enabled or WindowsOptionalFeatureState.EnablePending)
            return new WindowsHyperVEnablementResult(true, null,
                "Hyper-V is already enabled. Restart Windows if setup is pending.", false);
        if (!readiness.CanLaunchElevation)
            return Failure("interactive_session_required",
                "C-Sweet is not running in an interactive Windows session. Run the displayed DISM command in an elevated terminal.");

        if (!OperatingSystem.IsWindows())
            return Failure("unsupported_host", "Hyper-V can only be enabled on Windows.");
        return LaunchElevatedDism();
    }

    [SupportedOSPlatform("windows")]
    private static WindowsHyperVEnablementResult LaunchElevatedDism()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dism = Path.Combine(windowsDirectory, "System32", "dism.exe");
        if (!File.Exists(dism)) return Failure("dism_unavailable", "Windows DISM is unavailable.");

        var start = new ProcessStartInfo
        {
            FileName = dism,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("/Online");
        start.ArgumentList.Add("/Enable-Feature");
        start.ArgumentList.Add("/All");
        start.ArgumentList.Add("/FeatureName:Microsoft-Hyper-V");
        start.ArgumentList.Add("/NoRestart");

        try
        {
            using var process = Process.Start(start);
            return process is null
                ? Failure("enablement_start_failed", "The elevated Hyper-V installer could not be started.")
                : new WindowsHyperVEnablementResult(true, null,
                    "Windows opened an administrator prompt to enable Hyper-V. Finish that window, then restart Windows and recheck here.", true);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Failure("elevation_cancelled", "The administrator prompt was cancelled. No Windows features were changed.");
        }
        catch (Win32Exception)
        {
            return Failure("enablement_start_failed", "Windows could not open the administrator prompt.");
        }
    }

    private static WindowsHyperVEnablementResult Failure(string errorCode, string message) =>
        new(false, errorCode, message, false);
}
