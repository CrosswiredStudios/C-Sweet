[CmdletBinding()]
param([switch] $EnrollOffice)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vendor = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CSweet'
$root = Join-Path $vendor 'Provisioning'
$serviceNames = @('CSweet.Api', 'CSweet.AgentHost', 'CSweet.WorkerHost', 'CSweet.ExecutionGateway',
    'CSweet.GitHost', 'CSweet.SourceControlProvisionerHost', 'CSweet.Migrator')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$trustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'

function Assert-ProtectedPath([string] $Path) {
    $entry = Get-Item -LiteralPath $Path
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected provisioning path: $Path" }
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner.Value -notin @($system.Value, $admins.Value, $trustedInstaller)) { throw "Provisioning requires an administrator-owned path: $Path" }
    $modify = [Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $modify) -and
            $rule.IdentityReference.Value -notin @($system.Value, $admins.Value, $trustedInstaller)) {
            throw "Provisioning requires an administrator-controlled path: $Path"
        }
    }
}

function Set-PrivateAcl([string] $Path, [object[]] $Readers, [object] $Writer) {
    $directory = (Get-Item -LiteralPath $Path) -is [IO.DirectoryInfo]
    $security = if ($directory) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
    $inherit = if ($directory) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($admins)
    foreach ($identity in @($system, $admins)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', $inherit, 'None', 'Allow'))
    }
    foreach ($reader in $Readers) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($reader, 'ReadAndExecute', 'None', 'None', 'Allow'))
    }
    if ($Writer) {
        if ($directory) { throw 'Migration output access applies only to a preallocated file.' }
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Writer, 'WriteData, ReadAttributes, Synchronize', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

Assert-ProtectedPath $vendor
Assert-ProtectedPath $root
if ($EnrollOffice -and (Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue)) {
    throw 'An Office is already installed. Preserve its enrollment through the installed Office maintenance flow.'
}
$sids = @{}
foreach ($name in $serviceNames) {
    $file = Join-Path $root "$name.json"
    Assert-ProtectedPath $file
    $null = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $sids[$name] = [Security.Principal.NTAccount]::new('NT SERVICE', $name).Translate([Security.Principal.SecurityIdentifier])
}
Set-PrivateAcl $root @($sids.Values) $null
foreach ($name in $serviceNames) { Set-PrivateAcl (Join-Path $root "$name.json") @($sids[$name]) $null }

# Rotate only this setup invocation's private migration workspace. All service
# executables and their configuration remain read-only to the migration account.
$migration = Join-Path $root 'Migration'
if (-not (Test-Path -LiteralPath $migration)) { New-Item -ItemType Directory -Path $migration | Out-Null }
Assert-ProtectedPath $migration
Set-PrivateAcl $migration @($sids['CSweet.Migrator']) $null
foreach ($name in @('completed', 'enroll-office', 'enrollment.secret')) {
    $path = Join-Path $migration $name
    if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
}
if ($EnrollOffice) {
    Assert-ProtectedPath (Join-Path $root 'office.json')
    [IO.File]::WriteAllText((Join-Path $migration 'enroll-office'), '1')
    Set-PrivateAcl (Join-Path $migration 'enroll-office') @($sids['CSweet.Migrator']) $null
    [IO.File]::WriteAllText((Join-Path $migration 'enrollment.secret'), '')
    Set-PrivateAcl (Join-Path $migration 'enrollment.secret') @() $sids['CSweet.Migrator']
}
$completed = Join-Path $migration 'completed'
[IO.File]::WriteAllText($completed, '')
Set-PrivateAcl $completed @() $sids['CSweet.Migrator']
$deadline = [DateTime]::UtcNow.AddMinutes(10)
try {
    & (Join-Path ([Environment]::SystemDirectory) 'sc.exe') start 'CSweet.Migrator' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Headquarters migration service could not start.' }
    do {
        Start-Sleep -Milliseconds 250
        $service = Get-Service -Name 'CSweet.Migrator'
    } while ($service.Status -ne 'Stopped' -and [DateTime]::UtcNow -lt $deadline)
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'CSweet.Migrator'; throw 'Headquarters database migration timed out.' }
    if ((Get-Item -LiteralPath $completed).Length -ne 1 -or [IO.File]::ReadAllText($completed) -cne '1') {
        throw 'Database migration failed. Check the CSweet.Migrator event log and the configured database.'
    }
    if ($EnrollOffice) {
        $source = Join-Path $migration 'enrollment.secret'
        $info = Get-Item -LiteralPath $source
        if ($info.Length -gt 256 -or $info.Length -lt 32 -or $info.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Invalid initial Office enrollment response.'
        }
        # Copy data only. A service-owned file never becomes an administrator-owned executable or writable link.
        $token = [IO.File]::ReadAllText($source)
        if ($token -cnotmatch '^[A-Za-z0-9_-]{32,256}$') { throw 'Invalid initial Office enrollment response.' }
        $destination = Join-Path $root 'office-enrollment.secret'
        if ([IO.File]::Exists($destination)) { Assert-ProtectedPath $destination }
        [IO.File]::WriteAllText($destination, $token)
        Set-PrivateAcl $destination @() $null
    }
} finally {
    foreach ($name in @('enroll-office', 'enrollment.secret', 'completed')) {
        $path = Join-Path $migration $name
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
}
