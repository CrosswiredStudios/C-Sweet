. "$PSScriptRoot\..\scripts\Write-ComputeInstallerReceipt.ps1"
Describe 'Durable compute installer receipt' {
    It 'records process identity and failure without the handoff secret' {
        Write-ComputeInstallerReceipt -Directory $TestDrive -AttemptHash 'attempt-hash' -State 'Running'
        $process = Get-Content -LiteralPath (Join-Path $TestDrive 'process.json') -Raw | ConvertFrom-Json
        $process.processId | Should Be $PID
        $process.attemptHash | Should Be 'attempt-hash'
        [DateTimeOffset]::Parse($process.startedAt).Year | Should Be ([DateTimeOffset]::UtcNow.Year)
        Write-ComputeInstallerReceipt -Directory $TestDrive -AttemptHash 'attempt-hash' -State 'Failed'
        $result = Get-Content -LiteralPath (Join-Path $TestDrive 'result.json') -Raw | ConvertFrom-Json
        $result.state | Should Be 'Failed'
        $result.failureCode | Should Be 'local_setup_failed'
        @($result.PSObject.Properties.Name) -contains 'secret' | Should Be $false
    }
    It 'persists the specific failure with the current attempt identity' {
        $attempt = [guid]::NewGuid()
        Write-ComputeInstallerReceipt -Directory $TestDrive -AttemptHash 'attempt-hash' -State 'Failed' `
            -AttemptId $attempt -FailureCode 'setup_configuration_invalid'
        $result = Get-Content -LiteralPath (Join-Path $TestDrive 'result.json') -Raw | ConvertFrom-Json
        $result.attemptId | Should Be $attempt.ToString('D')
        $result.failureCode | Should Be 'setup_configuration_invalid'
        Write-ComputeInstallerReceipt -Directory $TestDrive -AttemptHash 'attempt-hash' -State 'Completed' -AttemptId $attempt
        $completed = Get-Content -LiteralPath (Join-Path $TestDrive 'result.json') -Raw | ConvertFrom-Json
        @($completed.PSObject.Properties.Name) -contains 'failureCode' | Should Be $false
    }
}
