using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CSweet.AgentRuntime.HyperV.Helper;

internal static class PowerShellHyperV
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private const string CreateShellScript = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vmName = $env:CSWEET_VM_NAME
        $vmPath = $env:CSWEET_VM_PATH
        $memoryBytes = [Int64]::Parse($env:CSWEET_MEMORY_BYTES, [Globalization.CultureInfo]::InvariantCulture)
        $vm = New-VM -Name $vmName -Generation 2 -MemoryStartupBytes $memoryBytes -NoVHD -Path $vmPath -ErrorAction Stop
        Get-VMNetworkAdapter -VM $vm -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false -ErrorAction Stop
        Get-VMDvdDrive -VM $vm -ErrorAction SilentlyContinue | Remove-VMDvdDrive -Confirm:$false -ErrorAction Stop
        $vm.Id.Guid
        """;

    private const string ConfigureScript = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vmName = $env:CSWEET_VM_NAME
        $baseDisk = $env:CSWEET_BASE_DISK
        $osDisk = $env:CSWEET_OS_DISK
        $scratchDisk = $env:CSWEET_SCRATCH_DISK
        $artifactIso = $env:CSWEET_ARTIFACT_ISO
        $memoryBytes = [Int64]::Parse($env:CSWEET_MEMORY_BYTES, [Globalization.CultureInfo]::InvariantCulture)
        $scratchBytes = [UInt64]::Parse($env:CSWEET_SCRATCH_BYTES, [Globalization.CultureInfo]::InvariantCulture)
        $cpuCount = [Int32]::Parse($env:CSWEET_CPU_COUNT, [Globalization.CultureInfo]::InvariantCulture)
        $cpuMaximum = [Int64]::Parse($env:CSWEET_CPU_MAXIMUM, [Globalization.CultureInfo]::InvariantCulture)
        try {
            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $null = New-VHD -Path $osDisk -ParentPath $baseDisk -Differencing -ErrorAction Stop
            $null = New-VHD -Path $scratchDisk -Dynamic -SizeBytes $scratchBytes -BlockSizeBytes 1MB -ErrorAction Stop
            Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $osDisk -ErrorAction Stop
            Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 1 -Path $scratchDisk -ErrorAction Stop
            if (-not [String]::IsNullOrWhiteSpace($artifactIso)) {
                Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 2 -Path $artifactIso -ErrorAction Stop
            }
            Set-VMProcessor -VM $vm -Count $cpuCount -Maximum $cpuMaximum -ErrorAction Stop
            Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes $memoryBytes -ErrorAction Stop
            Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -CheckpointType Disabled -ErrorAction Stop
            Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority -ErrorAction Stop
            $adapters = @(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)
            if ($adapters.Count -ne 0) { throw 'Restricted VM contains a network adapter.' }
            $drives = @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)
            if ($drives.Count -ne 2) { throw 'Restricted VM disk topology is invalid.' }
            $dvdDrives = @(Get-VMDvdDrive -VM $vm -ErrorAction Stop)
            $expectedDvdCount = if ([String]::IsNullOrWhiteSpace($artifactIso)) { 0 } else { 1 }
            if ($dvdDrives.Count -ne $expectedDvdCount) { throw 'Restricted VM artifact media topology is invalid.' }
        } catch {
            Get-VM -Name $vmName -ErrorAction SilentlyContinue | Remove-VM -Force -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $osDisk -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $scratchDisk -Force -ErrorAction SilentlyContinue
            throw
        }
        """;

    public static Task<string> RunAsync(string script, IReadOnlyDictionary<string, string>? environment = null) =>
        ExecuteAsync(script, environment, TimeSpan.FromSeconds(30));

    public static Task<string> CreateShellAsync(
        string vmName,
        string vmPath,
        int memoryMegabytes) =>
        ExecuteAsync(CreateShellScript, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSWEET_VM_NAME"] = vmName,
            ["CSWEET_VM_PATH"] = vmPath,
            ["CSWEET_MEMORY_BYTES"] = checked((long)memoryMegabytes * 1024 * 1024)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, TimeSpan.FromMinutes(1));

    public static Task<string> ConfigureAsync(
        string vmName,
        string baseDisk,
        string osDisk,
        string scratchDisk,
        int cpuCount,
        int cpuPercent,
        int memoryMegabytes,
        int scratchMegabytes,
        string? artifactImagePath)
    {
        // Hyper-V applies Maximum (0-100 percent) uniformly to each configured
        // virtual processor. Convert the aggregate quota to a per-vCPU limit.
        var cpuMaximum = Math.Clamp(
            (long)Math.Ceiling(cpuPercent / (double)cpuCount), 1, 100);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSWEET_VM_NAME"] = vmName,
            ["CSWEET_BASE_DISK"] = baseDisk,
            ["CSWEET_OS_DISK"] = osDisk,
            ["CSWEET_SCRATCH_DISK"] = scratchDisk,
            ["CSWEET_CPU_COUNT"] = cpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CSWEET_CPU_MAXIMUM"] = cpuMaximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CSWEET_MEMORY_BYTES"] = checked((long)memoryMegabytes * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CSWEET_SCRATCH_BYTES"] = checked((long)scratchMegabytes * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CSWEET_ARTIFACT_ISO"] = artifactImagePath ?? string.Empty
        };
        return ExecuteAsync(ConfigureScript, environment, TimeSpan.FromMinutes(2));
    }

    public static Task<string> StartAsync(string vmName) => ExecuteVmCommandAsync(
        "Import-Module Hyper-V -ErrorAction Stop; Start-VM -Name $env:CSWEET_VM_NAME -ErrorAction Stop | Out-Null",
        vmName, TimeSpan.FromMinutes(1));

    public static Task<string> StopAsync(string vmName) => ExecuteVmCommandAsync(
        "Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name $env:CSWEET_VM_NAME -ErrorAction SilentlyContinue; if ($null -eq $vm) { exit 44 }; if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop }",
        vmName, TimeSpan.FromMinutes(1));

    public static Task<string> DestroyAsync(string vmName) => ExecuteVmCommandAsync(
        "Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name $env:CSWEET_VM_NAME -ErrorAction SilentlyContinue; if ($null -ne $vm) { if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop }; Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop }",
        vmName, TimeSpan.FromMinutes(1));

    public static Task<string> GetStateAsync(string vmName) => ExecuteVmCommandAsync(
        "Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name $env:CSWEET_VM_NAME -ErrorAction SilentlyContinue; if ($null -eq $vm) { exit 44 }; $vm.State.ToString()",
        vmName, TimeSpan.FromSeconds(30));

    private static Task<string> ExecuteVmCommandAsync(string script, string vmName, TimeSpan timeout) =>
        ExecuteAsync(script, new Dictionary<string, string> { ["CSWEET_VM_NAME"] = vmName }, timeout);

    private static async Task<string> ExecuteAsync(
        string script,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) throw new HyperVCommandException("unsupported-host", "Hyper-V requires Windows.");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new HyperVCommandException("powershell-unavailable", "Windows PowerShell is unavailable.");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        if (environment is not null)
        {
            foreach (var item in environment) start.Environment[item.Key] = item.Value;
        }
        try
        {
            using var process = Process.Start(start) ??
                throw new HyperVCommandException("powershell-start-failed", "Windows PowerShell could not be started.");
            using var cancellation = new CancellationTokenSource(timeout);
            var outputTask = ReadBoundedAsync(process.StandardOutput, cancellation.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, cancellation.Token);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new HyperVCommandException("hyperv-timeout", "The Hyper-V operation timed out.");
            }
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode == 44) throw new HyperVCommandException("not-found", "The Hyper-V workload was not found.");
            if (process.ExitCode != 0)
                throw new HyperVCommandException("hyperv-command-failed", Sanitize(error));
            return output;
        }
        catch (Win32Exception)
        {
            throw new HyperVCommandException("powershell-start-failed", "Windows PowerShell could not be started.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (output.Length < MaximumOutputCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, MaximumOutputCharacters - output.Length)), cancellationToken);
            if (read == 0) break;
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
        ? "The Hyper-V command failed."
        : new(value.Where(character => !char.IsControl(character)).Take(256).ToArray());
}

internal sealed class HyperVCommandException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
