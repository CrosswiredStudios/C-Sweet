$preparationScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\scripts\Ensure-ComputeLinuxImage.ps1'))
Describe 'Automatic Linux preparation' {
    BeforeEach {
        $caseRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repo = Join-Path $caseRoot 'csweet'
        foreach ($relative in @('src/CSweet.Compute.Guest','src/CSweet.Compute.Contracts','src/CSweet.Domain/Compute','build/compute-linux','scripts')) {
            New-Item -ItemType Directory -Path (Join-Path $repo $relative) -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $repo "$relative/fixture.txt") -Value 'input'
        }
        New-Item -ItemType Directory -Path "$caseRoot/CSweet.Isolation/tools/LinuxImage" -Force | Out-Null
        Set-Content -LiteralPath "$caseRoot/CSweet.Isolation/tools/LinuxImage/fixture.txt" -Value 'shared input'
        foreach ($relative in @('Directory.Build.props','Directory.Packages.props','global.json','scripts/Install-ComputeGuestLinux.sh')) {
            Set-Content -LiteralPath (Join-Path $repo $relative) -Value 'input'
        }
        $builder = @'
param($OutputPath, $ReportProgress, $ArtifactDirectory)
if ($ArtifactDirectory) {
    [IO.Directory]::CreateDirectory($ArtifactDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $ArtifactDirectory 'builder-used'), 'cache and VM export')
}

[IO.File]::WriteAllText($OutputPath, 'image fixture')
$digest = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
@{ ImagePath = $OutputPath; Sha256 = $digest } | ConvertTo-Json | Set-Content -LiteralPath ($OutputPath + '.build.json')
'@
        Set-Content -LiteralPath "$repo/scripts/New-ComputeLinuxImage.ps1" -Value $builder
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../scripts/Write-ComputeSetupProgress.ps1') -Destination "$repo/scripts/Write-ComputeSetupProgress.ps1"
    }

    It 'prepares once and reuses the verified image automatically' {
        $first = & $preparationScript -RepositoryRoot $repo
        $second = & $preparationScript -RepositoryRoot $repo
        $second | Should Be $first
        @(Get-ChildItem "$repo/artifacts/compute/mvp/images" -Filter '*.vhdx').Count | Should Be 1
    }

    It 'builds and reuses the VHDX, cache and VM export on the selected volume' {
        $selected = Join-Path $caseRoot 'selected'
        $image = & $preparationScript -RepositoryRoot $repo -StorageRoot $selected
        [IO.Path]::GetDirectoryName($image) | Should Be (Join-Path $selected 'prepared')
        Test-Path (Join-Path $selected 'build/builder-used') | Should Be $true
        (& $preparationScript -RepositoryRoot $repo -StorageRoot $selected) | Should Be $image
        Test-Path "$repo/artifacts" | Should Be $false
    }

    It 'reports user-visible preparation progress separately from the image result' {
        $progress = Join-Path $caseRoot 'progress.json'
        $image = & $preparationScript -RepositoryRoot $repo -SetupProgressPath $progress
        Test-Path -LiteralPath $image -PathType Leaf | Should Be $true
        $report = Get-Content -LiteralPath $progress -Raw | ConvertFrom-Json
        [string]::IsNullOrWhiteSpace($report.message) | Should Be $false
        [DateTimeOffset]::Parse($report.updatedAt).Year | Should Be ([DateTimeOffset]::UtcNow.Year)
    }

    It 'rebuilds after guest changes while preserving the previous image' {
        $first = & $preparationScript -RepositoryRoot $repo
        Set-Content -LiteralPath "$repo/src/CSweet.Compute.Guest/fixture.txt" -Value 'updated guest'
        $second = & $preparationScript -RepositoryRoot $repo
        $second | Should Not Be $first
        Test-Path -LiteralPath $first | Should Be $true
    }

    It 'recovers from a damaged receipt and from an altered image' {
        $first = & $preparationScript -RepositoryRoot $repo
        Set-Content -LiteralPath "$repo/artifacts/compute/mvp/images/current-image.json" -Value '{}'
        $second = & $preparationScript -RepositoryRoot $repo
        $second | Should Not Be $first
        Set-Content -LiteralPath $second -Value 'altered bytes'
        $third = & $preparationScript -RepositoryRoot $repo
        $third | Should Not Be $second
    }

    It 'does not replace the current receipt when a rebuild fails' {
        & $preparationScript -RepositoryRoot $repo | Out-Null
        $receipt = Get-Content -LiteralPath "$repo/artifacts/compute/mvp/images/current-image.json" -Raw
        Set-Content -LiteralPath "$repo/scripts/New-ComputeLinuxImage.ps1" -Value "throw 'fixture build failure'"
        { & $preparationScript -RepositoryRoot $repo } | Should Throw 'fixture build failure'
        (Get-Content -LiteralPath "$repo/artifacts/compute/mvp/images/current-image.json" -Raw) | Should Be $receipt
    }
}

Describe 'Compute destination discovery' {
    BeforeEach {
        . (Join-Path $PSScriptRoot '../scripts/Initialize-ComputeStorage.ps1')
        Mock Get-Volume { [pscustomobject]@{ UniqueId='configured'; DriveLetter='Z'; DriveType='Fixed'; HealthStatus='Healthy'; FileSystemType='NTFS' } }
    }
    It 'never substitutes another volume when the saved identity is unavailable' {
        { Initialize-ComputeStorage 'unavailable' } | Should Throw 'volume is unavailable'
    }
    It 'rejects a removable backing disk even when the volume reports fixed storage' {
        Mock Get-Partition { [pscustomobject]@{DiskNumber=4} }
        Mock Get-Disk { [pscustomobject]@{IsOffline=$false;IsReadOnly=$false;BusType='USB'} }
        { Initialize-ComputeStorage 'configured' } | Should Throw 'must be healthy, fixed and writable'
    }
    It 'registers an empty installer directory and never claims pre-existing unrelated files' {
        $fixtureRoot = Join-Path $TestDrive 'volume-root'
        [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
        $id = '\\?\Volume{00000000-0000-0000-0000-000000000001}\'
        $fixtureKey = Get-ComputeStorageRegistration $id
        Mock Resolve-ComputeStorageRoot { $fixtureRoot }
        Mock Test-Path { $false } -ParameterFilter { $LiteralPath -eq $fixtureKey }
        Mock New-Item {} -ParameterFilter { $Path -eq $fixtureKey }
        Initialize-ComputeStorage $id | Should Be $fixtureRoot
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'model.bin') -Value 'user data'
        { Initialize-ComputeStorage $id } | Should Throw 'must be empty'
        Assert-MockCalled New-Item -Times 1 -Exactly -ParameterFilter { $Path -eq $fixtureKey }
    }
}
