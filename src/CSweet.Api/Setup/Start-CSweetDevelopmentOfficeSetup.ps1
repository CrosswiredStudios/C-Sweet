[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $HandoffInputPath,
    [string] $OfficeBootstrapScript,
    [switch] $UseLocalSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-QueryValue([uri] $Uri, [string] $Name) {
    foreach ($part in $Uri.Query.TrimStart('?').Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
        $pair = $part.Split('=', 2)
        if ([Uri]::UnescapeDataString($pair[0]) -ceq $Name) {
            if ($pair.Count -eq 2) { return [Uri]::UnescapeDataString($pair[1]) }
            return ''
        }
    }
    return $null
}

function Remove-TransientFile([string] $Path) {
    if (-not [String]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

function Get-OptionalObjectProperty([object] $InputObject, [string] $Name) {
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-RequiredPositiveIntProperty([object] $InputObject, [string] $Name) {
    $value = Get-OptionalObjectProperty $InputObject $Name
    try { $parsed = [Convert]::ToInt32($value, [Globalization.CultureInfo]::InvariantCulture) }
    catch { throw "C-Sweet did not return a valid Office allocation for $Name." }
    if ($parsed -le 0) { throw "C-Sweet did not return a valid Office allocation for $Name." }
    return $parsed
}

function Invoke-CSweetPinnedRestMethod {
    param(
        [Parameter(Mandatory = $true)][string] $Method,
        [Parameter(Mandatory = $true)][uri] $Uri,
        [Parameter(Mandatory = $true)][string] $Body
    )

    if ($null -eq $originUri -or
        $Uri.GetLeftPart([UriPartial]::Authority) -ine $originUri.GetLeftPart([UriPartial]::Authority)) {
        throw 'A pinned Office setup request targeted an unexpected origin.'
    }

    $script:controlPlaneRequestFailed = $false
    $previousCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    try {
        if (-not [String]::IsNullOrWhiteSpace($expectedControlPlaneCertificateSha256)) {
            $expectedCertificateSha256 = $expectedControlPlaneCertificateSha256
            [Net.ServicePointManager]::ServerCertificateValidationCallback = {
                param($sender, $certificate, $chain, $errors)
                if ($null -eq $certificate) { return $false }
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $sha256.ComputeHash($certificate.GetRawCertData())).Replace('-', '').ToLowerInvariant()
                    return $actual -ceq $expectedCertificateSha256
                }
                finally { $sha256.Dispose() }
            }.GetNewClosure()
        }

        $response = Invoke-RestMethod -Method $Method -Uri $Uri -ContentType 'application/json' `
            -Body $Body -TimeoutSec 30 -UseBasicParsing
        return $response
    }
    catch {
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        $script:controlPlaneRequestFailed = $null -eq $responseProperty -or $null -eq $responseProperty.Value
        if (-not $script:controlPlaneRequestFailed) {
            $problem = $null
            try {
                $bodyText = $_.ErrorDetails.Message
                if ([string]::IsNullOrWhiteSpace($bodyText)) {
                    $reader = [IO.StreamReader]::new($responseProperty.Value.GetResponseStream())
                    try { $bodyText = $reader.ReadToEnd() } finally { $reader.Dispose() }
                }
                $problem = $bodyText | ConvertFrom-Json
            } catch { }
            if ($null -ne $problem -and $null -ne $problem.PSObject.Properties['message']) {
                throw ("Office request {0} was rejected: {1}" -f $Uri.AbsolutePath, [string]$problem.message)
            }
        }
        throw
    }
    finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The C-Sweet development Office launcher must run with administrator approval.'
}

$tokenPath = $null
$progressPath = $null
$progressHelperLoaded = $false
$sessionId = [guid]::Empty
$origin = $null
$originUri = $null
$architecture = $null
$redemption = $null
$expectedControlPlaneCertificateSha256 = $null
$controlPlaneRequestFailed = $false
$release = $null
$prebuiltRoot = $null
try {
    $handoff = [IO.File]::ReadAllText($HandoffInputPath, [Text.Encoding]::UTF8).Trim()
    Remove-TransientFile $HandoffInputPath
    $uri = [uri]$handoff
    if ($uri.Scheme -cne 'csweet-office' -or $uri.Host -cne 'enroll' -or $uri.AbsolutePath -cne '/v1') {
        throw 'The C-Sweet Office setup handoff is invalid.'
    }
    $sessionId = [guid](Get-QueryValue $uri 'session')
    $origin = Get-QueryValue $uri 'origin'
    $handoffCertificateSha256 = Get-QueryValue $uri 'certificate'
    $fragment = $uri.Fragment.TrimStart('#')
    if (-not $fragment.StartsWith('handoff=', [StringComparison]::Ordinal)) {
        throw 'The C-Sweet Office setup handoff has no one-use authorization.'
    }
    $handoffSecret = [Uri]::UnescapeDataString($fragment.Substring('handoff='.Length))
    if (-not [Uri]::TryCreate($origin, [UriKind]::Absolute, [ref]$originUri) -or
        -not ($originUri.Scheme -ceq 'https' -or ($originUri.Scheme -ceq 'http' -and $originUri.IsLoopback)) -or
        [String]::IsNullOrWhiteSpace($handoffSecret) -or $handoffSecret.Length -gt 256) {
        throw 'The C-Sweet Office setup handoff is incomplete.'
    }

    $architecture = switch ($env:PROCESSOR_ARCHITECTURE) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { throw 'This Windows architecture is not supported by C-Sweet Office.' }
    }
    $setupRoot = Join-Path $env:ProgramData 'CSweet\Setup'
    New-Item -ItemType Directory -Path $setupRoot -Force | Out-Null
    $progressPath = Join-Path $setupRoot "windows-isolation-$($sessionId.ToString('N')).json"
    # Protect downloaded scripts before executing them with this setup session's UAC approval.
    & "$env:SystemRoot\System32\icacls.exe" $setupRoot '/inheritance:r' '/grant:r' `
        "*$($identity.User.Value):(OI)(CI)R" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Office download directory could not be protected.' }
    . (Join-Path $PSScriptRoot 'CSweet.OfficeRelease.ps1')
    if (-not $UseLocalSource) { $release = Resolve-CSweetOfficeRelease -Architecture $architecture }
    if ($null -ne $release) {
        # This small support asset supplies the preflight scripts. The large image is fetched only AFTER
        # redemption, so a slow image download cannot consume the five-minute one-use handoff lifetime.
        $supportRoot = Receive-CSweetOfficeAsset -Asset $release.Support -TimeoutSeconds 60 `
            -Destination (Join-Path $setupRoot "office-support-$([guid]::NewGuid().ToString('N'))")
        $officeScriptRoot = Join-Path $supportRoot 'scripts\windows'
        $OfficeBootstrapScript = Join-Path $officeScriptRoot 'Initialize-CSweetWindowsIsolationTest.ps1'
    } elseif ($OfficeBootstrapScript -and (Test-Path -LiteralPath $OfficeBootstrapScript -PathType Leaf)) {
        $officeScriptRoot = Split-Path -Parent $OfficeBootstrapScript
        Write-Host 'No compatible published Office bundle is available. Using the configured local Office source.'
    } else {
        throw 'No compatible Office release could be found and no local Office source is configured. Connect to GitHub or provide a local CSweet.Office checkout and retry.'
    }
    $progressHelper = Join-Path $officeScriptRoot 'CSweet.WindowsSetupProgress.ps1'
    . $progressHelper
    $progressHelperLoaded = $true
    $progressPath = Initialize-CSweetSetupProgress -Path $progressPath -JobId $sessionId `
        -ControlPlaneUserSid $identity.User.Value
    Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey connect-control-plane -PhaseDisplayName 'Connecting to C-Sweet' `
        -Message 'Administrator approval was received. Windows setup is connecting securely to C-Sweet.' `
        -PercentComplete 0 -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 45

    if ($originUri.Scheme -ceq 'https' -and -not [String]::IsNullOrWhiteSpace($handoffCertificateSha256)) {
        $expectedControlPlaneCertificateSha256 = $handoffCertificateSha256.Trim().Replace(':', '').Replace('-', '').ToLowerInvariant()
        if ($expectedControlPlaneCertificateSha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'The C-Sweet Office setup handoff contains an invalid certificate fingerprint.'
        }
    }

    $upgradeOfficeText = Get-QueryValue $uri 'office'
    $operation = Get-QueryValue $uri 'operation'
    $isUpgrade = -not [String]::IsNullOrWhiteSpace($upgradeOfficeText) -and $operation -in @('upgrade', 'repair')
    if (-not [String]::IsNullOrWhiteSpace($upgradeOfficeText)) {
        $expectedOfficeId = [guid]$upgradeOfficeText
        $statePath = Join-Path $env:ProgramData 'CSweet\Office\node\node-state.json'
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'The selected Office identity is unavailable.' }
        $installedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ([guid]$installedState.OfficeId -ne $expectedOfficeId) { throw 'The installed Office does not match the Office selected in Headquarters.' }
    }

    $recoveryProbe = Join-Path $officeScriptRoot 'Get-CSweetOfficeRecoveryState.ps1'
    $existingInstallationState = if (Test-Path -LiteralPath $recoveryProbe -PathType Leaf) {
        [string](& $recoveryProbe -ForUpgrade:($isUpgrade -and $operation -eq 'upgrade'))
    } else { 'unsafe' }
    if ($existingInstallationState -notin @('none', 'clean', 'active', 'unsafe')) {
        $existingInstallationState = 'unsafe'
    }
    $preflightRequest = @{
        handoffSecret = $handoffSecret
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = $architecture
        officeVersion = if ($null -ne $release) { $release.Version } else { '0.5.0' }
        existingInstallationState = $existingInstallationState
    } | ConvertTo-Json -Compress
    $preflight = $null
    try {
        $preflight = Invoke-CSweetPinnedRestMethod -Method Post `
            -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/preflight') -Body $preflightRequest
    }
    catch {
        if ($null -ne $_.ErrorDetails -and -not [String]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            try { $preflight = $_.ErrorDetails.Message | ConvertFrom-Json } catch { }
        }
        if ($null -eq $preflight) { throw }
    }
    if (-not [bool]$preflight.succeeded) {
        $preflightMessage = [string]$preflight.message
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' -State failed -PhaseKey preflight -PhaseDisplayName 'Office update could not start' -Message $preflightMessage -PercentComplete 0 -ErrorCode ([string]$preflight.errorCode) -ErrorMessage $preflightMessage
        throw $preflightMessage
    }
    if ([guid]$preflight.assistedSetupSessionId -ne $sessionId) {
        throw 'C-Sweet returned a mismatched Office setup session.'
    }
    if ([string]$preflight.existingInstallationAction -ceq 'remove') {
        $uninstaller = Join-Path $officeScriptRoot 'Uninstall-CSweetOffice.ps1'
        $removalCompleted = $false
        try {
            if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'The Office uninstaller is unavailable.' }
            Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                -State running -PhaseKey remove-preflight -PhaseDisplayName 'Preparing Office removal' `
                -Message 'Windows is preparing to remove the existing Office services, data, and virtual machines.' `
                -PercentComplete 1 -EstimatedRemainingMinimumSeconds 10 -EstimatedRemainingMaximumSeconds 180
            & $uninstaller -Force -Elevated -ProgressPath $progressPath -ProgressJobId $sessionId `
                -ProgressWorkflow 'developer-bootstrap'
            if ($LASTEXITCODE -ne 0) { throw "Office removal exited with code $LASTEXITCODE." }
            $removalRequest = @{
                handoffSecret = $handoffSecret
                machineName = [Environment]::MachineName
                operatingSystem = 'windows'
                architecture = $architecture
            } | ConvertTo-Json -Compress
            Invoke-CSweetPinnedRestMethod -Method Post `
                -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/removal-complete') `
                -Body $removalRequest | Out-Null
            $removalCompleted = $true
            Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                -State running -PhaseKey removal-complete -PhaseDisplayName 'Starting your fresh Office' `
                -Message 'The old Office was removed. C-Sweet is continuing automatically with the capacity you selected.' `
                -PercentComplete 1 -EstimatedRemainingMinimumSeconds 1200 -EstimatedRemainingMaximumSeconds 3000

            $preflightRequest = @{
                handoffSecret = $handoffSecret
                machineName = [Environment]::MachineName
                operatingSystem = 'windows'
                architecture = $architecture
                officeVersion = if ($null -ne $release) { $release.Version } else { '0.5.0' }
                existingInstallationState = 'none'
            } | ConvertTo-Json -Compress
            $preflight = Invoke-CSweetPinnedRestMethod -Method Post `
                -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/preflight') -Body $preflightRequest
            if (-not [bool]$preflight.proceedToRedemption) {
                throw 'C-Sweet could not continue fresh Office installation after removal.'
            }
        }
        catch {
            if ($removalCompleted) {
                Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                    -State failed -PhaseKey fresh-install-start-failed `
                    -PhaseDisplayName 'Fresh Office installation needs attention' `
                    -Message 'The old Office was removed, but C-Sweet could not start the fresh installation.' `
                    -PercentComplete 0 -ErrorCode 'office_setup_failed' -ErrorMessage $_.Exception.Message
                throw
            }
            $removalReceipt = [string](Get-OptionalObjectProperty $preflight 'setupReceipt')
            if (-not [String]::IsNullOrWhiteSpace($removalReceipt)) {
                $removalFailure = @{
                    assistedSetupSessionId = $sessionId
                    setupReceipt = $removalReceipt
                    resultCode = 'office_removal_failed'
                    machineName = [Environment]::MachineName
                    operatingSystem = 'windows'
                    architecture = $architecture
                } | ConvertTo-Json -Compress
                try {
                    Invoke-CSweetPinnedRestMethod -Method Post `
                        -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/result') `
                        -Body $removalFailure | Out-Null
                } catch { }
            }
            Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                -State failed -PhaseKey office-removal-failed -PhaseDisplayName 'Office removal needs attention' `
                -Message 'C-Sweet could not completely remove the existing Office.' -PercentComplete 0 `
                -ErrorCode 'office_removal_failed' -ErrorMessage $_.Exception.Message
            throw
        }
    }
    if (-not [bool]$preflight.proceedToRedemption) {
        $preflightCode = [string]$preflight.errorCode
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
            -State failed -PhaseKey existing-office -PhaseDisplayName 'Existing Office found' `
            -Message ([string]$preflight.message) -PercentComplete 0 `
            -ErrorCode $preflightCode -ErrorMessage ([string]$preflight.message)
        return
    }

    $request = @{
        handoffSecret = $handoffSecret
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = $architecture
        officeVersion = if ($null -ne $release) { $release.Version } else { '0.5.0' }
    } | ConvertTo-Json -Compress
    $redemption = Invoke-CSweetPinnedRestMethod -Method Post `
        -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/redeem') -Body $request
    if (-not $redemption.succeeded -or (-not $isUpgrade -and [String]::IsNullOrWhiteSpace([string]$redemption.enrollmentToken))) {
        throw 'C-Sweet could not authorize the local Office setup session.'
    }
    if ([String]::IsNullOrWhiteSpace([string]$redemption.controlPlaneUrl)) {
        throw 'C-Sweet did not return the Office control-plane address.'
    }
    $redeemedCertificateSha256 = [string](Get-OptionalObjectProperty $redemption 'controlPlaneCertificateSha256')
    if (-not [String]::IsNullOrWhiteSpace($handoffCertificateSha256) -and
        -not [String]::IsNullOrWhiteSpace($redeemedCertificateSha256) -and
        $handoffCertificateSha256.Trim().Replace(':', '').Replace('-', '') -ine
            $redeemedCertificateSha256.Trim().Replace(':', '').Replace('-', '')) {
        throw 'The control-plane certificate fingerprint changed during assisted setup.'
    }
    $controlPlaneCertificateSha256 = if (-not [String]::IsNullOrWhiteSpace($redeemedCertificateSha256)) {
        $redeemedCertificateSha256
    } else {
        $handoffCertificateSha256
    }
    $allocatableCpuCount = Get-RequiredPositiveIntProperty $redemption 'allocatableCpuCount'
    $allocatableMemoryMb = Get-RequiredPositiveIntProperty $redemption 'allocatableMemoryMb'
    $allocatableDiskMb = Get-RequiredPositiveIntProperty $redemption 'allocatableDiskMb'
    $maximumConcurrentWorkloads = Get-RequiredPositiveIntProperty $redemption 'maximumConcurrentWorkloads'

    if ($isUpgrade -and [string]$redemption.existingInstallationAction -notin @('upgrade', 'repair')) {
        throw 'Headquarters did not authorize the selected Office upgrade.'
    }
    if (-not $isUpgrade) {
        $tokenPath = Join-Path $setupRoot "office-enrollment-$($sessionId.ToString('N')).secret"
        [IO.File]::WriteAllText($tokenPath, [string]$redemption.enrollmentToken, [Text.UTF8Encoding]::new($false))
        & "$env:SystemRoot\System32\icacls.exe" $tokenPath '/inheritance:r' `
            "/grant:r" "*$($identity.User.Value):F" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Office enrollment handoff could not be protected.' }
    }
    $operation = Get-QueryValue $uri 'operation'
    if ($isUpgrade -and $operation -eq 'repair') {
        $maintenanceScript = Join-Path $officeScriptRoot 'Enter-CSweetOfficeMaintenance.ps1'
        & $maintenanceScript -OfficeId $expectedOfficeId | Out-Null
    }

    $installerAction = if ($isUpgrade) { 'upgrade' } else { [string]$redemption.existingInstallationAction }

    Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey start-bootstrap -PhaseDisplayName 'Starting secure runtime preparation' `
        -Message 'Administrator approval was received. C-Sweet is starting the Windows and Hyper-V checks.' `
        -PercentComplete 1 -EstimatedRemainingMinimumSeconds 1200 -EstimatedRemainingMaximumSeconds 3000
    try {
        if ($null -ne $release) {
            Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                -State running -PhaseKey download-office -PhaseDisplayName 'Downloading Office' `
                -Message "Downloading prebuilt Office $($release.Version). The downloaded image will be checked on this machine." `
                -PercentComplete 5
            # Hyper-V appends VM names and GUIDs beneath this bundle during certification.
            # Keep its unique staging name short enough for Hyper-V's legacy path limits.
            $prebuiltRoot = Receive-CSweetOfficeAsset -Asset $release.Bundle `
                -Destination (Join-Path $setupRoot "b-$([guid]::NewGuid().ToString('N').Substring(0, 16))")
            $officeScriptRoot = Join-Path $prebuiltRoot 'scripts\windows'
            $OfficeBootstrapScript = Join-Path $officeScriptRoot 'Initialize-CSweetWindowsIsolationTest.ps1'
        }
        $officeRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $officeScriptRoot '..\..'))
        $certificationRoot = Join-Path $officeRepositoryRoot 'artifacts\windows-test'
        $payloadResultPath = Join-Path $setupRoot "office-payload-$($sessionId.ToString('N')).txt"
        Remove-TransientFile $payloadResultPath
        $bootstrapArguments = @{}
        if ($prebuiltRoot) { $bootstrapArguments.PrebuiltRoot = $prebuiltRoot }
        & $OfficeBootstrapScript @bootstrapArguments -ControlPlaneUserSid $identity.User.Value `
            -ControlPlaneUrl ([string]$redemption.controlPlaneUrl) `
            -ProgressPath $progressPath -ProgressJobId $sessionId -NoElevation -SkipInstall -PayloadResultPath $payloadResultPath
        if ($LASTEXITCODE -ne 0) { throw "Secure VM runtime setup exited with code $LASTEXITCODE." }

        $preparationProgress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
        if ([string]$preparationProgress.state -ceq 'restart-required') { return }
        if (-not (Test-Path -LiteralPath $payloadResultPath -PathType Leaf)) {
            throw 'The Office build did not return a completed payload.'
        }
        $payloadRoot = [IO.Path]::GetFullPath([IO.File]::ReadAllText($payloadResultPath).Trim())
        Remove-TransientFile $payloadResultPath
        $allowedRoot = [IO.Path]::GetFullPath($certificationRoot).TrimEnd('\') + '\'
        if (-not $payloadRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath (Join-Path $payloadRoot 'runtime-manifest.json') -PathType Leaf)) {
            throw 'The Office build returned an invalid completed payload.'
        }
        # Reuse this setup session's UAC approval and progress channel. Image preparation
        # is an application step, never a command the user has to run.
        # Compute has its own on-demand preparation in Install-ComputeLocalProvider.ps1.
        # Preserve eager preparation for source developers; published Office setup needs no sibling tool checkout.
        if (-not $prebuiltRoot) {
            $headquartersRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
            $computePreparation = Join-Path $headquartersRoot 'scripts\Ensure-ComputeLinuxImage.ps1'
            if (-not (Test-Path -LiteralPath $computePreparation -PathType Leaf)) {
                throw 'This C-Sweet build is missing its Linux preparation component. Update C-Sweet and retry setup.'
            }
            & $computePreparation -ProgressPath $progressPath -ProgressJobId $sessionId `
                -ProgressHelperPath $progressHelper | Out-Host
        }
        $officeInstaller = Join-Path $officeScriptRoot 'Install-CSweetOfficeRuntimeHost.ps1'
        if (-not (Test-Path -LiteralPath $officeInstaller -PathType Leaf)) {
            throw 'The C-Sweet Office installer is unavailable.'
        }
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
            -State running -PhaseKey install-office -PhaseDisplayName 'Applying your Office capacity' `
            -Message 'C-Sweet is installing the Office services with the CPU, memory, and storage you selected.' `
            -PercentComplete 95 -EstimatedRemainingMinimumSeconds 15 -EstimatedRemainingMaximumSeconds 120
        if (-not $isUpgrade) {
            $readyRequest = @{
                assistedSetupSessionId = $sessionId; setupReceipt = [string]$redemption.setupReceipt
                machineName = [Environment]::MachineName; operatingSystem = 'windows'; architecture = $architecture
            } | ConvertTo-Json -Compress
            Invoke-CSweetPinnedRestMethod -Method Post `
                -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/enrollment-ready') -Body $readyRequest | Out-Null
        }
        & $officeInstaller -PayloadRoot $payloadRoot -ControlPlaneUserSid $identity.User.Value `
            -ControlPlaneUrl ([string]$redemption.controlPlaneUrl) `
            -ControlPlaneCertificateSha256 $controlPlaneCertificateSha256 `
            -EnrollmentTokenInputPath $tokenPath -AssistedSetupSessionId $sessionId `
            -AllocatableCpuCount $allocatableCpuCount -AllocatableMemoryMb $allocatableMemoryMb `
            -AllocatableDiskMb $allocatableDiskMb -MaximumConcurrentWorkloads $maximumConcurrentWorkloads `
            -ExistingInstallationAction $installerAction `
            -ProgressPath $progressPath -ProgressJobId $sessionId -ProgressWorkflow 'developer-bootstrap' `
            -NonInteractive
        if ($LASTEXITCODE -ne 0) { throw "Office installation exited with code $LASTEXITCODE." }
        if ($isUpgrade) {
            $completed = @{
                assistedSetupSessionId = $sessionId
                setupReceipt = [string]$redemption.setupReceipt
                resultCode = 'office_upgrade_completed'
                machineName = [Environment]::MachineName
                operatingSystem = 'windows'
                architecture = $architecture
            } | ConvertTo-Json -Compress
            Invoke-CSweetPinnedRestMethod -Method Post `
                -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/result') -Body $completed | Out-Null
        }

    }
    catch {
        $failureMessage = $_.Exception.Message
        $reportedCode = 'office_setup_failed'
        $bootstrapFailureAlreadyReported = $false
        if (-not [String]::IsNullOrWhiteSpace($progressPath) -and
            (Test-Path -LiteralPath $progressPath -PathType Leaf)) {
            try {
                $reportedProgress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
                if ([guid]$reportedProgress.jobId -eq $sessionId) {
                    $bootstrapFailureAlreadyReported = [string]$reportedProgress.state -ceq 'failed'
                    if ([string]$reportedProgress.errorCode -in @('existing_office_detected', 'existing_office_active', 'reconnect_unsafe')) {
                        $reportedCode = [string]$reportedProgress.errorCode
                    }
                }
            } catch { }
        }
        $setupReceipt = [string](Get-OptionalObjectProperty $redemption 'setupReceipt')
        if (-not [String]::IsNullOrWhiteSpace($setupReceipt)) {
            $resultRequest = @{
                assistedSetupSessionId = $sessionId
                setupReceipt = $setupReceipt
                resultCode = $reportedCode
                machineName = [Environment]::MachineName
                operatingSystem = 'windows'
                architecture = $architecture
            } | ConvertTo-Json -Compress
            try {
                Invoke-CSweetPinnedRestMethod -Method Post `
                    -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/result') `
                    -Body $resultRequest | Out-Null
            } catch { }
        }
        if ($progressHelperLoaded -and $sessionId -ne [guid]::Empty -and
            -not [String]::IsNullOrWhiteSpace($progressPath) -and -not $bootstrapFailureAlreadyReported) {
            try {
                Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                    -State failed -PhaseKey setup-paused -PhaseDisplayName 'Windows setup could not continue' `
                    -Message 'Windows setup started, but could not continue. Review the error below and try again.' `
                    -PercentComplete 0 -ErrorCode $reportedCode -ErrorMessage $failureMessage
            } catch { }
        }
        throw
    }
}
catch {
    $failureMessage = $_.Exception.Message
    if (-not $progressHelperLoaded -and $progressPath -and $sessionId -ne [guid]::Empty) {
        @{ schemaVersion = 1; jobId = $sessionId; workflow = 'developer-bootstrap'; state = 'failed';
            phaseKey = 'resolve-office'; phaseDisplayName = 'Office download needs attention';
            message = 'Office could not be prepared. Check the release source or local checkout and retry.';
            percentComplete = 0; errorCode = 'office_setup_failed'; errorMessage = $failureMessage;
            startedAt = [DateTimeOffset]::UtcNow.ToString('O'); updatedAt = [DateTimeOffset]::UtcNow.ToString('O'); ownerProcessId = $PID
        } | ConvertTo-Json | Set-Content -LiteralPath $progressPath -Encoding UTF8
    }
    if ($progressHelperLoaded -and $sessionId -ne [guid]::Empty -and
        -not [String]::IsNullOrWhiteSpace($progressPath)) {
        try {
            $failureAlreadyReported = $false
            if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
                try {
                    $existingProgress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
                    $failureAlreadyReported = [string]$existingProgress.state -ceq 'failed'
                } catch { }
            }
            if (-not $failureAlreadyReported) {
                $failurePhaseName = 'Windows setup could not continue'
                $failureUserMessage = 'Windows setup started, but could not continue. Review the error below and try again.'
                if ($controlPlaneRequestFailed) {
                    $failurePhaseName = 'Secure connection to C-Sweet failed'
                    $failureUserMessage = 'Windows could not establish the certificate-pinned connection to C-Sweet. Restart C-Sweet and try again.'
                }
                Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                    -State failed -PhaseKey setup-paused -PhaseDisplayName $failurePhaseName `
                    -Message $failureUserMessage -PercentComplete 0 `
                    -ErrorCode 'office_setup_failed' -ErrorMessage $failureMessage
            }
        } catch { }
    }
    throw
}
finally {
    Remove-TransientFile $HandoffInputPath
    Remove-TransientFile $tokenPath
}
