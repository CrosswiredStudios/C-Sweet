# Keep selection aligned with ComputeLocalInstallation.Select. Only protected installation metadata is read.
function Get-ComputeLocalInstallation {
    param([Parameter(Mandatory = $true)][guid] $OrganizationId,
        [string] $BaseRoot = (Join-Path $env:ProgramData 'CSweet\Compute'))
    if ($OrganizationId -eq [guid]::Empty) { throw 'A business identity is required.' }
    $businessRoot = Join-Path (Split-Path -Parent $BaseRoot) ('ComputeBusinesses\' + $OrganizationId.ToString('N'))
    $scoped = Test-Path -LiteralPath (Join-Path $businessRoot 'provider.json') -PathType Leaf
    $legacyPath = Join-Path $BaseRoot 'provider.json'
    if (-not $scoped -and (Test-Path -LiteralPath $legacyPath -PathType Leaf)) {
        $legacy = Get-Content -LiteralPath $legacyPath -Raw | ConvertFrom-Json
        if ([guid]$legacy.enrollment.organizationId -eq [guid]::Empty) { throw 'The existing compute enrollment is invalid.' }
        $scoped = [guid]$legacy.enrollment.organizationId -ne $OrganizationId
    }
    $suffix = if ($scoped) { '.' + $OrganizationId.ToString('N') } else { '' }
    [pscustomobject]@{
        Root = $(if ($scoped) { $businessRoot } else { $BaseRoot })
        ServiceName = 'CSweet.Compute.HyperV' + $suffix
        ServiceArguments = $(if ($scoped) { 'service ' + $OrganizationId.ToString('N') } else { 'service' })
        ValidationArguments = $(if ($scoped) { @('validate-service', $OrganizationId.ToString('N')) } else { @('validate-service') })
    }
}
