[CmdletBinding()]
param(
    [string] $SwitchName = 'Default Switch',
    [switch] $RebuildGuest,
    [switch] $SkipInstall,
    [switch] $NoElevation,
    [string] $ControlPlaneUserSid,
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument([string] $Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Get-GuestBuildFingerprint([string] $RepositoryRoot) {
    $roots = @(
        (Join-Path $RepositoryRoot 'src\CSweet.AgentRuntime.Guest'),
        (Join-Path $RepositoryRoot 'src\CSweet.AgentRuntime.Builder'),
        (Join-Path $RepositoryRoot 'src\CSweet.AgentRuntime.Protocol'),
        (Join-Path $RepositoryRoot 'build\windows-hyperv')
    )
    $files = @($roots | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -File -Recurse | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    })
    foreach ($name in @('Directory.Build.props', 'Directory.Packages.props', 'global.json')) {
        $candidate = Join-Path $RepositoryRoot $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $files += Get-Item -LiteralPath $candidate }
    }
    $records = $files | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($RepositoryRoot.TrimEnd('\').Length).TrimStart('\').Replace('\', '/')
        "$relative`0$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($records -join "`n"))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha256.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }
}

if ([String]::IsNullOrWhiteSpace($ControlPlaneUserSid)) {
    $ControlPlaneUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
if ($ProgressJobId -eq [guid]::Empty) { $ProgressJobId = [guid]::NewGuid() }
if ([String]::IsNullOrWhiteSpace($ProgressPath)) {
    $ProgressPath = Join-Path $env:ProgramData "CSweet\Setup\windows-isolation-$($ProgressJobId.ToString('N')).json"
}

if (-not (Test-Administrator)) {
    if ($NoElevation) { throw 'Administrator access is required to prepare and test Hyper-V.' }
    Write-Host 'Windows will ask for administrator approval to prepare and test C-Sweet Hyper-V isolation.' -ForegroundColor Cyan
    $hostExecutable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-ProcessArgument $PSCommandPath),
        '-SwitchName', (Quote-ProcessArgument $SwitchName), '-ControlPlaneUserSid',
        (Quote-ProcessArgument $ControlPlaneUserSid), '-ProgressPath', (Quote-ProcessArgument $ProgressPath),
        '-ProgressJobId', (Quote-ProcessArgument $ProgressJobId.ToString('D')), '-NoElevation')
    if ($RebuildGuest) { $arguments += '-RebuildGuest' }
    if ($SkipInstall) { $arguments += '-SkipInstall' }
    $process = Start-Process -FilePath $hostExecutable -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "The elevated Windows isolation test exited with code $($process.ExitCode)." }
    return
}

. (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
$ProgressPath = Initialize-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId `
    -ControlPlaneUserSid $ControlPlaneUserSid
$progressWorkflow = 'developer-bootstrap'

function Start-CertificationHeartbeat {
    $progressHelper = Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1'
    $ownerProcessId = $PID
    return Start-Job -ScriptBlock {
        param([string] $Helper, [string] $Path, [guid] $JobId, [int] $OwnerProcessId)
        . $Helper
        $started = [DateTimeOffset]::UtcNow
        while ($true) {
            Start-Sleep -Seconds 15
            $elapsedSeconds = [Math]::Max(0, [int]([DateTimeOffset]::UtcNow - $started).TotalSeconds)
            $elapsedMinutes = [Math]::Floor($elapsedSeconds / 60)
            $percent = [Math]::Min(81, 70 + [Math]::Floor($elapsedSeconds / 45))
            $minimum = [Math]::Max(0, 180 - $elapsedSeconds)
            $maximum = [Math]::Max(60, 900 - $elapsedSeconds)
            Write-CSweetSetupProgress -Path $Path -JobId $JobId -Workflow 'developer-bootstrap' `
                -State running -PhaseKey certify-runtime -PhaseDisplayName 'Certifying agent isolation and builds' `
                -Message "Disposable no-network VMs are testing runtime isolation and a real agent build ($elapsedMinutes minutes elapsed)." `
                -PercentComplete $percent -EstimatedRemainingMinimumSeconds $minimum `
                -EstimatedRemainingMaximumSeconds $maximum -OwnerProcessId $OwnerProcessId
        }
    } -ArgumentList $progressHelper, $ProgressPath, $ProgressJobId, $ownerProcessId
}

try {
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey host-preflight -PhaseDisplayName 'Checking Windows and Hyper-V' `
    -Message 'C-Sweet is validating the Windows host before making changes.' -PercentComplete 2 `
    -EstimatedRemainingMinimumSeconds 1200 -EstimatedRemainingMaximumSeconds 3000

if (-not [Environment]::Is64BitOperatingSystem) { throw 'C-Sweet Hyper-V isolation requires 64-bit Windows.' }
$feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
if ($feature.State -eq 'EnablePending') {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State restart-required -PhaseKey windows-restart -PhaseDisplayName 'Restart Windows to continue' `
        -Message 'Hyper-V enablement is waiting for a Windows restart. Reopen C-Sweet afterward to resume.' `
        -PercentComplete 5 -RequiresRestart
    Write-Host 'Hyper-V enablement is pending. Save your work, restart Windows, then run this same command again.' -ForegroundColor Yellow
    return
}
if ($feature.State -ne 'Enabled') {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State running -PhaseKey enable-hyperv -PhaseDisplayName 'Enabling Hyper-V' `
        -Message 'Windows is enabling the required virtualization feature.' -PercentComplete 4 `
        -EstimatedRemainingMinimumSeconds 60 -EstimatedRemainingMaximumSeconds 300
    Write-Host 'Enabling Microsoft Hyper-V. C-Sweet will not restart Windows automatically...' -ForegroundColor Yellow
    $result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All -NoRestart -ErrorAction Stop
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State restart-required -PhaseKey windows-restart -PhaseDisplayName 'Restart Windows to continue' `
        -Message 'Hyper-V is enabled and needs a Windows restart. Reopen C-Sweet afterward to resume.' `
        -PercentComplete 5 -RequiresRestart
    Write-Host 'Hyper-V is enabled. Save your work, restart Windows, then run this same command again.' -ForegroundColor Yellow
    if ($result.RestartNeeded) { exit 0 }
}

Import-Module Hyper-V -ErrorAction Stop
$vmms = Get-Service -Name vmms -ErrorAction Stop
if ($vmms.Status -ne 'Running') {
    Start-Service -Name vmms
    $vmms.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}
try { $null = Get-VMHost -ErrorAction Stop }
catch { throw 'The Hyper-V hypervisor is not ready. Restart Windows and verify that virtualization is enabled in UEFI/BIOS.' }
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
    $available = @(Get-VMSwitch | Select-Object -ExpandProperty Name)
    throw "The Hyper-V build switch '$SwitchName' is unavailable. Choose one with -SwitchName. Available switches: $($available -join ', ')"
}

Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey prepare-guest -PhaseDisplayName 'Preparing the secure guest image' `
    -Message 'C-Sweet is preparing the isolated Linux environment used by untrusted agents.' -PercentComplete 8 `
    -EstimatedRemainingMinimumSeconds 900 -EstimatedRemainingMaximumSeconds 2700

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$guestFingerprint = Get-GuestBuildFingerprint $repositoryRoot
$guestImageRoot = Join-Path $repositoryRoot 'artifacts\windows-runtime\source'
$guestImage = Join-Path $guestImageRoot "csweet-agent-guest-$guestFingerprint.vhdx"
$guestReadyMarker = "$guestImage.ready"
$guestCacheReady = (Test-Path -LiteralPath $guestImage -PathType Leaf) -and
    (Test-Path -LiteralPath $guestReadyMarker -PathType Leaf) -and
    ((Get-Content -LiteralPath $guestReadyMarker -Raw).Trim() -ceq $guestFingerprint)
if ($RebuildGuest -or -not $guestCacheReady) {
    & (Join-Path $PSScriptRoot 'New-CSweetHyperVTestGuest.ps1') -SwitchName $SwitchName -OutputPath $guestImage `
        -ProgressPath $ProgressPath -ProgressJobId $ProgressJobId
    if ($LASTEXITCODE -ne 0) { throw 'The C-Sweet test guest image build failed.' }
    [IO.File]::WriteAllText($guestReadyMarker, $guestFingerprint, [Text.UTF8Encoding]::new($false))
} else {
    Write-Host "Reusing the existing test guest image: $guestImage"
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State running -PhaseKey guest-cache -PhaseDisplayName 'Reusing the secure guest image' `
        -Message 'The previously prepared guest image passed the cache check.' -PercentComplete 55 `
        -EstimatedRemainingMinimumSeconds 180 -EstimatedRemainingMaximumSeconds 600
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $repositoryRoot "artifacts\windows-test\certification-$runId"
$helperPublish = Join-Path $runRoot 'helper'
$probePublish = Join-Path $runRoot 'probe'
$smokePublish = Join-Path $runRoot 'smoke'
$smokeOutput = Join-Path $runRoot 'output'
$evidencePath = Join-Path $runRoot 'windows-hyperv.json'
New-Item -ItemType Directory -Path $helperPublish, $probePublish, $smokePublish, $smokeOutput -Force | Out-Null

Write-Host 'Publishing the Windows helper, Linux probe, and certification runner...'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey publish-components -PhaseDisplayName 'Publishing secure runtime components' `
    -Message 'C-Sweet is compiling the helper, guest probe, and certification runner.' -PercentComplete 60 `
    -EstimatedRemainingMinimumSeconds 180 -EstimatedRemainingMaximumSeconds 600
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.AgentRuntime.HyperV.Helper\CSweet.AgentRuntime.HyperV.Helper.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $helperPublish
if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V helper publish failed.' }
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.AgentRuntime.GuestProbe\CSweet.AgentRuntime.GuestProbe.csproj') `
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $probePublish
if ($LASTEXITCODE -ne 0) { throw 'The Linux isolation probe publish failed.' }
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.AgentRuntime.WindowsSmokeTest\CSweet.AgentRuntime.WindowsSmokeTest.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $smokePublish
if ($LASTEXITCODE -ne 0) { throw 'The Windows isolation certification runner publish failed.' }

$helper = Join-Path $helperPublish 'CSweet.AgentRuntime.HyperV.Helper.exe'
$probe = Join-Path $probePublish 'CSweet.AgentRuntime.GuestProbe'
$smoke = Join-Path $smokePublish 'CSweet.AgentRuntime.WindowsSmokeTest.exe'
foreach ($required in @($helper, $probe, $smoke)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "A certification executable is missing: $required" }
}

Write-Host 'Starting real no-network Hyper-V guests and running the runtime and builder certification probes...'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey certify-runtime -PhaseDisplayName 'Certifying agent isolation and builds' `
    -Message 'Disposable no-network VMs are testing runtime isolation and a real agent build.' -PercentComplete 70 `
    -EstimatedRemainingMinimumSeconds 180 -EstimatedRemainingMaximumSeconds 900
$certificationLog = Join-Path $runRoot 'certification.log'
$previousErrorActionPreference = $ErrorActionPreference
$certificationHeartbeat = Start-CertificationHeartbeat
try {
    # Windows PowerShell 5.1 represents native stderr as ErrorRecord objects.
    # Do not let the script-wide Stop preference abort before we can persist it.
    $ErrorActionPreference = 'Continue'
    $certificationOutput = @(& $smoke --helper $helper --guest-image $guestImage --probe $probe `
        --output-root $smokeOutput --evidence $evidencePath 2>&1)
    $certificationExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
    Stop-Job -Job $certificationHeartbeat -ErrorAction SilentlyContinue
    Remove-Job -Job $certificationHeartbeat -Force -ErrorAction SilentlyContinue
}
$certificationLines = @($certificationOutput | ForEach-Object { $_.ToString() })
$certificationLines | Set-Content -LiteralPath $certificationLog -Encoding UTF8
if ($certificationExitCode -ne 0 -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    $certificationDetail = $certificationLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($certificationDetail)) {
        $certificationDetail = "The certification process exited with code $certificationExitCode."
    }
    throw "The real Hyper-V isolation certification probe failed. $certificationDetail Diagnostic log: $certificationLog"
}
$certificationLines | ForEach-Object { Write-Host $_ }
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
if (-not $evidence.checks -or @($evidence.checks.PSObject.Properties | Where-Object { $_.Value -ne $true }).Count -ne 0) {
    throw 'Certification evidence contains a failed isolation check.'
}

$certificateSubject = 'CN=C-Sweet Windows Hyper-V Development Guest Image Signer'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey sign-guest -PhaseDisplayName 'Signing the certified guest image' `
    -Message 'C-Sweet is binding the tested guest image to a development-only signing identity.' -PercentComplete 82 `
    -EstimatedRemainingMinimumSeconds 60 -EstimatedRemainingMaximumSeconds 240
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $certificateSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30)
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($null -eq $certificate) {
    Write-Host 'Creating a developer-only guest-image signing certificate in the current user certificate store...'
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $certificateSubject `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(1)
}
$certificatePath = Join-Path $runRoot 'guest-image-signing.cer'
$signaturePath = "$guestImage.sig"
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT -Force | Out-Null
$imageHash = (Get-FileHash -LiteralPath $guestImage -Algorithm SHA256).Hash
$hashBytes = [byte[]]::new(32)
for ($index = 0; $index -lt $hashBytes.Length; $index++) {
    $hashBytes[$index] = [Convert]::ToByte($imageHash.Substring($index * 2, 2), 16)
}
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if ($null -eq $rsa) { throw 'The development signing certificate has no RSA private key.' }
try {
    $signature = $rsa.SignHash($hashBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
} finally {
    $rsa.Dispose()
}
[IO.File]::WriteAllBytes($signaturePath, $signature)

$packageVersion = "dev-$runId"
$payloadRoot = Join-Path $runRoot 'payload'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
    -State running -PhaseKey package-runtime -PhaseDisplayName 'Packaging the secure runtime' `
    -Message 'RuntimeHost, the native helper, signed guest, and certification evidence are being packaged.' `
    -PercentComplete 88 -EstimatedRemainingMinimumSeconds 60 -EstimatedRemainingMaximumSeconds 300
& (Join-Path $PSScriptRoot 'New-CSweetWindowsRuntimePayload.ps1') `
    -GuestImage $guestImage `
    -GuestImageSignature $signaturePath `
    -GuestImageSigningCertificate $certificatePath `
    -GuestImageSigningCertificateThumbprint $certificate.Thumbprint `
    -CertificationEvidence $evidencePath `
    -CertificationSuiteVersion ([string]$evidence.certificationSuiteVersion) `
    -CertifiedAt ([datetimeoffset]$evidence.certifiedAt) `
    -CertificationExpiresAt ([string]$evidence.certificationExpiresAt) `
    -PackageVersion $packageVersion `
    -OutputRoot $payloadRoot
if ($LASTEXITCODE -ne 0) { throw 'The certified Windows runtime payload build failed.' }

if (-not $SkipInstall) {
    Write-Host 'Installing the certified development RuntimeHost payload...'
    & (Join-Path $PSScriptRoot 'Install-CSweetRuntimeHost.ps1') -PayloadRoot $payloadRoot `
        -ControlPlaneUserSid $ControlPlaneUserSid -ProgressPath $ProgressPath -ProgressJobId $ProgressJobId `
        -ProgressWorkflow $progressWorkflow
    if ($LASTEXITCODE -ne 0) { throw 'The Windows RuntimeHost installation failed.' }
}

Write-Host ''
Write-Host 'C-Sweet Windows Hyper-V isolation is ready for application testing.' -ForegroundColor Green
Write-Host "Certification evidence: $evidencePath"
Write-Host "Development payload: $payloadRoot"
if ($SkipInstall) {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State completed -PhaseKey package-complete -PhaseDisplayName 'Development package ready' `
        -Message 'The certified development payload is ready and installation was skipped.' -PercentComplete 100
    Write-Host 'The payload was not installed because -SkipInstall was specified.' -ForegroundColor Yellow
} else {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
        -State completed -PhaseKey setup-complete -PhaseDisplayName 'Secure agent runtime ready' `
        -Message 'RuntimeHost is installed, authenticated, and ready for final application validation.' -PercentComplete 100
    Write-Host 'Return to C-Sweet. Guided setup will detect the protected RuntimeHost key and finish automatically.' -ForegroundColor Green
}
} catch {
    try {
        $failurePercent = 0
        if (Test-Path -LiteralPath $ProgressPath -PathType Leaf) {
            try {
                $previousProgress = Get-Content -LiteralPath $ProgressPath -Raw | ConvertFrom-Json
                if ([guid]$previousProgress.jobId -eq $ProgressJobId) {
                    $failurePercent = [Math]::Min(99, [Math]::Max(0, [int]$previousProgress.percentComplete))
                }
            } catch { }
        }
        Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $progressWorkflow `
            -State failed -PhaseKey setup-paused -PhaseDisplayName 'Secure runtime preparation paused' `
            -Message 'The guided preparation stopped at a recoverable step. Review the detail below, then try again.' `
            -PercentComplete $failurePercent `
            -ErrorCode 'developer-bootstrap-failed' -ErrorMessage $_.Exception.Message
    } catch { }
    throw
}
