# Run with: Invoke-Pester ./scripts/tests/Reset-CSweetLocal.Tests.ps1 -EnableExit
. (Join-Path $PSScriptRoot '..\Reset-CSweetLocal.ps1')

# Pester 3's Should Throw does not reliably observe exceptions on PowerShell 7.
function Assert-ResetThrows([scriptblock] $Action) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    $caught | Should Be $true
}

# Stubs let the VM planner be tested without a Hyper-V host or administrator token.
function Get-VMHardDiskDrive { param([Parameter(ValueFromPipeline)] $VM) process { foreach ($path in $VM.Disks) { [pscustomobject]@{ Path = $path } } } }
function Get-VMDvdDrive { param([Parameter(ValueFromPipeline)] $VM) process { foreach ($path in $VM.Dvds) { [pscustomobject]@{ Path = $path } } } }
function Get-VMSnapshot { param([Parameter(ValueFromPipeline)] $VM) process {} }
function Get-VHD { param($Path) [pscustomobject]@{ ParentPath = ''; Attached = $false } }
function docker { param([Parameter(ValueFromRemainingArguments)] $Arguments) throw 'Unexpected Docker call in isolated test' }
function New-TestVm([string] $Path, [string[]] $Disks, [string[]] $Dvds = @()) {
    [pscustomobject]@{ Name = 'fixture'; Id = [guid]::NewGuid(); State = 'Off'; Path = $Path;
        ConfigurationLocation = $Path; SnapshotFileLocation = $Path; SmartPagingFilePath = $Path; Disks = $Disks; Dvds = $Dvds }
}

Describe 'Local reset filesystem boundaries' {
    It 'requires full drive or UNC paths on both PowerShell editions' {
        foreach ($path in @('', 'relative', 'C:relative', '\current-drive', '\\server', '\\?\C:\CSweet', '\\.\C:\CSweet')) {
            Test-CSweetResetAbsolutePath $path | Should Be $false
        }
        foreach ($path in @('C:\CSweet', 'C:/CSweet', '\\server\share\CSweet')) {
            Test-CSweetResetAbsolutePath $path | Should Be $true
        }
        Test-CSweetResetWithin '\\server\share\CSweet\disk.vhdx' '\\server\share\CSweet' | Should Be $true
        Test-CSweetResetWithin '\\server\share\CSweet-old\disk.vhdx' '\\server\share\CSweet' | Should Be $false
    }
    It 'rejects sibling-prefix matches, relative paths, traversal and disk roots' {
        Test-CSweetResetWithin 'C:\CSweet-old\disk.vhdx' 'C:\CSweet' | Should Be $false
        Test-CSweetResetWithin 'relative\disk.vhdx' 'C:\CSweet' | Should Be $false
        Test-CSweetResetWithin 'C:\CSweet\..\other' 'C:\CSweet' | Should Be $false
        Assert-ResetThrows { Assert-CSweetResetPath 'C:\' 'C:\' }
        Test-CSweetResetWithin 'C:\CSweet\workloads\disk.vhdx' 'C:\CSweet' | Should Be $true
    }
    It 'rejects junctions before descending into their targets' {
        $root = Join-Path $TestDrive 'links'
        $outside = Join-Path $TestDrive 'outside'
        New-Item -ItemType Directory -Path $root, $outside | Out-Null
        $link = Join-Path $root 'redirect'
        New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
        try {
            Assert-ResetThrows { Assert-CSweetResetPath (Join-Path $link 'child') $root }
            Assert-ResetThrows { Get-CSweetResetTree $root }
        } finally { [IO.Directory]::Delete($link) }
    }
    It 'selects project outputs without selecting arbitrary bin directories' {
        $repo = Join-Path $TestDrive 'repo'
        New-Item -ItemType Directory -Path "$repo\src\Example\bin", "$repo\src\Example\obj", "$repo\src\UserWork\bin", "$repo\artifacts" -Force | Out-Null
        & git -C $repo init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the isolated Git fixture.' }
        Set-Content -LiteralPath "$repo\src\Example\Example.csproj" -Value '<Project />'
        $targets = @(Get-CSweetResetBuildTargets $repo)
        $targets.Count | Should Be 3
        ($targets -contains "$repo\src\UserWork\bin") | Should Be $false
    }
    It 'refuses an artifacts directory containing tracked files' {
        $repo = Join-Path $TestDrive 'tracked-repo'
        New-Item -ItemType Directory -Path "$repo\artifacts" -Force | Out-Null
        & git -C $repo init --quiet
        Set-Content -LiteralPath "$repo\artifacts\keep.txt" -Value 'tracked output'
        & git -C $repo add -- artifacts/keep.txt
        if ($LASTEXITCODE -ne 0) { throw 'Failed to stage test fixture.' }
        Assert-ResetThrows { Assert-CSweetResetBuildUntracked $repo "$repo\artifacts" }
    }
}

Describe 'Local reset VM ownership' {
    It 'ignores unrelated VMs even if named like C-Sweet' {
        $vm = New-TestVm 'C:\Unrelated\VM' @('C:\Unrelated\disk.vhdx')
        $vm.Name = 'csweet-compute-not-ours'
        @(Get-CSweetResetVmPlan @($vm) @('C:\CSweet')).Count | Should Be 0
    }
    It 'selects VMs fully contained in a reset root' {
        $vm = New-TestVm 'C:\CSweet\workload' @('C:\CSweet\workload\os.vhdx')
        @(Get-CSweetResetVmPlan @($vm) @('C:\CSweet')).Count | Should Be 1
    }
    It 'blocks a VM outside reset roots using a selected disk' {
        $vm = New-TestVm 'C:\Other\VM' @('C:\CSweet\disk.vhdx')
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm) @('C:\CSweet') }
    }
    It 'blocks an owned VM with an external writable disk' {
        $vm = New-TestVm 'C:\CSweet\workload' @('D:\UserData\disk.vhdx')
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm) @('C:\CSweet') }
    }
    It 'blocks deleting a base image used by an unrelated VM' {
        Mock Get-VHD { if ($Path -eq 'C:\Other\child.vhdx') { [pscustomobject]@{ ParentPath = 'C:\CSweet\base.vhdx' } } else { [pscustomobject]@{ ParentPath = '' } } }
        $vm = New-TestVm 'C:\Other\VM' @('C:\Other\child.vhdx')
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm) @('C:\CSweet') }
    }
    It 'allows retaining an external base image of an owned VM' {
        Mock Get-VHD { if ($Path -eq 'C:\CSweet\child.vhdx') { [pscustomobject]@{ ParentPath = 'C:\Images\base.vhdx' } } else { [pscustomobject]@{ ParentPath = '' } } }
        $vm = New-TestVm 'C:\CSweet\workload' @('C:\CSweet\child.vhdx')
        @(Get-CSweetResetVmPlan @($vm) @('C:\CSweet')).Count | Should Be 1
    }
}

Describe 'Temporary C-Sweet image-build VMs' {
    BeforeEach {
        Mock Get-VMSnapshot { @() }
        $artifactRoot = Join-Path $TestDrive 'CSweet.Isolation\artifacts'
        $imageRoot = Join-Path $artifactRoot 'linux-images'
        $seed = Join-Path $imageRoot 'image-0123456789abcdef0123456789abcdef\cidata.iso'
        $tempRoot = Join-Path $TestDrive 'temp'
        $vmPath = Join-Path $tempRoot 'hyperv2074192109\csw-c4c1eb8ad3df'
        $disk = Join-Path (Split-Path -Parent $vmPath) 'csw-c4c1eb8ad3df.vhdx'
        New-Item -ItemType Directory -Path (Split-Path -Parent $seed), $vmPath -Force | Out-Null
        Set-Content -LiteralPath $seed -Value 'fixture'
        Set-Content -LiteralPath $disk -Value 'fixture'
        $vm = New-TestVm $vmPath @($disk) @($seed)
        $vm.Name = 'csw-c4c1eb8ad3df'
    }
    It 'includes the exact temporary VM and disk when its C-Sweet build seed is selected' {
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm) @($artifactRoot) }
        $extra = @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot)
        $extra.Count | Should Be 2
        ($extra.Path -contains $vmPath) | Should Be $true
        ($extra.Path -contains $disk) | Should Be $true
        ($extra.Path -contains (Split-Path -Parent $vmPath)) | Should Be $false
        @(Get-CSweetResetVmPlan @($vm) (@($artifactRoot) + $extra.Path)).Count | Should Be 1
    }
    It 'does not include temporary VMs when image artifacts are outside the selected reset' {
        @(Get-CSweetResetImageBuildTargets @($vm) @('C:\unrelated') $imageRoot $tempRoot).Count | Should Be 0
    }
    It 'requires a matching seed rather than trusting the VM name and temp folder' {
        $vm.Dvds = @()
        @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot).Count | Should Be 0
    }
    It 'rejects an unrelated ISO even when stored under selected artifacts' {
        $vm.Dvds = @((Join-Path $imageRoot 'cache\ubuntu.iso'))
        @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot).Count | Should Be 0
    }
    It 'requires the expected disk and refuses snapshots' {
        $vm.Disks = @('D:\user-data.vhdx')
        @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot).Count | Should Be 0
        $vm.Disks = @($disk)
        Mock Get-VMSnapshot { [pscustomobject]@{ Name = 'checkpoint' } }
        @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot).Count | Should Be 0
    }
    It 'continues to block another VM sharing the recognized temporary disk' {
        $extra = @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot)
        $other = New-TestVm (Join-Path $TestDrive 'other-vm') @($disk)
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm, $other) (@($artifactRoot) + $extra.Path) }
    }
    It 'refuses config paths escaping the recognized VM directory' {
        $extra = @(Get-CSweetResetImageBuildTargets @($vm) @($artifactRoot) $imageRoot $tempRoot)
        $vm.SnapshotFileLocation = Join-Path $TestDrive 'unrelated-checkpoints'
        Assert-ResetThrows { Get-CSweetResetVmPlan @($vm) (@($artifactRoot) + $extra.Path) }
    }
    It 'recognizes legacy Linux names and requires the same run ID in the seed path' {
        $legacyName = 'CSweet-Linux-Image-Build-image-0123456789abcdef0123456789abcdef'
        $legacyPath = Join-Path $tempRoot ('hyperv1562693706\' + $legacyName)
        $legacyDisk = $legacyPath + '.vhdx'
        $legacyVm = New-TestVm $legacyPath @($legacyDisk) @($seed)
        $legacyVm.Name = $legacyName
        $extra = @(Get-CSweetResetImageBuildTargets @($legacyVm) @($artifactRoot) $imageRoot $tempRoot)
        $extra.Count | Should Be 2
        @(Get-CSweetResetVmPlan @($legacyVm) (@($artifactRoot) + $extra.Path)).Count | Should Be 1
        $wrongSeed = Join-Path $imageRoot 'image-ffffffffffffffffffffffffffffffff\cidata.iso'
        New-Item -ItemType Directory -Path (Split-Path -Parent $wrongSeed) -Force | Out-Null
        Set-Content -LiteralPath $wrongSeed -Value 'another run'
        $legacyVm.Dvds = @($wrongSeed)
        @(Get-CSweetResetImageBuildTargets @($legacyVm) @($artifactRoot) $imageRoot $tempRoot).Count | Should Be 0
    }
    It 'recognizes historical Office and SatelliteOffice builders only with a matching selected seed' {
        foreach ($repo in @('CSweet.Office', 'CSweet.SatelliteOffice')) {
            $officeRoot = Join-Path $TestDrive ($repo + '\artifacts\windows-test')
            $run = '20260901-123456-abcdef01'
            $officeSeed = Join-Path $officeRoot ($run + '\cidata.iso')
            New-Item -ItemType Directory -Path (Split-Path -Parent $officeSeed) -Force | Out-Null
            Set-Content -LiteralPath $officeSeed -Value 'fixture'
            $name = 'CSweet-Agent-Guest-Image-Build-' + $run
            $path = Join-Path $tempRoot ('hyperv1234\' + $name)
            $officeVm = New-TestVm $path @($path + '.vhdx') @($officeSeed)
            $officeVm.Name = $name
            $extra = @(Get-CSweetResetImageBuildTargets @($officeVm) @($officeRoot) $imageRoot $tempRoot @($officeRoot))
            $extra.Count | Should Be 2
            @(Get-CSweetResetVmPlan @($officeVm) (@($officeRoot) + $extra.Path)).Count | Should Be 1
            @(Get-CSweetResetImageBuildTargets @($officeVm) @($artifactRoot) $imageRoot $tempRoot @($officeRoot)).Count | Should Be 0
            $officeVm.Dvds = @($seed)
            @(Get-CSweetResetImageBuildTargets @($officeVm) @($officeRoot, $artifactRoot) $imageRoot $tempRoot @($officeRoot)).Count | Should Be 0
        }
    }
    It 'reports every VM ownership blocker in one pass without returning a partial plan' {
        $other = New-TestVm (Join-Path $TestDrive 'outside') @($seed.Replace('.iso', '.vhdx'))
        $other.Name = 'unrelated-vm'
        $partialPlan = [Collections.Generic.List[object]]::new()
        $failure = ''
        try { Get-CSweetResetVmPlan @($vm, $other) @($artifactRoot) | ForEach-Object { $partialPlan.Add($_) } }
        catch { $failure = $_.Exception.Message }
        $failure | Should Match '2 blocker'
        $failure | Should Match 'csw-c4c1eb8ad3df'
        $failure | Should Match 'unrelated-vm'
        $partialPlan.Count | Should Be 0
    }
}

Describe 'Local reset execution gates' {
    BeforeEach {
        Mock Test-Path { $LiteralPath -like '*CSweet.AppHost.csproj' }
        Mock Get-CimInstance { @() }
        Mock Test-CSweetResetHyperVAvailable { $false }
        Mock Get-ItemProperty { $null }
        Mock Test-CSweetResetAdministrator { $true }
        Mock Set-Service { throw 'Mutation reached' }
        Mock Stop-Service { throw 'Mutation reached' }
        Mock Remove-Item { throw 'Mutation reached' }
        Mock Write-Host {}
    }
    It 'does not mutate in the default preview' {
        Invoke-CSweetLocalReset -RepositoryRoot 'C:\fixture'
        Assert-MockCalled Remove-Item -Times 0 -Exactly
        Assert-MockCalled Set-Service -Times 0 -Exactly
        Assert-MockCalled Stop-Service -Times 0 -Exactly
    }
    It 'resolves the default repository from the script location after binding' {
        $expectedProject = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\CSweet.AppHost\CSweet.AppHost.csproj'
        Invoke-CSweetLocalReset
        Assert-MockCalled Test-Path -Times 1 -Exactly -ParameterFilter { $LiteralPath -eq $expectedProject }
        Assert-MockCalled Remove-Item -Times 0 -Exactly
    }
    It 'blocks apply when any inventory is incomplete' {
        Mock Get-CimInstance { throw 'Inventory denied' }
        Assert-ResetThrows { Invoke-CSweetLocalReset -Apply -RepositoryRoot 'C:\fixture' -WarningAction SilentlyContinue }
        Assert-MockCalled Remove-Item -Times 0 -Exactly
        Assert-MockCalled Set-Service -Times 0 -Exactly
    }
    It 'honors WhatIf even when Apply is supplied' {
        Invoke-CSweetLocalReset -Apply -WhatIf -RepositoryRoot 'C:\fixture'
        Assert-MockCalled Remove-Item -Times 0 -Exactly
        Assert-MockCalled Set-Service -Times 0 -Exactly
    }
    It 'does not query Docker unless database reset was requested' {
        Mock docker { throw 'Docker must not be called' }
        Invoke-CSweetLocalReset -RepositoryRoot 'C:\fixture'
        Assert-MockCalled docker -Times 0 -Exactly
    }
    It 'preserves tracked build fixtures without blocking the remaining reset plan' {
        Mock Test-Path { $LiteralPath -like '*CSweet.AppHost.csproj' -or $LiteralPath -in @('C:\fixture\.git', 'C:\fixture\artifacts') }
        Mock Get-ChildItem { @() }
        Mock Get-CSweetResetBuildTargets { 'C:\fixture\artifacts' }
        Mock Assert-CSweetResetPath {}
        Mock Get-CSweetResetTrackedFiles { 'artifacts/keep.txt' }
        Mock Get-CSweetResetTree { throw 'Preserved targets must not be scanned or deleted' }
        Invoke-CSweetLocalReset -IncludeBuildOutputs -Apply -WhatIf -RepositoryRoot 'C:\fixture'
        Assert-MockCalled Get-CSweetResetTree -Times 0 -Exactly
        Assert-MockCalled Remove-Item -Times 0 -Exactly
    }
    It 'blocks a remote Docker context before any mutation' {
        Mock docker { $global:LASTEXITCODE = 0; '[{"Name":"remote","Endpoints":{"docker":{"Host":"ssh://remote"}}}]' }
        Assert-ResetThrows { Invoke-CSweetLocalReset -ResetDatabase -Apply -RepositoryRoot 'C:\fixture' -WarningAction SilentlyContinue }
        Assert-MockCalled Remove-Item -Times 0 -Exactly
        Assert-MockCalled Set-Service -Times 0 -Exactly
    }
    It 'blocks a failed Docker inventory before any mutation' {
        Mock docker { $global:LASTEXITCODE = 1; throw 'Docker unavailable' }
        Assert-ResetThrows { Invoke-CSweetLocalReset -ResetDatabase -Apply -RepositoryRoot 'C:\fixture' -WarningAction SilentlyContinue }
        Assert-MockCalled Remove-Item -Times 0 -Exactly
        Assert-MockCalled Set-Service -Times 0 -Exactly
    }
}
