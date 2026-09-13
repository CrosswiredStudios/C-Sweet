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
        @($result.PSObject.Properties.Name) -contains 'secret' | Should Be $false
    }
}
