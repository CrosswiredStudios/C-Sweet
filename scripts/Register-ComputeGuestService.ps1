#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
# Port 2763 mapped through the standard Hyper-V Linux VSOCK service ID scheme.
$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000acb-facb-11e6-bd58-64006a7986d3'
$elementName = 'C-Sweet Compute Guest'
if (-not $PSCmdlet.ShouldProcess($registryPath, 'Register the C-Sweet compute guest socket service')) { return }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'An elevated installer process is required.' }
}
finally { $identity.Dispose() }

if (Test-Path -LiteralPath $registryPath) {
    $existing = Get-ItemPropertyValue -LiteralPath $registryPath -Name 'ElementName' -ErrorAction Stop
    if ($existing -cne $elementName) { throw 'The fixed guest service ID has an unexpected registration; inspect it explicitly.' }
    Write-Output 'The compute guest socket service is already registered.'
    return
}
New-Item -Path $registryPath -ErrorAction Stop | Out-Null
New-ItemProperty -LiteralPath $registryPath -Name 'ElementName' -PropertyType String -Value $elementName -ErrorAction Stop | Out-Null
Write-Output 'Compute guest socket service registered. No VM or Windows service was started.'
