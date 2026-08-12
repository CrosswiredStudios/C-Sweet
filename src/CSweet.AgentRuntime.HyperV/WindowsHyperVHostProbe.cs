using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace CSweet.AgentRuntime.HyperV;

public sealed class WindowsHyperVHostProbe : IWindowsHyperVHostProbe
{
    private const uint ProcessorFeatureNxEnabled = 12;
    private const uint ProcessorFeatureSecondLevelAddressTranslation = 20;
    private const uint ProcessorFeatureVirtualizationFirmwareEnabled = 21;
    private const uint ProcessorFeatureHypervisorPresent = 23;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private WindowsHyperVHostReadiness? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<WindowsHyperVHostReadiness> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsHyperVHostReadiness(
                false, Environment.OSVersion.VersionString, string.Empty, false,
                false, false, false, 0, WindowsOptionalFeatureState.Unknown,
                false, false, false, null);
        }

        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && now - _cachedAt < TimeSpan.FromSeconds(30)) return _cached;
        await _probeLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && now - _cachedAt < TimeSpan.FromSeconds(30)) return _cached;
            _cached = await ProbeWindowsAsync(cancellationToken);
            _cachedAt = now;
            return _cached;
        }
        finally { _probeLock.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<WindowsHyperVHostReadiness> ProbeWindowsAsync(CancellationToken cancellationToken)
    {
        var productName = ReadRegistryString(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        var editionId = ReadRegistryString(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID") ?? "Unknown";
        var feature = await QueryFeatureStateAsync(cancellationToken);
        var diagnostic = feature.Error;
        var hypervisorPresent = IsProcessorFeaturePresent(ProcessorFeatureHypervisorPresent);
        var restartPending = IsHyperVRestartPending(
            feature.State,
            hypervisorPresent,
            IsWindowsRestartPending());

        return new WindowsHyperVHostReadiness(
            true,
            productName,
            editionId,
            IsSupportedEdition(editionId),
            IsProcessorFeaturePresent(ProcessorFeatureSecondLevelAddressTranslation),
            IsProcessorFeaturePresent(ProcessorFeatureVirtualizationFirmwareEnabled),
            IsProcessorFeaturePresent(ProcessorFeatureNxEnabled),
            GetPhysicalMemoryBytes(),
            feature.State,
            hypervisorPresent,
            restartPending,
            Environment.UserInteractive,
            diagnostic);
    }

    internal static bool IsSupportedEdition(string editionId) =>
        editionId.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) ||
        editionId.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) ||
        editionId.StartsWith("Education", StringComparison.OrdinalIgnoreCase) ||
        editionId.StartsWith("Server", StringComparison.OrdinalIgnoreCase) ||
        editionId.Equals("IoTEnterprise", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsHyperVRestartPending(
        WindowsOptionalFeatureState featureState,
        bool isHypervisorPresent,
        bool isWindowsRestartPending) =>
        featureState == WindowsOptionalFeatureState.EnablePending ||
        featureState == WindowsOptionalFeatureState.Enabled &&
        !isHypervisorPresent && isWindowsRestartPending;

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsRestartPending()
    {
        try
        {
            using var componentServicing = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var windowsUpdate = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var sessionManager = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            return componentServicing is not null || windowsUpdate is not null ||
                   sessionManager?.GetValue("PendingFileRenameOperations") is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(WindowsOptionalFeatureState State, string? Error)> QueryFeatureStateAsync(
        CancellationToken cancellationToken)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dism = Path.Combine(windowsDirectory, "System32", "dism.exe");
        if (!File.Exists(dism)) return (WindowsOptionalFeatureState.Unknown, "Windows DISM is unavailable.");

        var start = new ProcessStartInfo
        {
            FileName = dism,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/Online");
        start.ArgumentList.Add("/Get-FeatureInfo");
        start.ArgumentList.Add("/FeatureName:Microsoft-Hyper-V");
        start.ArgumentList.Add("/English");

        try
        {
            using var process = Process.Start(start);
            if (process is null) return (WindowsOptionalFeatureState.Unknown, "Windows DISM could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = ReadBoundedAsync(process.StandardOutput, 128 * 1024, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 16 * 1024, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                return (WindowsOptionalFeatureState.Unknown,
                    Sanitize(string.IsNullOrWhiteSpace(error) ? output : error));
            }

            return (ParseFeatureState(output), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (WindowsOptionalFeatureState.Unknown, "The Hyper-V feature check timed out.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return (WindowsOptionalFeatureState.Unknown, Sanitize(exception.Message));
        }
    }

    internal static WindowsOptionalFeatureState ParseFeatureState(string output)
    {
        if (output.Contains("Enable Pending", StringComparison.OrdinalIgnoreCase))
            return WindowsOptionalFeatureState.EnablePending;
        if (output.Contains("Disable Pending", StringComparison.OrdinalIgnoreCase))
            return WindowsOptionalFeatureState.DisablePending;
        if (output.Contains("State : Enabled", StringComparison.OrdinalIgnoreCase))
            return WindowsOptionalFeatureState.Enabled;
        if (output.Contains("State : Disabled", StringComparison.OrdinalIgnoreCase))
            return WindowsOptionalFeatureState.Disabled;
        return WindowsOptionalFeatureState.Unknown;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (output.Length < maximumCharacters)
        {
            var read = await reader.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maximumCharacters - output.Length)),
                cancellationToken);
            if (read == 0) break;
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private static string Sanitize(string value) =>
        new(value.Where(character => !char.IsControl(character) || character == ' ')
            .Take(256).ToArray());

    [SupportedOSPlatform("windows")]
    private static long GetPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(ref status) ? checked((long)status.TotalPhysical) : 0;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint processorFeature);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx() { }
    }
}
