[CmdletBinding()]
param(
    [switch] $Force,
    [switch] $Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'Administrator approval is required.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        ('"' + $PSCommandPath + '"'), '-Elevated')
    if ($Force) { $arguments += '-Force' }
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet uninstall failed with exit code $($process.ExitCode)." }
    return
}

$nodeRoot = Join-Path $env:ProgramData 'CSweet\ExecutionNode'
$programFilesRoot = Join-Path $env:ProgramFiles 'CSweet\RuntimeHost'
$runtimeDataRoot = Join-Path $env:ProgramData 'CSweet\AgentRuntime'
$nodeService = Get-Service -Name 'CSweet.ExecutionNode' -ErrorAction SilentlyContinue
$runtimeService = Get-Service -Name 'CSweet.RuntimeHost' -ErrorAction SilentlyContinue
$fleetInstalled = $null -ne $nodeService -or $null -ne $runtimeService -or
    (Test-Path -LiteralPath $programFilesRoot) -or (Test-Path -LiteralPath $nodeRoot)
$maintenance = Join-Path $nodeRoot 'maintenance'
$drainPath = Join-Path $maintenance 'drain-state'
$activeRoot = Join-Path $maintenance 'active-assignments'
$drainState = if (Test-Path -LiteralPath $drainPath -PathType Leaf) {
    [IO.File]::ReadAllText($drainPath).Trim()
} else { '' }
$activeCount = if (Test-Path -LiteralPath $activeRoot -PathType Container) {
    @(Get-ChildItem -LiteralPath $activeRoot -File -Filter '*.active').Count
} else { 0 }
if ($fleetInstalled -and -not $Force -and ($drainState -ne 'draining' -or $activeCount -ne 0)) {
    throw 'Drain this node in C-Sweet and wait for active assignments to reach zero before uninstalling. Use -Force only after revocation.'
}

foreach ($serviceName in @('CSweet.ExecutionNode', 'CSweet.RuntimeHost')) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Host
        if ($LASTEXITCODE -notin @(0, 1060)) { throw "The $serviceName service could not be removed." }
    }
}

$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'
$serviceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\$serviceId"
Remove-Item -LiteralPath $serviceRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
foreach ($name in @('CSWEET_HYPERV_BROKER_SERVICE_ID', 'CSWEET_HYPERV_DATA_ROOT', 'CSWEET_ARTIFACT_MEDIA_ROOT')) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Machine')
}

foreach ($path in @($programFilesRoot, $nodeRoot, $runtimeDataRoot)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

Write-Host 'C-Sweet RuntimeHost and ExecutionNode were uninstalled. Revoke the node in fleet administration if it was not already revoked.' -ForegroundColor Green
