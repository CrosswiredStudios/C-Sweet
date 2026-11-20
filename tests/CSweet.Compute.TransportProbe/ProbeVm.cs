internal static class ProbeVm
{
    internal const string Create = """
        Import-Module Hyper-V
        $name = $env:CSWEET_PROBE_NAME
        if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw 'Test VM name is already in use.' }
        if ((Get-VHD -Path $env:CSWEET_PROBE_IMAGE).Size -gt 64GB) { throw 'The image exceeds the test ceiling.' }
        New-VHD -Differencing -ParentPath $env:CSWEET_PROBE_IMAGE -Path $env:CSWEET_PROBE_DISK | Out-Null
        $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 2GB -VHDPath $env:CSWEET_PROBE_DISK
        try {
            Set-VM -VM $vm -Notes $name -CheckpointType Disabled -AutomaticStartAction Nothing -AutomaticStopAction TurnOff
            Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter -Confirm:$false
            if (@(Get-VMNetworkAdapter -VM $vm).Count -ne 0) { throw 'Unexpected network adapter.' }
            Set-VMProcessor -VM $vm -Count 1
            Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority
            Start-VM -VM $vm | Out-Null
            $vm.Id.ToString('D')
        } catch {
            if ((Get-VM -Id $vm.Id).State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
            Remove-VM -VM $vm -Force
            throw
        }
        """;

    internal const string Remove = """
        Import-Module Hyper-V
        $name = $env:CSWEET_PROBE_NAME
        $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
        if ($vm) {
            $disks = @(Get-VMHardDiskDrive -VM $vm)
            if ($vm.Notes -ne $name -or $disks.Count -ne 1 -or $disks[0].Path -ne $env:CSWEET_PROBE_DISK) {
                throw 'The VM ownership evidence no longer matches this test.'
            }
            if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
            Remove-VM -VM $vm -Force
        }
        """;
}
