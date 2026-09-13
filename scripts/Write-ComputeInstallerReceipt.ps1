function Write-ComputeInstallerReceipt {
    param([string] $Directory, [string] $AttemptHash, [string] $State)
    $receipt = @{ attemptHash = $AttemptHash; state = $State; processId = $PID;
        startedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o') }
    $name = if ($State -eq 'Running') { 'process.json' } else { 'result.json' }
    $path = Join-Path $Directory $name
    [IO.File]::WriteAllText(($path + '.tmp'), ($receipt | ConvertTo-Json -Compress))
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
