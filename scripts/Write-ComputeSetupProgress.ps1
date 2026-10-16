function Write-ComputeSetupProgress {
    param([string] $Path, [string] $Message, [string] $Phase = 'checking', [guid] $AttemptId = [guid]::Empty)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $previous = $null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        try { $previous = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { }
    }
    $sameAttempt = $null -ne $previous -and $previous.PSObject.Properties['version'] -and $previous.version -eq 2 -and
        $previous.PSObject.Properties['attemptId'] -and $previous.attemptId -eq $AttemptId.ToString('D') -and
        $previous.PSObject.Properties['startedAt'] -and $previous.PSObject.Properties['phaseStartedAt'] -and
        $previous.PSObject.Properties['updatedAt'] -and $previous.PSObject.Properties['phase'] -and $previous.PSObject.Properties['activity']
    $startedAt = if ($sameAttempt) { $previous.startedAt } else { $now }
    $phaseStartedAt = if ($sameAttempt -and $previous.phase -eq $Phase) { $previous.phaseStartedAt } else { $now }
    $updatedAt = if ($sameAttempt -and $previous.phase -eq $Phase) { $previous.updatedAt } else { $now }
    $activity = if ($sameAttempt) { @($previous.activity) } else { @() }
    if (-not $sameAttempt -or $previous.phase -ne $Phase) {
        $activity = @($activity | Select-Object -Last 23) + @(@{ phase = $Phase; at = $now })
    }
    $json = @{ version = 2; attemptId = $AttemptId.ToString('D'); phase = $Phase; message = $Message;
        startedAt = $startedAt; phaseStartedAt = $phaseStartedAt; updatedAt = $updatedAt; activity = @($activity) } | ConvertTo-Json -Depth 4 -Compress
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, $json)
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Path) }
}
