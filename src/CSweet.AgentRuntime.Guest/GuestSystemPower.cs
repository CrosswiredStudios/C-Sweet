using System.ComponentModel;
using System.Diagnostics;

namespace CSweet.AgentRuntime.Guest;

internal static class GuestSystemPower
{
    public static async Task PowerOffAsync()
    {
        if (!OperatingSystem.IsLinux()) return;

        var executable = File.Exists("/usr/bin/systemctl")
            ? "/usr/bin/systemctl"
            : File.Exists("/sbin/poweroff") ? "/sbin/poweroff" : null;
        if (executable is null) return;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (executable.EndsWith("systemctl", StringComparison.Ordinal))
            {
                start.ArgumentList.Add("poweroff");
                start.ArgumentList.Add("--no-block");
            }
            using var process = Process.Start(start);
            if (process is not null)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or Win32Exception)
        {
            // RuntimeHost also enforces the immutable lease deadline and will
            // retry cleanup even if the guest OS cannot initiate poweroff.
        }
    }
}
