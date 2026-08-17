[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $HandoffInputPath,
    [Parameter(Mandatory = $true)][string] $OfficeBootstrapScript
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

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The C-Sweet development Office launcher must run with administrator approval.'
}
if (-not (Test-Path -LiteralPath $OfficeBootstrapScript -PathType Leaf)) {
    throw 'The C-Sweet Office development bootstrap script is unavailable.'
}

$tokenPath = $null
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
    if ([String]::IsNullOrWhiteSpace($origin) -or -not $origin.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -or
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
    $officeScriptRoot = Split-Path -Parent $OfficeBootstrapScript
    $progressHelper = Join-Path $officeScriptRoot 'CSweet.WindowsSetupProgress.ps1'
    . $progressHelper
    $progressPath = Initialize-CSweetSetupProgress -Path $progressPath -JobId $sessionId `
        -ControlPlaneUserSid $identity.User.Value

    $recoveryProbe = Join-Path $officeScriptRoot 'Get-CSweetOfficeRecoveryState.ps1'
    $existingInstallationState = if (Test-Path -LiteralPath $recoveryProbe -PathType Leaf) {
        [string](& $recoveryProbe)
    } else { 'unsafe' }
    if ($existingInstallationState -notin @('none', 'clean', 'active', 'unsafe')) {
        $existingInstallationState = 'unsafe'
    }
    $preflightRequest = @{
        handoffSecret = $handoffSecret
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = $architecture
        officeVersion = '0.3.0'
        existingInstallationState = $existingInstallationState
    } | ConvertTo-Json -Compress
    $preflight = $null
    try {
        $preflight = Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/preflight') `
            -ContentType 'application/json' -Body $preflightRequest -TimeoutSec 30 -UseBasicParsing
    }
    catch {
        if ($null -ne $_.ErrorDetails -and -not [String]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            try { $preflight = $_.ErrorDetails.Message | ConvertFrom-Json } catch { }
        }
        if ($null -eq $preflight) { throw }
    }
    if ([guid]$preflight.assistedSetupSessionId -ne $sessionId) {
        throw 'C-Sweet returned a mismatched Office setup session.'
    }
    if ([string]$preflight.existingInstallationAction -ceq 'remove') {
        $uninstaller = Join-Path $officeScriptRoot 'Uninstall-CSweetOffice.ps1'
        try {
            if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'The Office uninstaller is unavailable.' }
            & $uninstaller -Force -Elevated
            if ($LASTEXITCODE -ne 0) { throw "Office removal exited with code $LASTEXITCODE." }
            $removalRequest = @{
                handoffSecret = $handoffSecret
                machineName = [Environment]::MachineName
                operatingSystem = 'windows'
                architecture = $architecture
            } | ConvertTo-Json -Compress
            Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/removal-complete') `
                -ContentType 'application/json' -Body $removalRequest -TimeoutSec 30 -UseBasicParsing | Out-Null
            Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
                -State completed -PhaseKey office-removed -PhaseDisplayName 'Office removed' `
                -Message 'The stale Office was removed. Return to C-Sweet to install a new Office.' -PercentComplete 100
            return
        }
        catch {
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
                    Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/result') `
                        -ContentType 'application/json' -Body $removalFailure -TimeoutSec 30 -UseBasicParsing | Out-Null
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
        officeVersion = '0.3.0'
    } | ConvertTo-Json -Compress
    $redemption = Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/redeem') `
        -ContentType 'application/json' -Body $request -TimeoutSec 30 -UseBasicParsing
    if (-not $redemption.succeeded -or [String]::IsNullOrWhiteSpace([string]$redemption.enrollmentToken)) {
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

    $tokenPath = Join-Path $setupRoot "office-enrollment-$($sessionId.ToString('N')).secret"
    [IO.File]::WriteAllText($tokenPath, [string]$redemption.enrollmentToken, [Text.UTF8Encoding]::new($false))
    & "$env:SystemRoot\System32\icacls.exe" $tokenPath '/inheritance:r' `
        "/grant:r" "*$($identity.User.Value):F" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Office enrollment handoff could not be protected.' }

    Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey start-bootstrap -PhaseDisplayName 'Starting secure runtime preparation' `
        -Message 'Administrator approval was received. C-Sweet is starting the Windows and Hyper-V checks.' `
        -PercentComplete 1 -EstimatedRemainingMinimumSeconds 1200 -EstimatedRemainingMaximumSeconds 3000
    try {
        $officeRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $officeScriptRoot '..\..'))
        $certificationRoot = Join-Path $officeRepositoryRoot 'artifacts\windows-test'
        $bootstrapStartedAt = Get-Date
        & $OfficeBootstrapScript -ControlPlaneUserSid $identity.User.Value `
            -ControlPlaneUrl ([string]$redemption.controlPlaneUrl) `
            -ProgressPath $progressPath -ProgressJobId $sessionId -NoElevation -SkipInstall
        if ($LASTEXITCODE -ne 0) { throw "Secure VM runtime setup exited with code $LASTEXITCODE." }

        $payloadRoot = Get-ChildItem -LiteralPath $certificationRoot -Directory -ErrorAction Stop |
            Where-Object { $_.LastWriteTime -ge $bootstrapStartedAt.AddMinutes(-1) } |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName 'payload' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
            Select-Object -First 1
        if ([String]::IsNullOrWhiteSpace([string]$payloadRoot)) {
            throw 'The certified Office payload was not created.'
        }
        $officeInstaller = Join-Path $officeScriptRoot 'Install-CSweetOfficeRuntimeHost.ps1'
        if (-not (Test-Path -LiteralPath $officeInstaller -PathType Leaf)) {
            throw 'The C-Sweet Office installer is unavailable.'
        }
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
            -State running -PhaseKey install-office -PhaseDisplayName 'Applying your Office capacity' `
            -Message 'C-Sweet is installing the Office services with the CPU, memory, and storage you selected.' `
            -PercentComplete 95 -EstimatedRemainingMinimumSeconds 15 -EstimatedRemainingMaximumSeconds 120
        & $officeInstaller -PayloadRoot $payloadRoot -ControlPlaneUserSid $identity.User.Value `
            -ControlPlaneUrl ([string]$redemption.controlPlaneUrl) `
            -ControlPlaneCertificateSha256 $controlPlaneCertificateSha256 `
            -EnrollmentTokenInputPath $tokenPath -AssistedSetupSessionId $sessionId `
            -AllocatableCpuCount $allocatableCpuCount -AllocatableMemoryMb $allocatableMemoryMb `
            -AllocatableDiskMb $allocatableDiskMb -MaximumConcurrentWorkloads $maximumConcurrentWorkloads `
            -ExistingInstallationAction ([string]$redemption.existingInstallationAction) `
            -ProgressPath $progressPath -ProgressJobId $sessionId -ProgressWorkflow 'developer-bootstrap' `
            -NonInteractive
        if ($LASTEXITCODE -ne 0) { throw "Office installation exited with code $LASTEXITCODE." }
    }
    catch {
        $reportedCode = 'office_setup_failed'
        if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
            try {
                $reportedProgress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
                if ([string]$reportedProgress.errorCode -in @('existing_office_detected', 'existing_office_active', 'reconnect_unsafe')) {
                    $reportedCode = [string]$reportedProgress.errorCode
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
                Invoke-RestMethod -Method Post -Uri ($origin.TrimEnd('/') + '/api/offices/local-sessions/result') `
                    -ContentType 'application/json' -Body $resultRequest -TimeoutSec 30 -UseBasicParsing | Out-Null
            } catch { }
        }
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
            -State failed -PhaseKey setup-paused -PhaseDisplayName 'Secure runtime preparation stopped' `
            -Message 'Windows setup stopped before the secure VM runtime was ready.' -PercentComplete 0 `
            -ErrorCode $reportedCode -ErrorMessage $_.Exception.Message
        throw
    }
}
finally {
    Remove-TransientFile $HandoffInputPath
    Remove-TransientFile $tokenPath
}
