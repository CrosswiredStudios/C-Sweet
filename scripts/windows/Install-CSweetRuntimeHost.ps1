[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadRoot,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\RuntimeHost",
    [string] $DataRoot = "$env:ProgramData\CSweet\AgentRuntime",
    [string] $ControlPlaneUserSid,
    [string] $ControlPlaneUrl,
    [string] $EnrollmentTokenInputPath,
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

$existingNodeService = Get-Service -Name 'CSweet.ExecutionNode' -ErrorAction SilentlyContinue
if ($null -ne $existingNodeService) {
    $maintenance = Join-Path $env:ProgramData 'CSweet\ExecutionNode\maintenance'
    $drainPath = Join-Path $maintenance 'drain-state'
    $activeRoot = Join-Path $maintenance 'active-assignments'
    $drainState = if (Test-Path -LiteralPath $drainPath -PathType Leaf) {
        [IO.File]::ReadAllText($drainPath).Trim()
    } else { '' }
    $activeCount = if (Test-Path -LiteralPath $activeRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $activeRoot -File -Filter '*.active').Count
    } else { 0 }
    if ($drainState -ne 'draining' -or $activeCount -ne 0) {
        throw 'Drain this node in C-Sweet and wait for active assignments to reach zero before upgrading RuntimeHost.'
    }
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
$helperManifestPath = ([string]$manifest.helperExecutable).Replace('\\', '/')
$helperManifestEntries = @($manifest.files | Where-Object { ([string]$_.path).Replace('\\', '/') -ceq $helperManifestPath })
if ($helperManifestEntries.Count -ne 1) { throw 'The RuntimeHost helper must have exactly one payload digest entry.' }
$helperExecutableDigest = "sha256:$([string]$helperManifestEntries[0].sha256)"
Assert-Sha256 $helperExecutableDigest 'helperExecutableDigest'
$executionNodeExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.executionNodeExecutable)
$guestImage = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImage)
$guestSignature = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSignature)
$signingCertificate = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSigningCertificate)
$certificationEvidence = Resolve-SafeChildPath $versionRoot ([string]$manifest.certificationEvidence)
foreach ($required in @($runtimeHostExe, $helperExe, $executionNodeExe, $guestImage, $guestSignature, $signingCertificate, $certificationEvidence)) {
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
& "$env:SystemRoot\System32\icacls.exe" $keyPath '/inheritance:r' "/grant:r" "*$ControlPlaneUserSid`:R" '*S-1-5-19:R' '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost key ACL could not be secured.' }
foreach ($artifactRoot in @($artifactStoreRoot, $artifactMediaRoot)) {
    & "$env:SystemRoot\System32\icacls.exe" $artifactRoot '/inheritance:r' "/grant:r" "*$ControlPlaneUserSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "The artifact storage ACL could not be secured: $artifactRoot" }
}

$config = @{
    Logging = @{
        LogLevel = @{ Default = 'Information'; 'Microsoft.Hosting.Lifetime' = 'Information' }
        EventLog = @{ LogLevel = @{ Default = 'Information' } }
    }
    CSweet = @{
        AgentRuntime = @{
            RuntimeHost = @{ NamedPipeName = 'csweet-runtime-host-v1'; AllowedClientSid = $ControlPlaneUserSid; AllowedClientSids = @($ControlPlaneUserSid, 'S-1-5-19'); UnixSocketPath = '/var/run/csweet/runtime-host-v1.sock'; ConnectTimeoutSeconds = 10; MaximumFrameBytes = 1048576 }
            HostAuthentication = @{ KeyId = 'control-plane'; SharedKeyBase64 = ''; SharedKeyFilePath = $keyPath }
            Providers = @{
                HyperV = @{
                    HelperExecutablePath = $helperExe
                    HelperExecutableDigest = $helperExecutableDigest
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

if (-not [String]::IsNullOrWhiteSpace($ControlPlaneUrl) -and
    -not [String]::IsNullOrWhiteSpace($EnrollmentTokenInputPath)) {
    $gatewayUri = [Uri]$ControlPlaneUrl
    if (-not $gatewayUri.IsAbsoluteUri -or $gatewayUri.Scheme -ne 'https') { throw 'ControlPlaneUrl must be an absolute HTTPS URL.' }
    $inputPath = [IO.Path]::GetFullPath($EnrollmentTokenInputPath)
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw 'The protected enrollment input is missing.' }
    $token = [IO.File]::ReadAllText($inputPath).Trim()
    Remove-Item -LiteralPath $inputPath -Force
    if ($token.Length -lt 32 -or $token.Length -gt 256) { throw 'The protected enrollment token is invalid.' }
    $nodeDataRoot = Join-Path $env:ProgramData 'CSweet\ExecutionNode'
    New-Item -ItemType Directory -Path $nodeDataRoot -Force | Out-Null
    $nodeTokenPath = Join-Path $nodeDataRoot 'enrollment.secret'
    [IO.File]::WriteAllText($nodeTokenPath, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    & "$env:SystemRoot\System32\icacls.exe" $nodeDataRoot '/inheritance:r' '/grant:r' '*S-1-5-19:(OI)(CI)M' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The ExecutionNode state ACL could not be secured.' }
    $config.CSweet.ExecutionNode = @{
        ControlPlaneUrl = $gatewayUri.AbsoluteUri
        StateDirectory = $nodeDataRoot
        ArtifactCacheDirectory = (Join-Path $nodeDataRoot 'artifact-cache')
        ArtifactMediaDirectory = $artifactMediaRoot
        EnrollmentTokenFilePath = $nodeTokenPath
    }
    [IO.File]::WriteAllText((Join-Path $versionRoot 'appsettings.json'),
        ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $nodeServiceName = 'CSweet.ExecutionNode'
    $nodeBinaryPath = '"' + $executionNodeExe + '" --contentRoot "' + $versionRoot + '" --environment Production'
    $nodeService = Get-Service -Name $nodeServiceName -ErrorAction SilentlyContinue
    if ($null -eq $nodeService) {
        $nodeService = New-Service -Name $nodeServiceName -BinaryPathName $nodeBinaryPath `
            -DisplayName 'C-Sweet ExecutionNode' -Description 'Unprivileged outbound C-Sweet execution node.' -StartupType Automatic
    } elseif ($nodeService.Status -ne 'Stopped') {
        Stop-Service -Name $nodeServiceName -Force
        $nodeService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $nodeServiceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nodeServiceName'"
    if ($null -eq $nodeServiceConfiguration) { throw 'The ExecutionNode service configuration could not be loaded.' }
    $nodeChangeResult = Invoke-CimMethod -InputObject $nodeServiceConfiguration -MethodName Change -Arguments @{
        PathName = $nodeBinaryPath
        StartName = 'NT AUTHORITY\LocalService'
        StartPassword = ''
        StartMode = 'Automatic'
    }
    if ($null -eq $nodeChangeResult -or [int]$nodeChangeResult.ReturnValue -ne 0) {
        $nodeReturnValue = if ($null -eq $nodeChangeResult) { 'no result' } else { [string]$nodeChangeResult.ReturnValue }
        throw "The ExecutionNode service could not be configured. Win32_Service.Change returned $nodeReturnValue."
    }
    Invoke-Sc @('failure', $nodeServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/none/0')
    Start-Service -Name $nodeServiceName
    (Get-Service -Name $nodeServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}
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
