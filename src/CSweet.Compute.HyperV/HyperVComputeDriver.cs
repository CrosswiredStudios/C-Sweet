using System.Globalization;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.HyperV;

internal sealed record HyperVMachineIdentity(Guid NodeId, Guid OrganizationId, Guid InstallationId, Guid EnvironmentId)
{
    public string Name => "csweet-compute-" + EnvironmentId.ToString("N");
    public string Ownership => $"CSweet.Compute.v1|{NodeId:D}|{OrganizationId:D}|{InstallationId:D}|{EnvironmentId:D}";
}
internal sealed record HyperVMachineObservation(Guid? Id, string State);

/// <summary>
/// Fixed privileged commands. A runtime executor must verify certification, protect paths,
/// reserve capacity and hold its journal/physical-operation lock before calling this driver.
/// Host paths are installer-owned inputs, never fields of an agent compute request.
/// </summary>
internal sealed class HyperVComputeDriver(IHyperVCommandRunner runner)
{
    public async Task<HyperVMachineObservation> CreateAsync(HyperVMachineIdentity identity, ComputeSpecification specification,
        string machinePath, string privateDiskPath, string imagePath, CancellationToken token)
    {
        Validate(identity);
        if (!specification.IsValid || specification.Architecture != "x64" || specification.OperatingSystem is not ("windows" or "linux") ||
            specification.NetworkPolicy.Mode is not (ComputeNetworkMode.None or ComputeNetworkMode.OutboundOnly) || specification.Resources.GpuCount != 0)
            throw new NotSupportedException("Hyper-V supports x64 Windows/Linux with no network or approved outbound networking, without GPU assignment.");
        foreach (var path in new[] { machinePath, privateDiskPath, imagePath })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Hyper-V paths must be protected absolute paths.");
        var parameters = Parameters(identity);
        parameters["CSWEET_COMPUTE_VM_PATH"] = machinePath;
        parameters["CSWEET_COMPUTE_DISK"] = privateDiskPath;
        parameters["CSWEET_COMPUTE_IMAGE"] = imagePath;
        parameters["CSWEET_COMPUTE_CPU"] = specification.Resources.CpuCount.ToString(CultureInfo.InvariantCulture);
        parameters["CSWEET_COMPUTE_MEMORY"] = checked(specification.Resources.MemoryMiB * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
        parameters["CSWEET_COMPUTE_DISK_BYTES"] = checked(specification.Resources.DiskMiB * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
        parameters["CSWEET_COMPUTE_FIRMWARE"] = specification.OperatingSystem == "windows" ? "MicrosoftWindows" : "MicrosoftUEFICertificateAuthority";
        parameters["CSWEET_COMPUTE_NETWORK"] = specification.NetworkPolicy.Mode.ToString();
        var created = Parse(await runner.RunAsync(CreateScript, parameters, token));
        if (created.Id is null || created.State == "Missing") throw new InvalidDataException("Hyper-V did not return the created machine identity.");
        return created;
    }

    public async Task<HyperVMachineObservation> ApplyAsync(HyperVMachineIdentity identity, Guid expectedVmId, string action, CancellationToken token, ComputeSpecification? specification = null, string? privateDiskPath = null)
    {
        Validate(identity);
        if (expectedVmId == Guid.Empty || action is not (InfrastructureActions.Start or InfrastructureActions.Stop or InfrastructureActions.Restart or InfrastructureActions.Destroy))
            throw new ArgumentException("A protected VM identity and supported action are required.");
        var parameters = Parameters(identity);
        parameters["CSWEET_COMPUTE_VM_ID"] = expectedVmId.ToString("D"); parameters["CSWEET_COMPUTE_ACTION"] = action;
        if (action is InfrastructureActions.Start or InfrastructureActions.Restart)
        {
            if (specification is not { IsValid: true } || specification.Architecture != "x64" ||
                specification.OperatingSystem is not ("windows" or "linux") || specification.NetworkPolicy.Mode is not (ComputeNetworkMode.None or ComputeNetworkMode.OutboundOnly) ||
                specification.Resources.GpuCount != 0 || privateDiskPath is null || !Path.IsPathFullyQualified(privateDiskPath))
                throw new NotSupportedException("Activation requires the approved isolated VM topology.");
            parameters["CSWEET_COMPUTE_DISK"] = privateDiskPath;
            parameters["CSWEET_COMPUTE_CPU"] = specification.Resources.CpuCount.ToString(CultureInfo.InvariantCulture);
            parameters["CSWEET_COMPUTE_MEMORY"] = checked(specification.Resources.MemoryMiB * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
            parameters["CSWEET_COMPUTE_DISK_BYTES"] = checked(specification.Resources.DiskMiB * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
            parameters["CSWEET_COMPUTE_FIRMWARE"] = specification.OperatingSystem == "windows" ? "MicrosoftWindows" : "MicrosoftUEFICertificateAuthority";
            parameters["CSWEET_COMPUTE_NETWORK"] = specification.NetworkPolicy.Mode.ToString();
        }
        var observed = Parse(await runner.RunAsync(ControlScript, parameters, token));
        if (observed.Id != expectedVmId) throw new InvalidDataException("Hyper-V returned a different machine identity.");
        return observed;
    }

    public async Task<HyperVMachineObservation> ObserveAsync(HyperVMachineIdentity identity, Guid expectedVmId, CancellationToken token)
    {
        Validate(identity);
        if (expectedVmId == Guid.Empty) throw new ArgumentException("An exact VM identity is required.");
        var parameters = Parameters(identity); parameters["CSWEET_COMPUTE_VM_ID"] = expectedVmId.ToString("D");
        var observed = Parse(await runner.RunAsync(ObserveScript, parameters, token));
        if (observed.Id != expectedVmId) throw new InvalidDataException("Hyper-V returned a different machine identity.");
        return observed;
    }

    public async Task<HyperVMachineObservation> DiscoverAsync(HyperVMachineIdentity identity, CancellationToken token)
    {
        Validate(identity);
        return Parse(await runner.RunAsync(DiscoverScript, Parameters(identity), token));
    }

    internal async Task ProtectRemovedWorkloadAsync(HyperVMachineIdentity identity, Guid expectedVmId, string workloadPath, CancellationToken token)
    {
        Validate(identity);
        if (expectedVmId == Guid.Empty || !Path.IsPathFullyQualified(workloadPath) ||
            Path.GetFileName(workloadPath) != identity.EnvironmentId.ToString("N"))
            throw new ArgumentException("Use the exact protected environment directory and removed VM identity.");
        var parameters = Parameters(identity);
        parameters["CSWEET_COMPUTE_VM_ID"] = expectedVmId.ToString("D");
        parameters["CSWEET_COMPUTE_WORKLOAD_PATH"] = workloadPath;
        await runner.RunAsync(ProtectRemovedWorkloadScript, parameters, token);
    }

    internal const string ProtectRemovedWorkloadScript = """
        Import-Module Hyper-V -ErrorAction Stop
        $expected = [Guid]::Parse($env:CSWEET_COMPUTE_VM_ID)
        if (@(Get-VM -ErrorAction Stop | Where-Object { $_.Id -eq $expected }).Count -ne 0) { throw 'Preserve existing VM before removing its files.' }
        $path = $env:CSWEET_COMPUTE_WORKLOAD_PATH
        if (!(Test-Path -LiteralPath $path)) { exit 0 }
        $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $entries = [Collections.Generic.List[IO.FileSystemInfo]]::new()
        $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
        $pending.Push((Get-Item -LiteralPath $path -Force -ErrorAction Stop))
        while ($pending.Count -gt 0) {
            $entry = $pending.Pop()
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Preserve redirected workload path.' }
            if ($entry -is [IO.FileInfo] -and (Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop).LinkType -eq 'HardLink') { throw 'Preserve linked workload file.' }
            $owner = (Get-Acl -LiteralPath $entry.FullName -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier])
            if (!$owner.Equals($admins) -and !$owner.Equals($system)) { throw 'Preserve untrusted workload ownership.' }
            $entries.Add($entry)
            if ($entry -is [IO.DirectoryInfo]) { foreach ($child in @(Get-ChildItem -LiteralPath $entry.FullName -Force -ErrorAction Stop)) { $pending.Push($child) } }
        }
        # Partial New-VM failures can leave Hyper-V's automatic broad VM-group ACLs.
        # A fresh authorized destroy may revoke those after exact VM absence, before
        # the executor applies its unchanged link/ownership guards and removes files.
        foreach ($entry in $entries) {
            $acl = if ($entry -is [IO.DirectoryInfo]) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
            $acl.SetAccessRuleProtection($true, $false)
            $acl.SetOwner($admins)
            $inherit = if ($entry -is [IO.DirectoryInfo]) { 'ContainerInherit,ObjectInherit' } else { 'None' }
            foreach ($sid in @($admins, $system)) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow')) }
            Set-Acl -LiteralPath $entry.FullName -AclObject $acl -ErrorAction Stop
        }
        """;

    internal const string DiscoverScript = """
        Import-Module Hyper-V -ErrorAction Stop
        $matches = @(Get-VM -ErrorAction Stop | Where-Object { $_.Name -ceq $env:CSWEET_COMPUTE_VM_NAME })
        if ($matches.Count -eq 0) { @{ id = $null; state = 'Missing' } | ConvertTo-Json -Compress; exit 0 }
        if ($matches.Count -ne 1 -or $matches[0].Notes -cne $env:CSWEET_COMPUTE_OWNER) { throw 'Ambiguous or conflicting compute ownership requires reconciliation.' }
        $vm = $matches[0]
        @{ id = $vm.Id.Guid; state = $vm.State.ToString() } | ConvertTo-Json -Compress
        """;

    private static void Validate(HyperVMachineIdentity identity)
    {
        if (identity.NodeId == Guid.Empty || identity.OrganizationId == Guid.Empty || identity.InstallationId == Guid.Empty || identity.EnvironmentId == Guid.Empty)
            throw new ArgumentException("Complete protected workload ownership is required.");
    }
    private static Dictionary<string, string> Parameters(HyperVMachineIdentity identity) => new()
    {
        ["CSWEET_COMPUTE_VM_NAME"] = identity.Name, ["CSWEET_COMPUTE_OWNER"] = identity.Ownership
    };
    private static HyperVMachineObservation Parse(string json)
    {
        var observation = JsonSerializer.Deserialize<HyperVMachineObservation>(json, ComputeProtocol.Json)
            ?? throw new InvalidDataException("Hyper-V returned no machine observation.");
        if (observation.State is not ("Running" or "Off" or "Starting" or "Stopping" or "Saved" or "Paused" or "Missing") ||
            observation.State != "Missing" && observation.Id is null || observation.Id == Guid.Empty)
            throw new InvalidDataException("Hyper-V returned an invalid machine observation.");
        return observation;
    }

    private const string NetworkFunctions = """
        function Assert-ComputeNetwork($vm) {
            $cards = @(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)
            if ($env:CSWEET_COMPUTE_NETWORK -ceq 'None') {
                if ($cards.Count -ne 0) { throw 'Unexpected compute network adapter.' }
            } elseif ($env:CSWEET_COMPUTE_NETWORK -ceq 'OutboundOnly') {
                $switch = Get-VMSwitch -Name 'Default Switch' -ErrorAction Stop
                if ($switch.SwitchType.ToString() -ne 'Internal' -or $cards.Count -ne 1 -or
                    $cards[0].Name -cne 'CSweet Outbound' -or $cards[0].SwitchId -ne $switch.Id -or
                    $cards[0].MacAddressSpoofing.ToString() -ne 'Off') { throw 'Unexpected compute outbound topology.' }
            } else { throw 'Unsupported compute network policy.' }
        }
        function Add-ComputeOutboundNetwork($vm) {
            if ($env:CSWEET_COMPUTE_NETWORK -ceq 'None') { return }
            if ($env:CSWEET_COMPUTE_NETWORK -cne 'OutboundOnly') { throw 'Unsupported compute network policy.' }
            $switch = Get-VMSwitch -Name 'Default Switch' -ErrorAction Stop
            if ($switch.SwitchType.ToString() -ne 'Internal') { throw 'The development NAT switch is unavailable.' }
            $gateway = @(Get-NetIPAddress -InterfaceAlias 'vEthernet (Default Switch)' -AddressFamily IPv4 -ErrorAction Stop |
                Where-Object { $_.AddressState.ToString() -eq 'Preferred' })
            if ($gateway.Count -ne 1) { throw 'The development NAT resolver is unavailable.' }
            Add-VMNetworkAdapter -VM $vm -Name 'CSweet Outbound' -SwitchName $switch.Name -ErrorAction Stop
            $card = Get-VMNetworkAdapter -VM $vm -Name 'CSweet Outbound' -ErrorAction Stop
            Set-VMNetworkAdapter -VMNetworkAdapter $card -MacAddressSpoofing Off -DhcpGuard On -RouterGuard On -ErrorAction Stop
            # NAT provides ordinary package-manager networking. Only replies to outbound
            # connections enter the VM; host/private destinations remain inaccessible.
            Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Deny -Direction Inbound -Weight 1 -ErrorAction Stop
            Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Deny -Direction Outbound -Weight 2 -ErrorAction Stop
            $weight = 100
            foreach ($protocol in @('TCP','UDP')) {
                Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Allow -Direction Outbound -Protocol $protocol -RemoteIPAddress '0.0.0.0/0' -Weight $weight -Stateful $true -ErrorAction Stop
                $weight++
            }
            $weight = 200
            foreach ($range in @('0.0.0.0/8','10.0.0.0/8','100.64.0.0/10','127.0.0.0/8','169.254.0.0/16','172.16.0.0/12','192.168.0.0/16','224.0.0.0/4','240.0.0.0/4')) {
                Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Deny -Direction Outbound -RemoteIPAddress $range -Weight $weight -ErrorAction Stop
                $weight++
            }
            $weight = 300
            foreach ($protocol in @('TCP','UDP')) {
                Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Allow -Direction Outbound -Protocol $protocol -RemoteIPAddress $gateway[0].IPAddress -RemotePort 53 -Weight $weight -Stateful $true -ErrorAction Stop
                $weight++
            }
            Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Allow -Direction Outbound -Protocol UDP -LocalPort 68 -RemotePort 67 -Weight 302 -ErrorAction Stop
            Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $card -Action Allow -Direction Inbound -Protocol UDP -LocalPort 68 -RemotePort 67 -RemoteIPAddress $gateway[0].IPAddress -Weight 303 -ErrorAction Stop
        }
        """;

    internal const string CreateScript = NetworkFunctions + "\n" + """
        Import-Module Hyper-V -ErrorAction Stop
        if (@(Get-VM -ErrorAction Stop | Where-Object { $_.Name -eq $env:CSWEET_COMPUTE_VM_NAME }).Count -ne 0) { throw 'Existing VM requires reconciliation.' }
        $image = Get-VHD -Path $env:CSWEET_COMPUTE_IMAGE -ErrorAction Stop
        $size = [Int64]::Parse($env:CSWEET_COMPUTE_DISK_BYTES, [Globalization.CultureInfo]::InvariantCulture)
        if ($image.VhdFormat.ToString() -ne 'VHDX' -or $image.VhdType.ToString() -notin @('Dynamic','Fixed') -or
            -not [String]::IsNullOrWhiteSpace($image.ParentPath) -or $image.Size -gt $size) { throw 'Image topology or requested disk capacity is invalid.' }
        if (Test-Path -LiteralPath $env:CSWEET_COMPUTE_DISK) { throw 'Existing disk requires reconciliation.' }
        [IO.File]::Copy($env:CSWEET_COMPUTE_IMAGE, $env:CSWEET_COMPUTE_DISK, $false)
        if ($image.Size -lt $size) { Resize-VHD -Path $env:CSWEET_COMPUTE_DISK -SizeBytes $size -ErrorAction Stop }
        $memory = [Int64]::Parse($env:CSWEET_COMPUTE_MEMORY, [Globalization.CultureInfo]::InvariantCulture)
        $cpu = [Int32]::Parse($env:CSWEET_COMPUTE_CPU, [Globalization.CultureInfo]::InvariantCulture)
        $vm = New-VM -Name $env:CSWEET_COMPUTE_VM_NAME -Generation 2 -NoVHD -MemoryStartupBytes $memory -Path $env:CSWEET_COMPUTE_VM_PATH -ErrorAction Stop
        Set-VM -VM $vm -Notes $env:CSWEET_COMPUTE_OWNER -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -CheckpointType Disabled -ErrorAction Stop
        Get-VMNetworkAdapter -VM $vm -ErrorAction Stop | Remove-VMNetworkAdapter -Confirm:$false -ErrorAction Stop
        Add-ComputeOutboundNetwork $vm
        Get-VMDvdDrive -VM $vm -ErrorAction Stop | Remove-VMDvdDrive -Confirm:$false -ErrorAction Stop
        Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $env:CSWEET_COMPUTE_DISK -ErrorAction Stop
        Set-VMProcessor -VM $vm -Count $cpu -Maximum 100 -ErrorAction Stop
        Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes $memory -ErrorAction Stop
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate $env:CSWEET_COMPUTE_FIRMWARE -ErrorAction Stop
        $disk = @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)
        Assert-ComputeNetwork $vm
        if ($disk.Count -ne 1 -or $disk[0].Path -ne $env:CSWEET_COMPUTE_DISK) { throw 'Compute VM topology is invalid.' }
        Set-VMFirmware -VM $vm -FirstBootDevice $disk[0] -ErrorAction Stop
        # Hyper-V adds broad VM-group/capability ACLs to the workload directory.
        # Replace them with SYSTEM, Administrators and this VM's worker identity.
        $workload = [IO.Path]::GetDirectoryName($env:CSWEET_COMPUTE_DISK)
        $worker = ([Security.Principal.NTAccount]::new('NT VIRTUAL MACHINE', $vm.Id.ToString())).Translate([Security.Principal.SecurityIdentifier])
        $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $entries = @((Get-Item -LiteralPath $workload)) + @(Get-ChildItem -LiteralPath $workload -Recurse -Force)
        foreach ($entry in $entries) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Redirected workload path.' }
        }
        foreach ($entry in $entries) {
            $acl = if ($entry.PSIsContainer) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
            $acl.SetAccessRuleProtection($true, $false)
            $acl.SetOwner($admins)
            $inherit = if ($entry.PSIsContainer) { 'ContainerInherit,ObjectInherit' } else { 'None' }
            foreach ($sid in @($admins, $system)) {
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
            }
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($worker, 'Modify', $inherit, 'None', 'Allow'))
            Set-Acl -LiteralPath $entry.FullName -AclObject $acl
        }
        @{ id = $vm.Id.Guid; state = $vm.State.ToString() } | ConvertTo-Json -Compress
        """;

    private const string OwnedVm = """
        Import-Module Hyper-V -ErrorAction Stop
        $expected = [Guid]::Parse($env:CSWEET_COMPUTE_VM_ID)
        $vm = Get-VM -ErrorAction Stop | Where-Object { $_.Id -eq $expected }
        if ($null -eq $vm) { @{ id = $env:CSWEET_COMPUTE_VM_ID; state = 'Missing' } | ConvertTo-Json -Compress; exit 0 }
        if ($vm.Name -cne $env:CSWEET_COMPUTE_VM_NAME -or $vm.Notes -cne $env:CSWEET_COMPUTE_OWNER) { throw 'The VM ownership does not match protected compute history.' }
        """;
    internal const string ObserveScript = OwnedVm + "\n@{ id = $vm.Id.Guid; state = $vm.State.ToString() } | ConvertTo-Json -Compress";
    internal const string ControlScript = NetworkFunctions + "\n" + OwnedVm + "\n" + """
        if ($env:CSWEET_COMPUTE_ACTION -in @('compute.start.v1', 'compute.restart.v1')) {
            Assert-ComputeNetwork $vm
            $disks = @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)
            $memory = Get-VMMemory -VM $vm -ErrorAction Stop
            $cpu = Get-VMProcessor -VM $vm -ErrorAction Stop
            $firmware = Get-VMFirmware -VM $vm -ErrorAction Stop
            if ($vm.Generation -ne 2 -or $disks.Count -ne 1 -or $disks[0].Path -ne $env:CSWEET_COMPUTE_DISK -or
                @($vm | Get-VMDvdDrive -ErrorAction Stop).Count -ne 0 -or
                $memory.DynamicMemoryEnabled -or $memory.Startup -ne [Int64]::Parse($env:CSWEET_COMPUTE_MEMORY) -or
                $cpu.Count -ne [Int32]::Parse($env:CSWEET_COMPUTE_CPU) -or @(Get-VMSnapshot -VM $vm -ErrorAction Stop).Count -ne 0 -or
                $firmware.SecureBoot.ToString() -ne 'On' -or $firmware.SecureBootTemplate -ne $env:CSWEET_COMPUTE_FIRMWARE -or
                @(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop).Count -ne 0 -or @(Get-VMAssignableDevice -VM $vm -ErrorAction Stop).Count -ne 0) {
                throw 'The compute VM topology has changed; activation is denied.'
            }
            $vhd = Get-VHD -Path $env:CSWEET_COMPUTE_DISK -ErrorAction Stop
            if ($vhd.VhdFormat.ToString() -ne 'VHDX' -or $vhd.VhdType.ToString() -notin @('Dynamic','Fixed') -or
                -not [String]::IsNullOrWhiteSpace($vhd.ParentPath) -or $vhd.Size -ne [Int64]::Parse($env:CSWEET_COMPUTE_DISK_BYTES)) {
                throw 'The private compute disk topology or capacity has changed.'
            }
        }
        switch ($env:CSWEET_COMPUTE_ACTION) {
            'compute.start.v1' { if ($vm.State -ne 'Running') { Start-VM -VM $vm -ErrorAction Stop | Out-Null } }
            'compute.stop.v1' { if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop } }
            'compute.restart.v1' { if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop }; Start-VM -VM $vm -ErrorAction Stop | Out-Null }
            'compute.destroy.v1' {
                if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop }
                Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop
                @{ id = $env:CSWEET_COMPUTE_VM_ID; state = 'Missing' } | ConvertTo-Json -Compress; exit 0
            }
            default { throw 'Unsupported compute control.' }
        }
        $current = Get-VM -Id $vm.Id -ErrorAction Stop
        @{ id = $current.Id.Guid; state = $current.State.ToString() } | ConvertTo-Json -Compress
        """;
}
