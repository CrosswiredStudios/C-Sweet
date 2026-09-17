. (Join-Path $PSScriptRoot '../scripts/Get-ComputeLocalInstallation.ps1')
Describe 'Business compute installation selection' {
    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $root | Out-Null
        $first = [guid]::NewGuid()
        $second = [guid]::NewGuid()
    }
    It 'retains the first service and isolates the second business' {
        @{ enrollment = @{ organizationId = $first.ToString() } } | ConvertTo-Json | Set-Content (Join-Path $root 'provider.json')
        $legacy = Get-ComputeLocalInstallation $first $root
        $added = Get-ComputeLocalInstallation $second $root
        $legacy.Root | Should Be $root
        $legacy.ServiceName | Should Be 'CSweet.Compute.HyperV'
        $legacy.ServiceArguments | Should Be 'service'
        @($legacy.ValidationArguments).Count | Should Be 1
        $added.Root | Should Be (Join-Path (Split-Path -Parent $root) ('ComputeBusinesses\' + $second.ToString('N')))
        $added.ServiceName | Should Be ('CSweet.Compute.HyperV.' + $second.ToString('N'))
        $added.ServiceArguments | Should Be ('service ' + $second.ToString('N'))
        @($added.ValidationArguments).Count | Should Be 2
        $added.ValidationArguments[1] | Should Be $second.ToString('N')
        (Get-ComputeLocalInstallation $first $root).Root | Should Be $root
    }
    It 'keeps an existing business service when the legacy installation is absent' {
        $businessRoot = Join-Path (Split-Path -Parent $root) ('ComputeBusinesses\' + $second.ToString('N'))
        New-Item -ItemType Directory $businessRoot -Force | Out-Null
        '{}' | Set-Content (Join-Path $businessRoot 'provider.json')
        (Get-ComputeLocalInstallation $second $root).Root | Should Be $businessRoot
    }
    It 'rejects missing business identity and damaged legacy metadata' {
        { Get-ComputeLocalInstallation ([guid]::Empty) $root } | Should Throw
        '{}' | Set-Content (Join-Path $root 'provider.json')
        { Get-ComputeLocalInstallation $first $root } | Should Throw
    }
}
