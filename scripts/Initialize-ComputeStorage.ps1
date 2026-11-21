# The handoff selects a volume identity. Only this elevated installer derives host paths.
function Get-ComputeStorageRegistration([string] $VolumeId) {
    if ($VolumeId -notmatch '^\\\\\?\\Volume\{([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\}\\$') {
        throw 'Unexpected Windows Compute volume identifier.'
    }
    Join-Path 'HKLM:\SOFTWARE\CSweet\Compute\StorageVolumes' $Matches[1]
}

function Resolve-ComputeStorageRoot([string] $VolumeId, [switch] $Create) {
    if (-not $VolumeId) { return }
    $volumes = @(Get-Volume -ErrorAction Stop | Where-Object { $_.DriveLetter -and $_.UniqueId -eq $VolumeId })
    if ($volumes.Count -ne 1) { throw 'The selected Compute volume is unavailable.' }
    $volume = $volumes[0]
    $disks = @(Get-Partition -DriveLetter $volume.DriveLetter -ErrorAction Stop | ForEach-Object {
        Get-Disk -Number $_.DiskNumber -ErrorAction Stop
    })
    if ($volume.DriveType -ne 'Fixed' -or $volume.FileSystemType -notin @('NTFS','ReFS') -or
        $volume.HealthStatus -ne 'Healthy' -or !$disks.Count -or @($disks | Where-Object {
            $_.IsOffline -or $_.IsReadOnly -or $_.BusType -in @('USB','SD','MMC','1394','iSCSI')
        }).Count) { throw 'The selected Compute volume must be healthy, fixed and writable.' }
    $mount = "$($volume.DriveLetter):\"
    $root = Join-Path $mount 'CSweetCompute'
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $trusted = @($admins.Value,$system.Value,'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    foreach ($path in @($mount,$root)) {
        if ($path -eq $root -and !(Test-Path -LiteralPath $root)) {
            if (!$Create) { continue }
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $acl.SetOwner($admins)
            $acl.SetAccessRuleProtection($true,$false)
            foreach ($sid in @($admins,$system)) {
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
            }
            # Allow the Headquarters process to measure build files by listing directories.
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ListDirectory,Traverse,ReadAttributes','ContainerInherit','None','Allow'))
            [IO.DirectoryInfo]::new($root).Create($acl)
        }
        $entry = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        $security = Get-Acl -LiteralPath $path -ErrorAction Stop
        if (!$entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $security.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Untrusted Compute storage directory.' }
        $dangerous = [Security.AccessControl.FileSystemRights]'Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
        if ($path -eq $root) { $dangerous = $dangerous -bor [Security.AccessControl.FileSystemRights]::Write }
        foreach ($rule in $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
                ($rule.FileSystemRights -band $dangerous) -and
                ($path -eq $root -or !($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly))) {
                throw 'An unprivileged user can modify the Compute storage directory.'
            }
        }
    }
    return $root
}

function Initialize-ComputeStorage([string] $VolumeId) {
    if (!$VolumeId) { return }
    $root = Resolve-ComputeStorageRoot $VolumeId -Create
    $key = Get-ComputeStorageRegistration $VolumeId
    if (!(Test-Path -LiteralPath $key)) {
        # A newly selected volume must not claim a pre-existing directory full of other data.
        if (@(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop | Select-Object -First 1).Count -and
            !(Test-Path -LiteralPath $key)) { throw 'The unregistered Compute storage directory must be empty.' }
        New-Item -Path $key -Force -ErrorAction Stop | Out-Null
    }
    return $root
}
