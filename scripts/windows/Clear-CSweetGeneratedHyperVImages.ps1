[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $CleanupLogPath,
    [switch] $NoElevation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ([String]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
} else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}
if ([String]::IsNullOrWhiteSpace($CleanupLogPath)) {
    $CleanupLogPath = Join-Path $RepositoryRoot 'artifacts\windows-image-cleanup.log'
}
$CleanupLogPath = [IO.Path]::GetFullPath($CleanupLogPath)
trap {
    $failure = ($_ | Out-String)
    $vmInventory = if (Test-Administrator) {
        @(Get-VM -ErrorAction SilentlyContinue | ForEach-Object {
            $vm = $_
            [pscustomobject]@{
                name = $vm.Name
                id = $vm.Id
                state = $vm.State.ToString()
                path = $vm.Path
                disks = @(Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue |
                    Select-Object -ExpandProperty Path)
            }
        })
    } else { @() }
    $vhdInventory = if (Test-Administrator -and -not [String]::IsNullOrWhiteSpace($RepositoryRoot)) {
        @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'artifacts') -Recurse -File -Filter '*.vhdx' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $vhd = Get-VHD -Path $_.FullName -ErrorAction SilentlyContinue
                if ($null -ne $vhd) {
                    [pscustomobject]@{
                        path = $_.FullName
                        attached = $vhd.Attached
                        diskNumber = $vhd.DiskNumber
                        parentPath = $vhd.ParentPath
                    }
                }
            })
    } else { @() }
    $detail = [pscustomobject]@{
        failure = $failure
        virtualMachines = $vmInventory
        virtualDisks = $vhdInventory
        vmWorkers = if (Test-Administrator) {
            @(Get-CimInstance Win32_Process -Filter "Name = 'vmwp.exe'" -ErrorAction SilentlyContinue |
                Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine)
        } else { @() }
    } | ConvertTo-Json -Depth 6
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($CleanupLogPath)) | Out-Null
    [IO.File]::WriteAllText($CleanupLogPath, $detail)
    exit 1
}

if (-not (Test-Administrator)) {
    if ($NoElevation) { throw 'Administrator access is required to clean generated Hyper-V images.' }
    $hostExecutable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @(
        '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        ('"' + $PSCommandPath + '"'), '-RepositoryRoot', ('"' + $RepositoryRoot + '"'),
        '-CleanupLogPath', ('"' + $CleanupLogPath + '"'), '-NoElevation')
    $process = Start-Process -FilePath $hostExecutable -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $CleanupLogPath -PathType Leaf) {
            Get-Content -LiteralPath $CleanupLogPath -Raw
        } else { 'No elevated diagnostic was returned.' }
        throw "The elevated image cleanup exited with code $($process.ExitCode). $detail"
    }
    return
}

if ($null -eq ('CSweet.Windows.FileLockInspector' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CSweet.Windows
{
    public static class FileLockInspector
    {
        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RmAppType
        {
            UnknownApp = 0, MainWindow = 1, OtherWindow = 2, Service = 3,
            Explorer = 4, Console = 5, Critical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public RmAppType ApplicationType;
            public uint AppStatus;
            public uint TerminalSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint sessionHandle, uint fileCount,
            string[] fileNames, uint applicationCount, IntPtr applications,
            uint serviceCount, IntPtr serviceNames);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded,
            ref uint processInfoCount, [In, Out] RmProcessInfo[] affectedApplications,
            ref uint rebootReasons);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint sessionHandle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingPath, string newPath, int flags);

        public static int[] GetLockingProcessIds(string path)
        {
            uint session;
            var key = Guid.NewGuid().ToString("N");
            var result = RmStartSession(out session, 0, key);
            if (result != 0) throw new InvalidOperationException("Restart Manager session failed: " + result);
            try
            {
                result = RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero);
                if (result != 0) throw new InvalidOperationException("Restart Manager registration failed: " + result);
                uint needed = 0, count = 0, reasons = 0;
                result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == 0) return Array.Empty<int>();
                if (result != ErrorMoreData) throw new InvalidOperationException("Restart Manager query failed: " + result);
                var processes = new RmProcessInfo[needed];
                count = needed;
                result = RmGetList(session, out needed, ref count, processes, ref reasons);
                if (result != 0) throw new InvalidOperationException("Restart Manager process query failed: " + result);
                var ids = new List<int>((int)count);
                for (var index = 0; index < count; index++) ids.Add(processes[index].Process.ProcessId);
                return ids.ToArray();
            }
            finally { RmEndSession(session); }
        }

        public static void ScheduleDeleteOnReboot(string path)
        {
            const int MoveFileDelayUntilReboot = 4;
            if (!MoveFileEx(path, null, MoveFileDelayUntilReboot))
                throw new InvalidOperationException(
                    "Could not schedule stale image deletion. Win32 error: " + Marshal.GetLastWin32Error());
        }
    }
}
'@
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts'))
$staleRoots = @(
    [IO.Path]::GetFullPath((Join-Path $artifactRoot 'windows-test')),
    [IO.Path]::GetFullPath((Join-Path $artifactRoot 'windows-runtime\source'))
)
$sourceRoot = $staleRoots[1]
$currentReady = Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.vhdx.ready' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $currentReady) {
    throw 'The current ready-marked development guest image could not be identified; cleanup was not started.'
}
$currentImagePrefix = $currentReady.Name.Substring(0, $currentReady.Name.Length - '.ready'.Length)

function Test-StalePath([string] $Path) {
    if ([String]::IsNullOrWhiteSpace($Path)) { return $false }
    $fullPath = [IO.Path]::GetFullPath($Path)
    return @($staleRoots | Where-Object {
        $fullPath.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
}

$removedVms = [Collections.Generic.List[string]]::new()
$registeredVms = @(Get-VM)
foreach ($vm in $registeredVms) {
    $diskPaths = @(Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue |
        Where-Object { -not [String]::IsNullOrWhiteSpace($_.Path) } |
        Select-Object -ExpandProperty Path)
    $generatedDisks = @($diskPaths | Where-Object { Test-StalePath $_ })
    $allDisksGenerated = $diskPaths.Count -gt 0 -and $generatedDisks.Count -eq $diskPaths.Count
    $generatedConfiguration = Test-StalePath $vm.Path
    if (-not ($allDisksGenerated -or ($generatedConfiguration -and $diskPaths.Count -eq 0))) { continue }
    if ($vm.State -ne [Microsoft.HyperV.PowerShell.VMState]::Off) {
        Stop-VM -VM $vm -TurnOff -Force
    }
    Remove-VM -VM $vm -Force
    $removedVms.Add($vm.Name)
}

$orphanedAttachedImages = @(foreach ($root in $staleRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.vhdx' -Force |
        Where-Object {
            -not ($_.FullName.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase) -and
                $_.Name.StartsWith($currentImagePrefix, [StringComparison]::OrdinalIgnoreCase))
        } |
        ForEach-Object {
            $vhd = Get-VHD -Path $_.FullName -ErrorAction SilentlyContinue
            if ($null -ne $vhd -and $vhd.Attached) { $_.FullName }
        }
})
if ($registeredVms.Count -eq 0 -and $orphanedAttachedImages.Count -gt 0) {
    $runtimeService = Get-Service -Name 'CSweet.RuntimeHost' -ErrorAction SilentlyContinue
    $restartRuntime = $null -ne $runtimeService -and $runtimeService.Status -eq 'Running'
    try {
        if ($restartRuntime) { Stop-Service -Name 'CSweet.RuntimeHost' -Force }
        Restart-Service -Name 'vmms' -Force
    }
    finally {
        if ($restartRuntime) { Start-Service -Name 'CSweet.RuntimeHost' }
    }
}

$removedImages = 0
$scheduledImages = [Collections.Generic.List[string]]::new()
$imageRecords = @()
foreach ($root in $staleRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    $images = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force |
        Where-Object {
            $_.Extension -in @('.vhd', '.vhdx') -and
            -not ($root.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -and
                $_.Name.StartsWith($currentImagePrefix, [StringComparison]::OrdinalIgnoreCase))
        })
    foreach ($image in $images) {
        if (-not (Test-StalePath $image.FullName)) { throw "Unsafe image cleanup target: $($image.FullName)" }
        $virtualDisk = Get-VHD -Path $image.FullName -ErrorAction SilentlyContinue
        $imageRecords += [pscustomobject]@{ File = $image; VirtualDisk = $virtualDisk }
    }
}
$remainingImages = @($imageRecords)
while ($remainingImages.Count -gt 0) {
    $parentPaths = @($remainingImages |
        Where-Object { $null -ne $_.VirtualDisk -and -not [String]::IsNullOrWhiteSpace($_.VirtualDisk.ParentPath) } |
        ForEach-Object { [IO.Path]::GetFullPath($_.VirtualDisk.ParentPath) })
    $leaves = @($remainingImages | Where-Object { $parentPaths -notcontains $_.File.FullName })
    if ($leaves.Count -eq 0) { throw 'The generated VHD dependency graph contains a cycle.' }
    foreach ($record in $leaves) {
        if ($null -ne $record.VirtualDisk -and $null -ne $record.VirtualDisk.DiskNumber) {
            Dismount-VHD -Path $record.File.FullName
        }
        try {
            Remove-Item -LiteralPath $record.File.FullName -Force
        }
        catch [IO.IOException] {
            $owners = @([CSweet.Windows.FileLockInspector]::GetLockingProcessIds($record.File.FullName))
            if ($owners.Count -eq 0 -or @($owners | Where-Object { $_ -ne 4 }).Count -gt 0) { throw }
            [CSweet.Windows.FileLockInspector]::ScheduleDeleteOnReboot($record.File.FullName)
            $scheduledImages.Add($record.File.FullName)
        }
        $removedImages++
    }
    $leafPaths = @($leaves | ForEach-Object { $_.File.FullName })
    $remainingImages = @($remainingImages | Where-Object { $leafPaths -notcontains $_.File.FullName })
}

$windowsTest = $staleRoots[0]
if (Test-Path -LiteralPath $windowsTest -PathType Container) {
    Get-ChildItem -LiteralPath $windowsTest -Force |
        Where-Object Name -ne 'cache' |
        ForEach-Object {
            $testItemPath = $_.FullName
            if (-not (Test-StalePath $testItemPath)) { throw "Unsafe test cleanup target: $testItemPath" }
            try { Remove-Item -LiteralPath $testItemPath -Recurse -Force }
            catch [IO.IOException] {
                $remaining = @(Get-ChildItem -LiteralPath $testItemPath -Recurse -File -Force -ErrorAction SilentlyContinue |
                    Where-Object { $scheduledImages -notcontains $_.FullName })
                if ($remaining.Count -gt 0) { throw }
            }
        }
}

if (Test-Path -LiteralPath $sourceRoot -PathType Container) {
    Get-ChildItem -LiteralPath $sourceRoot -Force |
        Where-Object { -not $_.Name.StartsWith($currentImagePrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
        if (-not (Test-StalePath $_.FullName)) { throw "Unsafe source-image cleanup target: $($_.FullName)" }
        if ($scheduledImages -notcontains $_.FullName) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
}

$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($RepositoryRoot))
[pscustomobject]@{
    removedVms = @($removedVms)
    removedImageFiles = $removedImages
    scheduledImageFiles = @($scheduledImages)
    restartRequired = $scheduledImages.Count -gt 0
    preservedCurrentSourceImage = $currentImagePrefix
    freeSpaceGb = [Math]::Round($drive.AvailableFreeSpace / 1GB, 2)
    installedRuntimePreserved = (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'CSweet\RuntimeHost'))
} | ConvertTo-Json -Compress
