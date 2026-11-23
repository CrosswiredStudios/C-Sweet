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

function Initialize-OfficeIssuer {
    $consumers = @('CSweet.Api', 'CSweet.ExecutionGateway')
    $readers = @($sids['CSweet.Api'], $sids['CSweet.ExecutionGateway'])
    $configured = foreach ($consumer in $consumers) {
        $profile = Get-Content -LiteralPath (Join-Path $root "$consumer.json") -Raw | ConvertFrom-Json
        $values = foreach ($setting in @('AuthorityPfxPath', 'AuthorityCertificateThumbprint')) {
            $key = "CSweet:ExecutionNodeCertificates:$setting"
            $prop = $profile.PSObject.Properties[$key]
            if (-not $prop) {
                $section = $profile
                foreach ($part in $key.Split(':')) {
                    $prop = $section.PSObject.Properties[$part]
                    if (-not $prop -or $null -eq $prop.Value) { break }
                    $section = $prop.Value
                }
            }
            if ($prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) { "$setting=$($prop.Value)" }
        }
        if (@($values).Count -gt 1) { throw 'Configure one Office identity issuer per service.' }
        if ($values) { [string]$values } else { 'installer' }
    }
    if ($configured[0] -ine $configured[1]) { throw 'Headquarters API and Execution Gateway require the same Office identity issuer.' }
    if ($configured[0] -ne 'installer') { return }
    $record = Join-Path $root 'office-issuer.json'
    if ([IO.File]::Exists($record)) {
        Assert-ProtectedPath $record
        $saved = (Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).CSweet.ExecutionNodeCertificates.AuthorityCertificateThumbprint
        if ($saved -cnotmatch '^[A-Fa-f0-9]{40}$' -or -not (Test-Path -LiteralPath "Cert:\LocalMachine\My\$saved")) {
            throw 'Restore the registered Office identity issuer before continuing.'
        }
        Set-PrivateAcl $record $readers $null
        return
    }
    $constraints = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 0, $true)
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=C-Sweet Headquarters Office Identity Issuer' -Extension $constraints `
        -KeyAlgorithm ECDSA_nistP256 -KeyExportPolicy NonExportable -KeyUsage CertSign,CRLSign `
        -Provider 'Microsoft Software Key Storage Provider' -HashAlgorithm SHA256 -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotBefore ([DateTime]::Now.AddHours(-1)) -NotAfter ([DateTime]::Now.AddYears(10))
    $pending = Join-Path $root ("issuer-" + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $key = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate)
        try { $file = $key.Key.UniqueName } finally { $key.Dispose() }
        if (-not $file -or [IO.Path]::GetFileName($file) -cne $file) { throw 'Unexpected machine key location.' }
        $privateKey = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Microsoft\Crypto\Keys\$file"
        Set-PrivateAcl $privateKey $readers $null
        $settings = @{ CSweet=@{ ExecutionNodeCertificates=@{ AuthorityCertificateThumbprint=$certificate.Thumbprint } } }
        [IO.File]::WriteAllText($pending, ($settings | ConvertTo-Json -Depth 4))
        Set-PrivateAcl $pending $readers $null
        [IO.File]::Move($pending, $record)
    } catch {
        Remove-Item -LiteralPath ("Cert:\LocalMachine\My\" + $certificate.Thumbprint) -DeleteKey
        throw
    } finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
}

Assert-ProtectedPath $vendor
Assert-ProtectedPath $root
if ($EnrollOffice -and (Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue)) {
    throw 'An Office is already installed. Preserve its enrollment through the installed Office maintenance flow.'
}
$sids = @{}
$profiles = @{}
foreach ($name in $serviceNames) {
    $file = Join-Path $root "$name.json"
    Assert-ProtectedPath $file
    $profiles[$name] = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $sids[$name] = [Security.Principal.NTAccount]::new('NT SERVICE', $name).Translate([Security.Principal.SecurityIdentifier])
}
Set-PrivateAcl $root @($sids.Values) $null
foreach ($name in $serviceNames) { Set-PrivateAcl (Join-Path $root "$name.json") @($sids[$name]) $null }
. (Join-Path $PSScriptRoot 'HeadquartersServiceSettings.ps1')
Initialize-HeadquartersServiceSettings $profiles

# Rotate only this setup invocation's private migration workspace. All service
# executables and their configuration remain read-only to the migration account.
$migration = Join-Path $root 'Migration'
if (-not (Test-Path -LiteralPath $migration)) { New-Item -ItemType Directory -Path $migration | Out-Null }
Assert-ProtectedPath $migration
Set-PrivateAcl $migration @($sids['CSweet.Migrator']) $null
foreach ($name in @('completed', 'enroll-office', 'enrollment.secret', 'compute-business.id', 'combined-enrollment.id')) {
    $path = Join-Path $migration $name
    if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
}
if ($EnrollOffice) {
    Assert-ProtectedPath (Join-Path $root 'office.json')
    Initialize-OfficeIssuer
    [IO.File]::WriteAllText((Join-Path $migration 'enroll-office'), '1')
    Set-PrivateAcl (Join-Path $migration 'enroll-office') @($sids['CSweet.Migrator']) $null
    [IO.File]::WriteAllText((Join-Path $migration 'enrollment.secret'), '')
    Set-PrivateAcl (Join-Path $migration 'enrollment.secret') @() $sids['CSweet.Migrator']
    [IO.File]::WriteAllText((Join-Path $migration 'combined-enrollment.id'), '')
    Set-PrivateAcl (Join-Path $migration 'combined-enrollment.id') @() $sids['CSweet.Migrator']
    $catalogPath = Join-Path $PSScriptRoot 'office-compute-release.json'
    if ([IO.File]::Exists($catalogPath)) {
        Assert-ProtectedPath $catalogPath
        . (Join-Path $PSScriptRoot 'OfficeComputeProvisioning.ps1')
        $deployment = Get-Content -Raw -LiteralPath (Join-Path $root 'office.json') | ConvertFrom-Json
        $release = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
        # Ask the database for an automatic scope only when the install budget can support it.
        if (!$deployment.PSObject.Properties['compute'] -and (Test-OfficeComputeBudget $deployment $null)) {
            [IO.File]::WriteAllText((Join-Path $migration 'compute-business.id'), '')
            Set-PrivateAcl (Join-Path $migration 'compute-business.id') @() $sids['CSweet.Migrator']
        }
    }
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
        $generated = Join-Path $root 'combined-office-compute.json'
        if ([IO.File]::Exists($generated)) { Assert-ProtectedPath $generated }
        $settings = @{}
        if ([IO.File]::Exists($catalogPath)) {
            $scope = ''
            $scopePath = Join-Path $migration 'compute-business.id'
            if ([IO.File]::Exists($scopePath)) {
                $scopeFile = Get-Item -LiteralPath $scopePath
                if ($scopeFile.Length -gt 36 -or ($scopeFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid compute scope output.' }
                $scope = [IO.File]::ReadAllText($scopePath)
            }
            $policy = Get-CombinedOfficeComputeSettings $deployment $release $scope
            if ($policy) { $settings = $policy }
        }
        $enrollmentFile = Get-Item -LiteralPath (Join-Path $migration 'combined-enrollment.id')
        $enrollmentId = [guid]::Empty
        if ($enrollmentFile.Length -ne 36 -or ($enrollmentFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ![guid]::TryParseExact([IO.File]::ReadAllText($enrollmentFile.FullName), 'D', [ref]$enrollmentId) -or $enrollmentId -eq [guid]::Empty) {
            throw 'Missing combined Office enrollment identity.'
        }
        if (!$settings.ContainsKey('CSweet')) { $settings.CSweet = @{} }
        $settings.CSweet.CombinedInstallOfficeEnrollmentId = $enrollmentId.ToString('D')
        [IO.File]::WriteAllText($generated, ($settings | ConvertTo-Json -Depth 7))
        Set-PrivateAcl $generated @($sids['CSweet.ExecutionGateway']) $null
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
    foreach ($name in @('enroll-office', 'enrollment.secret', 'completed', 'compute-business.id', 'combined-enrollment.id')) {
        $path = Join-Path $migration $name
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
}
