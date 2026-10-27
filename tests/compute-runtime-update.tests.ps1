param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
. (Join-Path $RepositoryRoot 'scripts/Update-ComputeProviderRuntime.ps1')
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('csweet-runtime-update-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $publish=Join-Path $testRoot 'publish';New-Item -ItemType Directory -Path $publish | Out-Null
    Set-Content -LiteralPath (Join-Path $publish 'CSweet.Compute.HyperV.exe') -Value 'new'
    foreach($failure in @($false,$true)) {
        $root=Join-Path $testRoot ([guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path (Join-Path $root 'runtime') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'runtime/CSweet.Compute.HyperV.exe') -Value 'old'
        foreach($name in @('provider.json','catalog.json','provisioning.json','journal.json','key.pem','workload.vhdx')) { Set-Content -LiteralPath (Join-Path $root $name) -Value 'preserve' }
        $caught=$false
        try { $backup=Install-ComputeProviderRuntime $root $publish -Validate { param($exe)
            if((Get-Content -LiteralPath $exe -Raw).Trim() -ne 'new') { throw 'New runtime missing' }
            foreach($name in @('catalog.json','provisioning.json')) { Set-Content -LiteralPath (Join-Path $root $name) -Value 'new-certification' }
            if($failure) { throw 'Injected validation failure after certification' }
        }} catch { if(-not $failure) { throw };$caught=$true }
        if($caught -ne $failure) { throw 'Validation failure was not propagated' }
        $expected=if($failure){'old'}else{'new'}
        if((Get-Content -LiteralPath (Join-Path $root 'runtime/CSweet.Compute.HyperV.exe') -Raw).Trim() -ne $expected) { throw 'Runtime replacement/rollback failed' }
        foreach($name in @('provider.json','journal.json','key.pem','workload.vhdx')) {
            if((Get-Content -LiteralPath (Join-Path $root $name) -Raw).Trim() -ne 'preserve') { throw 'Durable provider state changed' }
        }
        foreach($name in @('catalog.json','provisioning.json')) {
            $expectedMetadata=if($failure){'preserve'}else{'new-certification'}
            if((Get-Content -LiteralPath (Join-Path $root $name) -Raw).Trim() -ne $expectedMetadata) { throw 'Certification transaction did not follow runtime commit/rollback' }
        }
        if(-not $failure -and (Get-Content -LiteralPath (Join-Path $backup 'CSweet.Compute.HyperV.exe') -Raw).Trim() -ne 'old') { throw 'Recoverable backup missing' }
    }
    Write-Output 'Passed provider payload replacement and rollback; identity, journal and workloads preserved.'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if([IO.Path]::GetDirectoryName($resolved) -ne ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())).TrimEnd([IO.Path]::DirectorySeparatorChar) -or -not [IO.Path]::GetFileName($resolved).StartsWith('csweet-runtime-update-test-')) { throw 'Unexpected fixture cleanup target' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
