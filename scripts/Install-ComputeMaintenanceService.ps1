#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$ExecutablePath)

$ErrorActionPreference = 'Stop'
$serviceName = 'CSweet.Compute.HyperV'
if (-not [IO.Path]::IsPathFullyQualified($ExecutablePath) -or $ExecutablePath.Contains('"') -or
    $ExecutablePath.ToCharArray().Where({ [char]::IsControl($_) }).Count -gt 0) {
    throw 'Supply an absolute published compute executable path without quotes or control characters.'
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
if ([IO.Path]::GetFileName($resolvedExecutable) -cne 'CSweet.Compute.HyperV.exe' -or
    -not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw 'The published CSweet.Compute.HyperV.exe is required.'
}
if (-not $PSCmdlet.ShouldProcess($resolvedExecutable, "Register $serviceName as LocalSystem, configure failure recovery and automatic startup, leave stopped")) { return }
if (-not $IsWindows) { throw 'Windows is required.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'An elevated installer process is required.' }
}
finally { $identity.Dispose() }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw 'The compute service already exists. This first-install command never replaces or reconfigures an existing service.'
}

# Bootstrap trust before executing the preflight binary. The runtime repeats detailed protection checks.
function Assert-TrustedPath([string]$Path) {
    $current = Get-Item -LiteralPath $Path -Force
    $ancestor = $false
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Runtime paths must not traverse links.' }
        $acl = Get-Acl -LiteralPath $current.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Runtime paths require a trusted owner.' }
        $mutating = [Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteExtendedAttributes,WriteAttributes,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
        if ($ancestor) {
            $mutating = $mutating -band (-bnot [int][Security.AccessControl.FileSystemRights]'WriteData,AppendData')
        }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if (($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $mutating) -ne 0) {
                throw 'An untrusted principal can modify the runtime path.'
            }
        }
        $current = [IO.Directory]::GetParent($current.FullName)
        $ancestor = $true
    }
}
$runtimeDirectory = [IO.Path]::GetDirectoryName($resolvedExecutable)
Assert-TrustedPath $runtimeDirectory
$directories = [Collections.Generic.Stack[string]]::new()
$directories.Push($runtimeDirectory)
while ($directories.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
        Assert-TrustedPath $entry.FullName
        if ($entry.PSIsContainer) { $directories.Push($entry.FullName) }
    }
}
& $resolvedExecutable validate-service
if ($LASTEXITCODE -ne 0) { throw 'Compute preflight failed; no service was registered.' }

# A failure after New-Service intentionally leaves a stopped Manual service for inspection.
# Do not delete or silently reconfigure it on a retry.
$binaryPath = '"' + $resolvedExecutable + '" service'
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'C-Sweet Compute Maintenance' -StartupType Manual | Out-Null
$serviceControl = Join-Path ([Environment]::GetFolderPath('System')) 'sc.exe'
& $serviceControl failure $serviceName 'reset=' '86400' 'actions=' 'restart/60000/restart/120000/restart/300000'
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure service recovery; the service remains stopped and Manual.' }
& $serviceControl failureflag $serviceName '1'
if ($LASTEXITCODE -ne 0) { throw 'Failed to enable non-crash recovery; the service remains stopped and Manual.' }
Set-Service -Name $serviceName -StartupType Automatic
Write-Output 'Compute maintenance service registered with recovery and automatic startup; it has not been started.'
