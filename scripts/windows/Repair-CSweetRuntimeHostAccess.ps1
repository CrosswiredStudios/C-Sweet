[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ControlPlaneUserSid,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\RuntimeHost",
    [string] $DataRoot = "$env:ProgramData\CSweet\AgentRuntime",
    [string] $ProgressPath,
    [guid] $ProgressJobId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This repair must run from the C-Sweet administrator prompt.'
    }
}

function Resolve-ProtectedInstalledPath([string] $Root, [string] $Candidate) {
    if ([String]::IsNullOrWhiteSpace($Candidate) -or -not [IO.Path]::IsPathRooted($Candidate)) {
        throw 'The installed RuntimeHost path is invalid.'
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Candidate)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installed RuntimeHost path is outside the protected installation directory.'
    }
    return $fullPath
}

Assert-Administrator
try {
    $controlPlaneIdentity = [Security.Principal.SecurityIdentifier]::new($ControlPlaneUserSid)
    $null = $controlPlaneIdentity.Translate([Security.Principal.NTAccount])
} catch {
    throw 'The C-Sweet control-plane Windows user identity is invalid.'
}
if ($ProgressJobId -eq [guid]::Empty) { $ProgressJobId = [guid]::NewGuid() }
if ([String]::IsNullOrWhiteSpace($ProgressPath)) {
    $ProgressPath = Join-Path $env:ProgramData "CSweet\Setup\windows-isolation-$($ProgressJobId.ToString('N')).json"
}
. (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
$ProgressPath = Initialize-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId `
    -ControlPlaneUserSid $ControlPlaneUserSid

try {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
        -State running -PhaseKey repair-runtime-access -PhaseDisplayName 'Refreshing secure runtime access' `
        -Message 'C-Sweet is updating protected RuntimeHost access for this Windows account.' -PercentComplete 98 `
        -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 60

    $serviceName = 'CSweet.RuntimeHost'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { throw 'The RuntimeHost Windows service is not installed.' }

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $imagePath = [string](Get-ItemProperty -LiteralPath $serviceRegistryPath -Name ImagePath).ImagePath
    $match = [regex]::Match($imagePath, '^"(?<exe>[^"]+)"\s+--contentRoot\s+"(?<root>[^"]+)"$')
    if (-not $match.Success) { throw 'The installed RuntimeHost service command is invalid.' }

    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    $contentRoot = Resolve-ProtectedInstalledPath $InstallRoot $match.Groups['root'].Value
    $runtimeHostExe = Resolve-ProtectedInstalledPath $InstallRoot $match.Groups['exe'].Value
    $expectedExecutable = Join-Path $contentRoot 'runtime\CSweet.RuntimeHost.exe'
    if (-not $runtimeHostExe.Equals([IO.Path]::GetFullPath($expectedExecutable), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $runtimeHostExe -PathType Leaf)) {
        throw 'The installed RuntimeHost executable could not be verified.'
    }

    $configurationPath = Join-Path $contentRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw 'The installed RuntimeHost configuration is missing.'
    }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if ($null -eq $configuration.CSweet -or $null -eq $configuration.CSweet.AgentRuntime -or
        $null -eq $configuration.CSweet.AgentRuntime.RuntimeHost) {
        throw 'The installed RuntimeHost configuration is invalid.'
    }
    $configuration.CSweet.AgentRuntime.RuntimeHost.AllowedClientSid = $ControlPlaneUserSid

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $temporaryConfiguration = Join-Path $contentRoot "appsettings.$($ProgressJobId.ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryConfiguration,
            ($configuration | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryConfiguration -Destination $configurationPath -Force
    } finally {
        Remove-Item -LiteralPath $temporaryConfiguration -Force -ErrorAction SilentlyContinue
    }

    $DataRoot = [IO.Path]::GetFullPath($DataRoot)
    $keyPath = Join-Path $DataRoot 'runtime-host.key'
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw 'The RuntimeHost authentication key is missing.'
    }
    & "$env:SystemRoot\System32\icacls.exe" $keyPath '/inheritance:r' '/grant:r' "*$ControlPlaneUserSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost key permissions could not be repaired.' }
    foreach ($artifactRoot in @((Join-Path $DataRoot 'artifacts'), (Join-Path $DataRoot 'artifact-media'))) {
        if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) { continue }
        & "$env:SystemRoot\System32\icacls.exe" $artifactRoot '/inheritance:r' '/grant:r' "*$ControlPlaneUserSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The RuntimeHost artifact permissions could not be repaired: $artifactRoot" }
    }

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
        -State completed -PhaseKey repair-complete -PhaseDisplayName 'Secure runtime access refreshed' `
        -Message 'RuntimeHost access is ready for automatic validation.' -PercentComplete 100
    Write-Host 'C-Sweet RuntimeHost access was repaired successfully.' -ForegroundColor Green
} catch {
    try {
        $stoppedService = Get-Service -Name 'CSweet.RuntimeHost' -ErrorAction SilentlyContinue
        if ($null -ne $stoppedService -and $stoppedService.Status -ne 'Running') {
            Start-Service -Name 'CSweet.RuntimeHost'
        }
    } catch { }
    try {
        Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
            -State failed -PhaseKey repair-failed -PhaseDisplayName 'Secure runtime repair needs setup' `
            -Message 'C-Sweet could not refresh the existing RuntimeHost installation.' -PercentComplete 100 `
            -ErrorCode 'runtime-access-repair-failed' -ErrorMessage $_.Exception.Message
    } catch { }
    Write-Error $_.Exception.Message
    exit 1
}
