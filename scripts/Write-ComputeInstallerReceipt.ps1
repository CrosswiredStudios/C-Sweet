function Write-ComputeInstallerReceipt {
    param([string] $Directory, [string] $AttemptHash, [string] $State, [guid] $AttemptId = [guid]::Empty,
        [string] $FailureCode = 'local_setup_failed')
    $receipt = @{ attemptHash = $AttemptHash; state = $State; processId = $PID;
        startedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); attemptId = $AttemptId.ToString('D') }
    if ($State -eq 'Failed') { $receipt.failureCode = $FailureCode }
    $name = if ($State -eq 'Running') { 'process.json' } else { 'result.json' }
    $path = Join-Path $Directory $name
    [IO.File]::WriteAllText(($path + '.tmp'), ($receipt | ConvertTo-Json -Compress))
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
