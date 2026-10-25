using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Setup;

namespace CSweet.Infrastructure.Setup;

/// <summary>Host volume discovery, never browser filesystem discovery. Installation revalidates it elevated.</summary>
internal static class LocalOfficeStorageProbe
{
    private static readonly object Sync = new();
    private static DateTimeOffset lastRead;
    private static IReadOnlyList<LocalOfficeVolumeResponse> cached = [];

    internal static IReadOnlyList<LocalOfficeVolumeResponse> GetWindowsVolumes(bool refresh)
    {
        lock (Sync)
        {
            if (!refresh && DateTimeOffset.UtcNow - lastRead < TimeSpan.FromSeconds(15)) return cached;
            try
            {
                // Query the backing disk too: a USB device can advertise DriveType=Fixed.
                const string script = """
                    $ErrorActionPreference = 'Stop'
                    $result = @(Get-Volume | Where-Object DriveLetter | ForEach-Object {
                        $v = $_; $reason = $null
                        try { $disks = @($v | Get-Partition | Get-Disk -ErrorAction Stop) } catch { $disks = @() }
                        if ($v.DriveType -ne 'Fixed' -or $disks.Count -eq 0 -or @($disks | Where-Object { $_.BusType -in @('USB','SD','MMC','1394','iSCSI') -or $_.IsOffline -or $_.IsReadOnly }).Count -gt 0) { $reason = 'Choose a writable fixed local drive.' }
                        elseif ($v.FileSystemType -notin @('NTFS','ReFS')) { $reason = 'Office requires NTFS or ReFS on Windows.' }
                        elseif ($v.HealthStatus -ne 'Healthy') { $reason = 'This volume is not healthy or is unavailable.' }
                        [pscustomobject]@{ Id = [string]$v.UniqueId; MountPath = "$($v.DriveLetter):\"; Label = [string]$v.FileSystemLabel; FileSystem = [string]$v.FileSystemType; TotalBytes = [long]$v.Size; FreeBytes = [long]$v.SizeRemaining; IsSystemVolume = ("$($v.DriveLetter):" -eq $env:SystemDrive); IsEligible = ($null -eq $reason); UnavailableReason = $reason }
                    })
                    ConvertTo-Json -InputObject $result -Compress
                    """;
                var start = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                             Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Volume discovery could not start.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                    throw new IOException("Volume discovery timed out.");
                }
                if (process.ExitCode != 0) throw new IOException("Volume discovery failed.");
                var volumes = JsonSerializer.Deserialize<DiscoveredVolume[]>(output.GetAwaiter().GetResult()) ?? [];
                cached = volumes.Select(v =>
                {
                    var root = Path.Combine(v.MountPath, "CSweet", "Office");
                    return new LocalOfficeVolumeResponse(v.Id, v.MountPath, v.Label, v.FileSystem,
                        v.TotalBytes, v.FreeBytes, v.IsSystemVolume, v.IsEligible, v.UnavailableReason,
                        v.IsSystemVolume ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CSweet", "Office") : Path.Combine(root, "program"),
                        v.IsSystemVolume ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "Office") : Path.Combine(root, "data"));
                }).OrderByDescending(v => v.IsSystemVolume).ThenBy(v => v.MountPath).ToArray();
                _ = error.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or JsonException or System.ComponentModel.Win32Exception)
            {
                cached = []; // Never retain stale disk identities after a failed discovery.
            }
            lastRead = DateTimeOffset.UtcNow;
            return cached;
        }
    }

    private sealed record DiscoveredVolume(string Id, string MountPath, string Label, string FileSystem,
        long TotalBytes, long FreeBytes, bool IsSystemVolume, bool IsEligible, string? UnavailableReason);
}
