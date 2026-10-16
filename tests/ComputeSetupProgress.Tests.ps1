. "$PSScriptRoot\..\scripts\Write-ComputeSetupProgress.ps1"
Describe 'Compute preparation milestones' {
    It 'preserves phase start and milestone time when a quiet phase reports again' {
        $path = Join-Path $TestDrive 'progress.json'
        $attempt = [guid]::NewGuid()
        Write-ComputeSetupProgress -Path $path -Phase 'checking' -Message 'Checking' -AttemptId $attempt
        Write-ComputeSetupProgress -Path $path -Phase 'build-guest' -Message 'Installing' -AttemptId $attempt
        $first = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Write-ComputeSetupProgress -Path $path -Phase 'build-guest' -Message 'Still installing' -AttemptId $attempt
        $quiet = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $quiet.version | Should Be 2
        $quiet.attemptId | Should Be $attempt.ToString('D')
        $quiet.phaseStartedAt | Should Be $first.phaseStartedAt
        $quiet.updatedAt | Should Be $first.updatedAt
        @($quiet.activity).Count | Should Be 2
        @(Get-ChildItem -LiteralPath $TestDrive -Filter '*.tmp').Count | Should Be 0
    }
    It 'resets history for a retry and bounds persisted activity' {
        $path = Join-Path $TestDrive 'retry-progress.json'
        $attempt = [guid]::NewGuid()
        foreach ($number in 1..30) {
            Write-ComputeSetupProgress -Path $path -Phase "phase-$number" -Message 'Phase' -AttemptId $attempt
        }
        $prior = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        @($prior.activity).Count | Should Be 24
        Write-ComputeSetupProgress -Path $path -Phase 'checking' -Message 'Retry' -AttemptId ([guid]::NewGuid())
        $retry = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        @($retry.activity).Count | Should Be 1
        $retry.attemptId | Should Not Be $prior.attemptId
        $retry.activity[0].phase | Should Be 'checking'
    }
    It 'replaces a truncated previous snapshot' {
        $path = Join-Path $TestDrive 'truncated.json'
        Set-Content -LiteralPath $path -Value '{'
        Write-ComputeSetupProgress -Path $path -Phase 'checking' -Message 'Recover' -AttemptId ([guid]::NewGuid())
        (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).phase | Should Be 'checking'
    }
    It 'replaces an incomplete same-attempt snapshot under strict mode' {
        Set-StrictMode -Version Latest
        $path = Join-Path $TestDrive 'incomplete.json'
        $attempt = [guid]::NewGuid()
        @{ version = 2; attemptId = $attempt.ToString('D') } | ConvertTo-Json | Set-Content -LiteralPath $path
        Write-ComputeSetupProgress -Path $path -Phase 'checking' -Message 'Recover' -AttemptId $attempt
        @((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).activity).Count | Should Be 1
    }
}
