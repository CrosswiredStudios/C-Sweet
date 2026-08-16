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
    $request = @{
        handoffSecret = $handoffSecret
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = $architecture
        officeVersion = '0.1.0'
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

    $setupRoot = Join-Path $env:ProgramData 'CSweet\Setup'
    New-Item -ItemType Directory -Path $setupRoot -Force | Out-Null
    $tokenPath = Join-Path $setupRoot "office-enrollment-$($sessionId.ToString('N')).secret"
    [IO.File]::WriteAllText($tokenPath, [string]$redemption.enrollmentToken, [Text.UTF8Encoding]::new($false))
    & "$env:SystemRoot\System32\icacls.exe" $tokenPath '/inheritance:r' `
        "/grant:r" "*$($identity.User.Value):F" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Office enrollment handoff could not be protected.' }

    $progressPath = Join-Path $setupRoot "windows-isolation-$($sessionId.ToString('N')).json"
    $progressHelper = Join-Path (Split-Path -Parent $OfficeBootstrapScript) 'CSweet.WindowsSetupProgress.ps1'
    . $progressHelper
    $progressPath = Initialize-CSweetSetupProgress -Path $progressPath -JobId $sessionId `
        -ControlPlaneUserSid $identity.User.Value
    Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey start-bootstrap -PhaseDisplayName 'Starting secure runtime preparation' `
        -Message 'Administrator approval was received. C-Sweet is starting the Windows and Hyper-V checks.' `
        -PercentComplete 1 -EstimatedRemainingMinimumSeconds 1200 -EstimatedRemainingMaximumSeconds 3000
    try {
        & $OfficeBootstrapScript -ControlPlaneUserSid $identity.User.Value `
            -ControlPlaneUrl ([string]$redemption.controlPlaneUrl) `
            -ControlPlaneCertificateSha256 $controlPlaneCertificateSha256 `
            -EnrollmentTokenInputPath $tokenPath -ProgressPath $progressPath -ProgressJobId $sessionId -NoElevation
        if ($LASTEXITCODE -ne 0) { throw "Secure VM runtime setup exited with code $LASTEXITCODE." }
    }
    catch {
        Write-CSweetSetupProgress -Path $progressPath -JobId $sessionId -Workflow 'developer-bootstrap' `
            -State failed -PhaseKey setup-paused -PhaseDisplayName 'Secure runtime preparation stopped' `
            -Message 'Windows setup stopped before the secure VM runtime was ready.' -PercentComplete 0 `
            -ErrorCode 'development-bootstrap-failed' -ErrorMessage $_.Exception.Message
        throw
    }
}
finally {
    Remove-TransientFile $HandoffInputPath
    Remove-TransientFile $tokenPath
}
