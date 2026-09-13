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
param($OutputPath, $ReportProgress)
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
