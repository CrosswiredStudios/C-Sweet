Set-StrictMode -Version Latest

function New-CSweetOfficeConnectionConfiguration {
    param($Node, [string] $ControlPlaneUrl, [string] $ExpectedSha256, $ObservedCertificate)
    $target = [Uri]$ControlPlaneUrl
    if (-not $target.IsAbsoluteUri -or $target.Scheme -ne 'https' -or $target.UserInfo -or $target.Query -or $target.Fragment) {
        throw 'Office connection repair requires the authorized HTTPS control-plane address.'
    }
    $expected = $ExpectedSha256.Replace(':', '').Replace('-', '').ToLowerInvariant()
    $actual = ([string]$ObservedCertificate.certificateSha256).Replace(':', '').Replace('-', '').ToLowerInvariant()
    # An exact authorized pin establishes trust for a self-signed development certificate.
    # Missing certificates and hostname mismatches remain invalid independently of the pin.
    $rejectedErrors = [int][Net.Security.SslPolicyErrors]::RemoteCertificateNotAvailable -bor
        [int][Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch
    if ($expected -notmatch '^[0-9a-f]{64}$' -or $expected -cne $actual -or
        (([int]$ObservedCertificate.policyErrors -band $rejectedErrors) -ne 0) -or
        ([DateTime]$ObservedCertificate.notBefore).ToUniversalTime() -gt [DateTime]::UtcNow -or
        ([DateTime]$ObservedCertificate.notAfter).ToUniversalTime() -lt [DateTime]::UtcNow) {
        throw 'The Office control-plane certificate does not match the authorized pin, host or validity period.'
    }
    $updated = [ordered]@{}
    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Name -notin @('ControlPlaneUrl', 'ControlPlaneCertificateSha256')) {
            $updated[$property.Name] = $property.Value
        }
    }
    $updated.ControlPlaneUrl = $target.AbsoluteUri
    return [pscustomobject]@{ Node = [pscustomobject]$updated; Pin = $expected }
}

function Read-CSweetOfficeServiceCommand {
    param([string] $Command)
    if ($Command -notmatch '^"([^"]+)"\s+--contentRoot\s+"([^"]+)"(?:\s+--environment\s+(?:Production|"Production"))?\s*$') {
        throw 'The existing Office service has an unrecognized installation command.'
    }
    return [pscustomobject]@{ Executable = $Matches[1]; ContentRoot = $Matches[2] }
}

function Sync-CSweetOfficeConnection {
    param([Guid] $OfficeId, [string] $InstallRoot, [string] $DataRoot,
        [string] $ControlPlaneUrl, [string] $ExpectedSha256)
    if ($OfficeId -eq [Guid]::Empty -or -not [IO.Path]::IsPathRooted($InstallRoot) -or -not [IO.Path]::IsPathRooted($DataRoot)) {
        throw 'Office connection repair requires its current identity and protected storage roots.'
    }
    $service = Get-CimInstance Win32_Service -Filter "Name='CSweet.Office.Node'"
    if ($null -eq $service) {
        throw 'The existing Office service has an unrecognized installation command.'
    }
    $command = Read-CSweetOfficeServiceCommand ([string]$service.PathName)
    $executable = [IO.Path]::GetFullPath($command.Executable)
    $contentRoot = [IO.Path]::GetFullPath($command.ContentRoot)
    $protectedRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    if (-not $contentRoot.StartsWith($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $executable.StartsWith($contentRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $contentRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The Office service is outside its protected installation.'
    }
    $configurationPath = Join-Path $contentRoot 'appsettings.json'
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $node = $configuration.CSweet.Office.Node
    $nodeRoot = [IO.Path]::GetFullPath((Join-Path $DataRoot 'node'))
    if ([IO.Path]::GetFullPath([string]$node.StateDirectory) -ine $nodeRoot) { throw 'The Office state location changed.' }
    $state = Get-Content -LiteralPath (Join-Path $nodeRoot 'node-state.json') -Raw | ConvertFrom-Json
    if ([Guid]$state.OfficeId -ne $OfficeId) { throw 'The connection repair belongs to another Office.' }
    $probeJson = & $executable --probe-control-plane-certificate $ControlPlaneUrl
    if ($LASTEXITCODE -ne 0) { throw 'The Office control-plane certificate probe failed.' }
    $plan = New-CSweetOfficeConnectionConfiguration $node $ControlPlaneUrl $ExpectedSha256 ($probeJson | ConvertFrom-Json)
    $trustPath = Join-Path $nodeRoot 'control-plane-trust.json'
    $priorPin = if ($node.PSObject.Properties['ControlPlaneCertificateSha256']) { [string]$node.ControlPlaneCertificateSha256 } else { '' }
    if (-not $priorPin -and $node.PSObject.Properties['ControlPlaneTrustFilePath'] -and
        (Test-Path -LiteralPath $node.ControlPlaneTrustFilePath -PathType Leaf)) {
        $priorPin = [string](Get-Content -LiteralPath $node.ControlPlaneTrustFilePath -Raw | ConvertFrom-Json).certificateSha256
    }
    if ($priorPin -ieq $plan.Pin -and [string]$node.ControlPlaneUrl -eq $plan.Node.ControlPlaneUrl) { return $false }
    $plan.Node | Add-Member -NotePropertyName ControlPlaneTrustFilePath -NotePropertyValue $trustPath -Force
    $oldConfiguration = [IO.File]::ReadAllText($configurationPath)
    $oldTrust = if (Test-Path -LiteralPath $trustPath) { [IO.File]::ReadAllText($trustPath) } else { $null }
    Stop-Service -Name 'CSweet.Office.Node' -ErrorAction Stop
    $writeStarted = $false
    try {
        foreach ($targetPath in @($nodeRoot, $trustPath, $configurationPath)) {
            if ((Test-Path -LiteralPath $targetPath) -and
                ((Get-Item -LiteralPath $targetPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Office connection repair refuses linked state or configuration paths.'
            }
        }
        $writeStarted = $true
        [IO.File]::WriteAllText($trustPath, (@{ schemaVersion = 1; certificateSha256 = $plan.Pin } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $nodeSid = ([Security.Principal.NTAccount]::new('NT SERVICE\CSweet.Office.Node')).Translate([Security.Principal.SecurityIdentifier]).Value
        & "$env:SystemRoot\System32\icacls.exe" $trustPath '/inheritance:r' '/grant:r' "*$nodeSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Office public trust file could not be protected.' }
        $configuration.CSweet.Office.Node = $plan.Node
        [IO.File]::WriteAllText($configurationPath, ($configuration | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    }
    catch {
        if ($writeStarted) {
            [IO.File]::WriteAllText($configurationPath, $oldConfiguration)
            if ($null -ne $oldTrust) { [IO.File]::WriteAllText($trustPath, $oldTrust) }
        }
        throw
    }
    finally { Start-Service -Name 'CSweet.Office.Node' -ErrorAction Stop }
    Write-Host 'Reconciled the verified Office connection; identity and workload state were preserved.'
    return $true
}
