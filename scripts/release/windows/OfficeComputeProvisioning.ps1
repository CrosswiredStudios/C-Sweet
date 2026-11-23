function Test-OfficeComputeBudget($Deployment, $Reservation) {
    foreach ($pair in @(@('cpuCount', 'allocatableCpuCount', 2, 1), @('memoryMiB', 'allocatableMemoryMb', 2048, 128),
                        @('diskMiB', 'allocatableDiskMb', 20480, 64), @('maximumEnvironments', 'maximumConcurrentWorkloads', 1, 1))) {
        $reserved = if ($null -eq $Reservation) { $pair[2] } else { $Reservation.($pair[0]) }
        $total = $Deployment.($pair[1])
        if (($reserved -isnot [int] -and $reserved -isnot [long]) -or $reserved -lt $pair[2] -or
            ($total -isnot [int] -and $total -isnot [long]) -or $total -gt [int]::MaxValue -or $reserved -gt ($total - $pair[3])) { return $false }
    }
    return $true
}

function Get-CombinedOfficeComputeSettings($Deployment, $Release, [string] $SingleBusiness) {
    $custom = $Deployment.PSObject.Properties['compute']
    if ($custom -and $custom.Value.enabled -isnot [bool]) { throw 'Invalid Office compute enablement.' }
    if ($custom -and !$custom.Value.enabled) { return }
    $scope = if ($custom) { [string]$custom.Value.organizationId } else { $SingleBusiness }
    if ([string]::IsNullOrEmpty($scope)) { return }
    $business = [guid]::Empty
    if (![guid]::TryParseExact($scope, 'D', [ref]$business) -or $business -eq [guid]::Empty) { throw 'Invalid Office compute business.' }
    if ($Release.imageDigest -cnotmatch '^sha256:[0-9a-f]{64}$' -or $Release.features -isnot [array] -or
        $Release.features -cnotcontains 'docker' -or $Release.features.Count -gt 64 -or
        @($Release.features | Where-Object { $_ -isnot [string] -or $_ -cnotmatch '^[a-z0-9][a-z0-9._-]{0,63}$' }).Count) { throw 'Invalid installed compute image catalog.' }
    $policy = if ($custom) { $custom.Value } else {
        [pscustomobject]@{ enabled=$true; organizationId=$business.ToString('D'); maximumEnvironments=1; cpuCount=2; memoryMiB=2048; diskMiB=20480 }
    }
    if (!(Test-OfficeComputeBudget $Deployment $policy)) {
        if ($custom) { throw 'The Office allocation cannot cover the requested compute reservation.' }
        return
    }
    return @{ reservation=$policy; CSweet=@{ OfficeCompute=@{ CombinedInstallImage=@{
        OrganizationId=$business.ToString('D'); ImageDigest=$Release.imageDigest; Features=@($Release.features)
    } } } }
}
