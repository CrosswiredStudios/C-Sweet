Set-StrictMode -Version Latest

function Resolve-CSweetSetupProgressPath {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][guid] $JobId)
    $root = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSweet\Setup'))
    $full = [IO.Path]::GetFullPath($Path)
    $expected = Join-Path $root "windows-isolation-$($JobId.ToString('N')).json"
    if (-not $full.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Windows setup progress path is outside the protected C-Sweet setup directory.'
    }
    return $full
}

function Initialize-CSweetSetupProgress {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][guid] $JobId,
        [Parameter(Mandatory = $true)][string] $ControlPlaneUserSid
    )
    $resolved = Resolve-CSweetSetupProgressPath -Path $Path -JobId $JobId
    $root = [IO.Path]::GetDirectoryName($resolved)
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    & "$env:SystemRoot\System32\icacls.exe" $root '/inheritance:r' "/grant:r" `
        "*$ControlPlaneUserSid`:(OI)(CI)R" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Windows setup progress directory ACL could not be secured.' }
    return $resolved
}

function Write-CSweetSetupProgress {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][guid] $JobId,
        [Parameter(Mandatory = $true)][string] $Workflow,
        [Parameter(Mandatory = $true)][ValidateSet('running', 'restart-required', 'completed', 'failed')][string] $State,
        [Parameter(Mandatory = $true)][string] $PhaseKey,
        [Parameter(Mandatory = $true)][string] $PhaseDisplayName,
        [Parameter(Mandatory = $true)][string] $Message,
        [Parameter(Mandatory = $true)][ValidateRange(0, 100)][int] $PercentComplete,
        [Nullable[int]] $EstimatedRemainingMinimumSeconds,
        [Nullable[int]] $EstimatedRemainingMaximumSeconds,
        [switch] $RequiresRestart,
        [string] $ErrorCode,
        [string] $ErrorMessage,
        [ValidateRange(1, 2147483647)][int] $OwnerProcessId = $PID
    )
    $resolved = Resolve-CSweetSetupProgressPath -Path $Path -JobId $JobId
    $startedAt = [DateTimeOffset]::UtcNow
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        try {
            $existing = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
            if ([guid]$existing.jobId -eq $JobId) { $startedAt = [DateTimeOffset]$existing.startedAt }
        } catch { }
    }
    $clean = {
        param([string] $Value, [int] $Maximum)
        if ([String]::IsNullOrWhiteSpace($Value)) { return '' }
        return -join @($Value.ToCharArray() | Where-Object { -not [char]::IsControl($_) } | Select-Object -First $Maximum)
    }
    $document = [ordered]@{
        schemaVersion = 1
        jobId = $JobId
        workflow = & $clean $Workflow 64
        state = $State
        phaseKey = & $clean $PhaseKey 64
        phaseDisplayName = & $clean $PhaseDisplayName 160
        message = & $clean $Message 512
        percentComplete = $PercentComplete
        startedAt = $startedAt.ToUniversalTime().ToString('O')
        updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        ownerProcessId = $OwnerProcessId
        estimatedRemainingMinimumSeconds = if ($null -eq $EstimatedRemainingMinimumSeconds) { $null } else { [Math]::Min(86400, [Math]::Max(0, [int]$EstimatedRemainingMinimumSeconds)) }
        estimatedRemainingMaximumSeconds = if ($null -eq $EstimatedRemainingMaximumSeconds) { $null } else { [Math]::Min(86400, [Math]::Max(0, [int]$EstimatedRemainingMaximumSeconds)) }
        requiresRestart = [bool]$RequiresRestart
        errorCode = if ([String]::IsNullOrWhiteSpace($ErrorCode)) { $null } else { & $clean $ErrorCode 64 }
        errorMessage = if ([String]::IsNullOrWhiteSpace($ErrorMessage)) { $null } else { & $clean $ErrorMessage 1024 }
    }
    $json = $document | ConvertTo-Json -Depth 4
    $temporary = "$resolved.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $resolved -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
