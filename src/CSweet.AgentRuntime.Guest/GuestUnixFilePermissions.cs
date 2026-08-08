using System.Diagnostics;

namespace CSweet.AgentRuntime.Guest;

internal static class GuestUnixFilePermissions
{
    public const string WorkloadUser = "csweet-workload";

    public static async Task GrantWorkloadGroupAsync(
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, mode);
        var start = new ProcessStartInfo("/usr/bin/chgrp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(WorkloadUser);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Could not apply the workload broker group.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException("Could not apply the workload broker group.");
    }

    public static async Task GrantWorkloadTreeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return;
        var start = CreateGroupChangeStartInfo();
        start.ArgumentList.Add("--recursive");
        start.ArgumentList.Add("--no-dereference");
        start.ArgumentList.Add(WorkloadUser);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Could not grant workload artifact access.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException("Could not grant workload artifact access.");
    }

    private static ProcessStartInfo CreateGroupChangeStartInfo() => new("/usr/bin/chgrp")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
}
