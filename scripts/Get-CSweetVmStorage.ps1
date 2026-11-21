# Read-only local Windows inventory. Run as Administrator to include protected VM storage.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

function Measure-VmStorage([string]$Name, [string]$Path) {
    $total = 0L
    $diskBytes = 0L
    $diskCount = 0
    $status = 'Measured'
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    try {
        $root = [IO.DirectoryInfo]::new($Path)
        for ($ancestor = $root; $ancestor; $ancestor = $ancestor.Parent) {
            if ([IO.File]::GetAttributes($ancestor.FullName) -band [IO.FileAttributes]::ReparsePoint) {
                throw [IO.IOException]::new('Linked path')
            }
        }
        $pending.Push($root)
        while ($pending.Count) {
            try {
                foreach ($entry in $pending.Pop().EnumerateFileSystemInfos()) {
                    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { $status='Partial'; continue }
                    if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry); continue }
                    $total += $entry.Length
                    if ($entry.Extension -in '.vhd','.vhdx','.avhd','.avhdx') {
                        $diskCount++
                        $diskBytes += $entry.Length
                    }
                }
            } catch [System.UnauthorizedAccessException], [IO.IOException] { $status='Partial' }
        }
    } catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { $status='Missing' }
    catch [System.UnauthorizedAccessException], [IO.IOException] { $status='Unavailable' }
    if ($status -eq 'Partial' -and $total -eq 0) { $status='Unavailable' }
    $result = [ordered]@{
        Component = $Name
        Path = $Path
        Status = $status
        FileGiB = $null
        VirtualDisks = $null
        VirtualDiskGiB = $null
    }
    if ($status -in 'Measured','Partial') {
        $result.FileGiB = [math]::Round($total / 1GB, 2)
        $result.VirtualDisks = $diskCount
        $result.VirtualDiskGiB = [math]::Round($diskBytes / 1GB, 2)
    }
    [pscustomobject]$result
}

$repo = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $env:ProgramData 'CSweet'
foreach ($name in 'Compute','ComputeBusinesses','ComputeImages') {
    Measure-VmStorage $name (Join-Path $vendor $name)
}
Measure-VmStorage 'Legacy prepared Compute images' (Join-Path $repo 'artifacts\compute\mvp\images')
Measure-VmStorage 'Legacy image builder' (Join-Path (Split-Path -Parent $repo) 'CSweet.Isolation\artifacts\linux-images')
foreach ($drive in [IO.DriveInfo]::GetDrives()) {
    if ($drive.DriveType -ne 'Fixed' -or !$drive.IsReady) { continue }
    $compute = Join-Path $drive.Name 'CSweetCompute'
    if ([IO.Directory]::Exists($compute)) { Measure-VmStorage 'Selected Compute storage' $compute }
    $program = Join-Path $drive.Name 'CSweet\Office\program'
    $data = Join-Path $drive.Name 'CSweet\Office\data'
    if ($drive.Name -eq "$env:SystemDrive\") {
        $program = Join-Path $env:ProgramFiles 'CSweet\Office'
        $data = Join-Path $vendor 'Office'
    }
    if ([IO.Directory]::Exists($program)) { Measure-VmStorage 'Office programs and recovery' $program }
    if ([IO.Directory]::Exists($data)) {
        Measure-VmStorage 'Office runtime VMs' (Join-Path $data 'hyperv')
        Measure-VmStorage 'Office setup and certification' (Join-Path $data 'setup')
    }
}
