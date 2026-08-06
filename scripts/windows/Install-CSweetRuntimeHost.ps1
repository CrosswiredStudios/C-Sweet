[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadRoot,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\RuntimeHost",
    [string] $DataRoot = "$env:ProgramData\CSweet\AgentRuntime",
    [string] $ControlPlaneUserSid,
    [string] $ProgressPath,
    [guid] $ProgressJobId,
    [string] $ProgressWorkflow = 'packaged-installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This installer must run from the C-Sweet administrator prompt.'
    }
}

function Resolve-SafeChildPath([string] $Root, [string] $RelativePath) {
    if ([String]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Invalid payload path: $RelativePath"
    }
    $segments = $RelativePath.Replace('/', '\').Split('\')
    if ($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }) {
        throw "Invalid payload path: $RelativePath"
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($rootPath, $RelativePath))
    if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload path escaped its root: $RelativePath"
    }
    return $candidate
}

function Assert-Sha256([string] $Value, [string] $Name) {
    if ($Value -notmatch '^sha256:[0-9a-f]{64}$') {
        throw "$Name must be a lowercase SHA-256 digest."
    }
}

function Invoke-Sc([string[]] $Arguments) {
    $output = @(& "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Out-Host
    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join ' '
        throw "Windows service configuration failed while running 'sc.exe $($Arguments -join ' ')' with exit code $exitCode. $details"
    }
}

Assert-Administrator
if ([String]::IsNullOrWhiteSpace($ControlPlaneUserSid)) {
    $ControlPlaneUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
if ($ProgressJobId -eq [guid]::Empty) { $ProgressJobId = [guid]::NewGuid() }
if ([String]::IsNullOrWhiteSpace($ProgressPath)) {
    $ProgressPath = Join-Path $env:ProgramData "CSweet\Setup\windows-isolation-$($ProgressJobId.ToString('N')).json"
}
try {
    $controlPlaneIdentity = [Security.Principal.SecurityIdentifier]::new($ControlPlaneUserSid)
    $null = $controlPlaneIdentity.Translate([Security.Principal.NTAccount])
} catch {
    throw 'The C-Sweet control-plane Windows user identity is invalid.'
}
. (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
$ProgressPath = Initialize-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId `
    -ControlPlaneUserSid $ControlPlaneUserSid

try {
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey verify-package -PhaseDisplayName 'Verifying the secure runtime package' `
    -Message 'Every packaged file is being checked against the signed release manifest.' -PercentComplete 92 `
    -EstimatedRemainingMinimumSeconds 30 -EstimatedRemainingMaximumSeconds 180
$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
    throw 'The signed RuntimeHost payload directory is missing.'
}
$manifestPath = Join-Path $PayloadRoot 'runtime-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The RuntimeHost payload manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.packageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') {
    throw 'The RuntimeHost payload manifest schema or package version is invalid.'
}
Assert-Sha256 $manifest.guestImageDigest 'guestImageDigest'
Assert-Sha256 $manifest.certificationEvidenceDigest 'certificationEvidenceDigest'
if ($null -eq $manifest.files -or @($manifest.files).Count -lt 1 -or @($manifest.files).Count -gt 1000) {
    throw 'The RuntimeHost payload file manifest is invalid.'
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    if (-not $seen.Add([string]$file.path)) { throw "Duplicate payload path: $($file.path)" }
    $source = Resolve-SafeChildPath $PayloadRoot ([string]$file.path)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Payload file is missing: $($file.path)" }
    if ([string]$file.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Invalid payload digest: $($file.path)" }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne [string]$file.sha256) { throw "Payload integrity check failed: $($file.path)" }
}

$versionRoot = Join-Path $InstallRoot ([string]$manifest.packageVersion)
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey install-runtime -PhaseDisplayName 'Installing RuntimeHost' `
    -Message 'The verified runtime, helper, signed guest, and certification evidence are being installed.' `
    -PercentComplete 94 -EstimatedRemainingMinimumSeconds 20 -EstimatedRemainingMaximumSeconds 120
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
foreach ($file in $manifest.files) {
    $source = Resolve-SafeChildPath $PayloadRoot ([string]$file.path)
    $destination = Resolve-SafeChildPath $versionRoot ([string]$file.path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($installedHash -ceq [string]$file.sha256) {
            continue
        }
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$runtimeHostExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.runtimeHostExecutable)
$helperExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.helperExecutable)
$guestImage = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImage)
$guestSignature = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSignature)
$signingCertificate = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSigningCertificate)
$certificationEvidence = Resolve-SafeChildPath $versionRoot ([string]$manifest.certificationEvidence)
foreach ($required in @($runtimeHostExe, $helperExe, $guestImage, $guestSignature, $signingCertificate, $certificationEvidence)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required installed file is missing: $required" }
}

$artifactStoreRoot = Join-Path $DataRoot 'artifacts'
$artifactMediaRoot = Join-Path $DataRoot 'artifact-media'
$hyperVDataRoot = Join-Path $DataRoot 'hyperv'
New-Item -ItemType Directory -Path $artifactStoreRoot, $artifactMediaRoot, $hyperVDataRoot -Force | Out-Null

$keyPath = Join-Path $DataRoot 'runtime-host.key'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey secure-local-state -PhaseDisplayName 'Securing local runtime state' `
    -Message 'C-Sweet is creating the authentication key and applying protected Windows permissions.' `
    -PercentComplete 96 -EstimatedRemainingMinimumSeconds 10 -EstimatedRemainingMaximumSeconds 90
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    $keyBytes = [byte[]]::new(32)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($keyBytes) } finally { $random.Dispose() }
    [IO.File]::WriteAllText($keyPath, [Convert]::ToBase64String($keyBytes), [Text.UTF8Encoding]::new($false))
}
& "$env:SystemRoot\System32\icacls.exe" $keyPath '/inheritance:r' "/grant:r" "*$ControlPlaneUserSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost key ACL could not be secured.' }
foreach ($artifactRoot in @($artifactStoreRoot, $artifactMediaRoot)) {
    & "$env:SystemRoot\System32\icacls.exe" $artifactRoot '/inheritance:r' "/grant:r" "*$ControlPlaneUserSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "The artifact storage ACL could not be secured: $artifactRoot" }
}

$config = @{
    CSweet = @{
        AgentRuntime = @{
            RuntimeHost = @{ NamedPipeName = 'csweet-runtime-host-v1'; AllowedClientSid = $ControlPlaneUserSid; UnixSocketPath = '/var/run/csweet/runtime-host-v1.sock'; ConnectTimeoutSeconds = 10; MaximumFrameBytes = 1048576 }
            HostAuthentication = @{ KeyId = 'control-plane'; SharedKeyBase64 = ''; SharedKeyFilePath = $keyPath }
            Providers = @{
                HyperV = @{
                    HelperExecutablePath = $helperExe
                    GuestImagePath = $guestImage
                    GuestImageDigest = [string]$manifest.guestImageDigest
                    GuestImageSignaturePath = $guestSignature
                    GuestImageSigningCertificatePath = $signingCertificate
                    GuestImageSigningCertificateThumbprint = [string]$manifest.guestImageSigningCertificateThumbprint
                    ArtifactImageRoot = $artifactMediaRoot
                    BrokerProtocolVersion = '1.0'
                    CertificationSuiteVersion = [string]$manifest.certificationSuiteVersion
                    CertificationEvidencePath = $certificationEvidence
                    CertificationEvidenceDigest = [string]$manifest.certificationEvidenceDigest
                    CertifiedAt = [string]$manifest.certifiedAt
                    CertificationExpiresAt = $manifest.certificationExpiresAt
                }
                Firecracker = @{}
                AppleVirtualization = @{}
            }
        }
    }
}
[IO.File]::WriteAllText((Join-Path $versionRoot 'appsettings.json'),
    ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'
$legacyServiceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{$serviceId}"
$serviceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\$serviceId"
Remove-Item -LiteralPath $legacyServiceRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $serviceRegistryPath -Force | Out-Null
New-ItemProperty -Path $serviceRegistryPath -Name 'ElementName' -PropertyType String -Value 'C-Sweet authenticated agent broker' -Force | Out-Null
[Environment]::SetEnvironmentVariable('CSWEET_HYPERV_BROKER_SERVICE_ID', $serviceId, 'Machine')
[Environment]::SetEnvironmentVariable('CSWEET_HYPERV_DATA_ROOT', $hyperVDataRoot, 'Machine')
[Environment]::SetEnvironmentVariable('CSWEET_ARTIFACT_MEDIA_ROOT', $artifactMediaRoot, 'Machine')

$serviceName = 'CSweet.RuntimeHost'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey start-service -PhaseDisplayName 'Starting the RuntimeHost service' `
    -Message 'The privileged VM lifecycle service is being registered and started.' -PercentComplete 98 `
    -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 60
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$binaryPath = '"' + $runtimeHostExe + '" --contentRoot "' + $versionRoot + '"'
if ($null -eq $service) {
    $service = New-Service -Name $serviceName -BinaryPathName $binaryPath `
        -DisplayName 'C-Sweet RuntimeHost' `
        -Description 'Privileged C-Sweet virtual-machine lifecycle service. No network listener.' `
        -StartupType Automatic
} else {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    if ($null -eq $serviceConfiguration) {
        throw 'The RuntimeHost service configuration could not be loaded.'
    }
    $changeResult = Invoke-CimMethod -InputObject $serviceConfiguration -MethodName Change -Arguments @{
        PathName = $binaryPath
        StartName = 'LocalSystem'
    }
    if ($null -eq $changeResult -or [int]$changeResult.ReturnValue -ne 0) {
        $returnValue = if ($null -eq $changeResult) { 'no result' } else { [string]$changeResult.ReturnValue }
        throw "The RuntimeHost service executable could not be updated. Win32_Service.Change returned $returnValue."
    }
    Set-Service -Name $serviceName -DisplayName 'C-Sweet RuntimeHost' `
        -Description 'Privileged C-Sweet virtual-machine lifecycle service. No network listener.' `
        -StartupType Automatic
}
$serviceEnvironment = @(
    "CSWEET_HYPERV_BROKER_SERVICE_ID=$serviceId",
    "CSWEET_HYPERV_DATA_ROOT=$hyperVDataRoot",
    "CSWEET_ARTIFACT_MEDIA_ROOT=$artifactMediaRoot"
)
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'Environment' -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
Invoke-Sc @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/none/0')
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
if ($ProgressWorkflow -eq 'packaged-installer') {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
        -State completed -PhaseKey setup-complete -PhaseDisplayName 'Secure agent runtime ready' `
        -Message 'RuntimeHost is installed and ready for final application validation.' -PercentComplete 100
} else {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
        -State running -PhaseKey service-ready -PhaseDisplayName 'RuntimeHost service installed' `
        -Message 'RuntimeHost is running. C-Sweet is completing final readiness validation.' -PercentComplete 99 `
        -EstimatedRemainingMinimumSeconds 1 -EstimatedRemainingMaximumSeconds 30
}
Write-Host 'C-Sweet RuntimeHost was installed and started successfully.' -ForegroundColor Green
} catch {
    try {
        Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
            -State failed -PhaseKey install-failed -PhaseDisplayName 'Secure runtime installation failed' `
            -Message 'C-Sweet could not install the secure runtime.' -PercentComplete 100 `
            -ErrorCode 'runtime-install-failed' -ErrorMessage $_.Exception.Message
    } catch { }
    throw
}
