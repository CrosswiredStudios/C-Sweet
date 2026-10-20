function Get-VM { }
function Get-VMHardDiskDrive { }
function Get-VMDvdDrive { }
function Get-VMSnapshot { }
function Get-VHD { }
function Unregister-ScheduledTask { param($TaskName, [switch]$Confirm) }
function New-ScheduledTaskAction { param($Execute,$Argument) }
function New-ScheduledTaskTrigger { param([switch]$Once,$At,$RepetitionInterval) }
function New-ScheduledTaskSettingsSet { param([switch]$StartWhenAvailable,$MultipleInstances,$ExecutionTimeLimit) }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,[switch]$Force) }
# Machine operations are mocked; all filesystem effects stay inside Pester's TestDrive.
Describe 'Business provider retirement' {
    BeforeEach {
        $script:oldProgramData = $env:ProgramData
        $env:ProgramData = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:business = [guid]::NewGuid()
        $script:node = [guid]::NewGuid()
        $script:owned = Join-Path $env:ProgramData ('CSweet\ComputeBusinesses\' + $business.ToString('N'))
        $script:taskDirectory = Join-Path $env:ProgramData ('CSweet\ComputeRetirement\' + $business.ToString('N'))
        $null = New-Item -ItemType Directory -Path $owned -Force
        $null = New-Item -ItemType Directory -Path $taskDirectory -Force
        $script:taskScript = Join-Path $taskDirectory 'retire.ps1'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\src\CSweet.Compute.HyperV\Retire-ComputeBusiness.ps1') -Destination $taskScript
        @{ enrollment = @{ organizationId=$business; nodeId=$node } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $owned 'provider.json')
        Set-Content -LiteralPath (Join-Path $owned 'private.vhdx') -Value 'owned private bytes'
        @{cleanupId=[guid]::NewGuid(); businessId=$business; nodeId=$node; receiptToken=('a' * 64);
            coreOrigin='https://core.example/'; coreCertificateSha256=$null; certificateThumbprint=('b' * 40);
            preserveSharedProvider=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDirectory 'request.json')
        Mock Import-Module { } -ParameterFilter { $Name -eq 'Hyper-V' }
        Mock Get-VM { if ($env:CSWEET_RETIRE_TEST_VM_ROOT) { @([pscustomobject]@{Path=$env:CSWEET_RETIRE_TEST_VM_ROOT; ConfigurationLocation=$env:CSWEET_RETIRE_TEST_VM_ROOT; SnapshotFileLocation=$env:CSWEET_RETIRE_TEST_VM_ROOT; SmartPagingFilePath=$env:CSWEET_RETIRE_TEST_VM_ROOT}) } else { @() } }
        Mock Get-CimInstance { $null }
        Mock Get-Service { $null }
        Mock Get-Item { $null } -ParameterFilter { $LiteralPath -like 'Cert:*' }
        Mock Invoke-WebRequest { if ($env:CSWEET_RETIRE_TEST_DENY -and $Uri.ToString().EndsWith('/retirement-authorize')) { return @{StatusCode=409} }; if ($env:CSWEET_RETIRE_TEST_FAIL_RECEIPT -and $Uri.ToString().EndsWith('/retirement-receipt')) { throw 'Core unavailable' }; @{StatusCode=204} }
        Mock Unregister-ScheduledTask { }
    }
    AfterEach { $env:ProgramData = $script:oldProgramData; Remove-Item Env:CSWEET_RETIRE_TEST_VM_ROOT, Env:CSWEET_RETIRE_TEST_FAIL_RECEIPT, Env:CSWEET_RETIRE_TEST_DENY -ErrorAction SilentlyContinue }

    It 'removes the exact business root and acknowledges after checking absence' {
        $other = Join-Path $env:ProgramData ('CSweet\ComputeBusinesses\' + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $other -Force
        Set-Content -LiteralPath (Join-Path $other 'shared-image.vhdx') -Value 'kept'
        & $taskScript
        (Test-Path -LiteralPath $owned) | Should Be $false
        (Test-Path -LiteralPath $taskDirectory) | Should Be $false
        (Test-Path -LiteralPath (Join-Path $other 'shared-image.vhdx')) | Should Be $true
        Assert-MockCalled Invoke-WebRequest -Times 1 -Exactly -Scope It -ParameterFilter { $Uri.ToString().EndsWith('/retirement-receipt') }
    }
    It 'keeps the retry task after a lost callback and replays removal idempotently' {
        $env:CSWEET_RETIRE_TEST_FAIL_RECEIPT = '1'
        $caught = $false
        try { & $taskScript } catch { $caught = $true }
        $caught | Should Be $true
        (Test-Path -LiteralPath $owned) | Should Be $false
        (Test-Path -LiteralPath $taskScript) | Should Be $true
        Remove-Item Env:CSWEET_RETIRE_TEST_FAIL_RECEIPT
        & $taskScript
        (Test-Path -LiteralPath $taskDirectory) | Should Be $false
    }
    It 'refuses a VM still referencing a business image and sends no receipt' {
        $env:CSWEET_RETIRE_TEST_VM_ROOT = $owned
        Mock Get-VMHardDiskDrive { @() }
        Mock Get-VMDvdDrive { @() }
        Mock Get-VMSnapshot { @() }
        $caught = $false
        try { & $taskScript } catch { $caught = $true }
        Assert-MockCalled Get-VM -Times 1 -Scope It
        $caught | Should Be $true
        (Test-Path -LiteralPath (Join-Path $owned 'private.vhdx')) | Should Be $true
        Assert-MockCalled Invoke-WebRequest -Times 0 -Exactly -Scope It -ParameterFilter { $Uri.ToString().EndsWith('/retirement-receipt') }
    }
    It 'refuses storage whose enrollment no longer matches the business' {
        @{enrollment=@{organizationId=[guid]::NewGuid();nodeId=$node}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $owned 'provider.json')
        $caught = $false
        try { & $taskScript } catch { $caught = $true }
        $caught | Should Be $true
        (Test-Path -LiteralPath (Join-Path $owned 'private.vhdx')) | Should Be $true
        Assert-MockCalled Invoke-WebRequest -Times 0 -Exactly -Scope It -ParameterFilter { $Uri.ToString().EndsWith('/retirement-receipt') }
    }
    It 'keeps all payloads when current Core state refuses retirement' {
        $env:CSWEET_RETIRE_TEST_DENY = '1'
        $caught = $false
        try { & $taskScript } catch { $caught = $true }
        $caught | Should Be $true
        (Test-Path -LiteralPath (Join-Path $owned 'private.vhdx')) | Should Be $true
        Assert-MockCalled Invoke-WebRequest -Times 0 -Exactly -Scope It -ParameterFilter { $Uri.ToString().EndsWith('/retirement-receipt') }
    }
    It 'cleans an interrupted install using its protected ownership marker' {
        @{enrollment=@{organizationId=$business;nodeId=$node}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $owned 'compute-owner.json')
        Remove-Item -LiteralPath (Join-Path $owned 'provider.json') -Force -ErrorAction Stop
        (Test-Path -LiteralPath (Join-Path $owned 'provider.json')) | Should Be $false
        & $taskScript
        (Test-Path -LiteralPath $owned) | Should Be $false
        Assert-MockCalled Invoke-WebRequest -Times 1 -Exactly -Scope It -ParameterFilter { $Uri.ToString().EndsWith('/retirement-receipt') }
    }

}

Describe 'Privileged installer retirement registration' {
    BeforeEach {
        $script:savedProgramData = $env:ProgramData
        $env:ProgramData = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:registerBusiness = [guid]::NewGuid()
        $script:registerSetup = [guid]::NewGuid()
        $script:registerRoot = Join-Path $env:ProgramData ('CSweet\ComputeBusinesses\' + $registerBusiness.ToString('N'))
        $script:handoff = Join-Path $TestDrive 'handoff.json'
        @{organizationId=$registerBusiness;setupId=$registerSetup;repositoryRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot));
            coreOrigin='https://core.example/';coreCertificateSha256=$null;
            retirement=@{cleanupId=[guid]::NewGuid();businessId=$registerBusiness;nodeId=$registerSetup;receiptToken=('a' * 64)}} |
            ConvertTo-Json | Set-Content -LiteralPath $handoff
        Mock Set-Acl { }
        Mock Get-Acl {
            $security = New-Object PSObject
            $security | Add-Member ScriptMethod GetOwner { param($type)
                $sid = if ($env:CSWEET_RETIRE_TEST_BAD_OWNER) { 'S-1-5-32-545' } else { 'S-1-5-18' }
                New-Object Security.Principal.SecurityIdentifier($sid)
            }
            $security | Add-Member ScriptMethod GetAccessRules { param($explicit,$inherited,$type) @() }
            $security
        }
        Mock New-ScheduledTaskAction { [pscustomobject]@{Execute=$Execute;Arguments=$Argument} }
        Mock New-ScheduledTaskTrigger { [pscustomobject]@{At=$At} }
        Mock New-ScheduledTaskSettingsSet { [pscustomobject]@{MultipleInstances=$MultipleInstances} }
        Mock Register-ScheduledTask { }
    }
    AfterEach {
        $env:ProgramData = $script:savedProgramData
        Remove-Item Env:CSWEET_RETIRE_TEST_BAD_OWNER -ErrorAction SilentlyContinue
    }
    It 'registers before payload creation and persists only the scoped receipt in protected task storage' {
        & (Join-Path $PSScriptRoot '..\Register-ComputeBusinessRetirement.ps1') -HandoffPath $handoff -ProtectedRoot $registerRoot -ServiceName ('CSweet.Compute.HyperV.' + $registerBusiness.ToString('N'))
        $directory = Join-Path $env:ProgramData ('CSweet\ComputeRetirement\' + $registerBusiness.ToString('N'))
        $request = Get-Content -LiteralPath (Join-Path $directory 'request.json') -Raw | ConvertFrom-Json
        $request.receiptToken | Should Be ('a' * 64)
        ([guid]$request.businessId) | Should Be $registerBusiness
        (Test-Path -LiteralPath $registerRoot) | Should Be $false
        Assert-MockCalled Register-ScheduledTask -Times 1 -Exactly -Scope It -ParameterFilter {$User -eq 'SYSTEM' -and $RunLevel -eq 'Highest'}
    }
    It 'refuses an ancestor that could let a local user replace the SYSTEM task script' {
        $env:CSWEET_RETIRE_TEST_BAD_OWNER = '1'
        $caught = $false
        try { & (Join-Path $PSScriptRoot '..\Register-ComputeBusinessRetirement.ps1') -HandoffPath $handoff -ProtectedRoot $registerRoot -ServiceName ('CSweet.Compute.HyperV.' + $registerBusiness.ToString('N')) } catch { $caught = $true }
        $caught | Should Be $true
        Assert-MockCalled Register-ScheduledTask -Times 0 -Exactly -Scope It
    }
}
