$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../src/CSweet.Api/Setup/Sync-CSweetOfficeConnection.ps1')
$pin = 'a' * 64
$node = [pscustomobject]@{ ControlPlaneUrl = 'https://old.example.test/'; ControlPlaneCertificateSha256 = 'b' * 64
    StateDirectory = 'D:\protected\node'; AssistedSetupSessionId = 'retained-session'; MaximumConcurrentWorkloads = 6 }
$certificate = [pscustomobject]@{ certificateSha256 = $pin; policyErrors = 1
    notBefore = [DateTime]::UtcNow.AddDays(-1); notAfter = [DateTime]::UtcNow.AddDays(1) }
$before = $node | ConvertTo-Json -Compress
$plan = New-CSweetOfficeConnectionConfiguration $node 'https://current.example.test/' $pin $certificate
if ($plan.Node.ControlPlaneUrl -ne 'https://current.example.test/' -or $plan.Pin -ne $pin) { throw 'The verified connection was not updated.' }
if ($plan.Node.PSObject.Properties['ControlPlaneCertificateSha256']) { throw 'The stale inline pin overrides the trust file.' }
if ($plan.Node.StateDirectory -ne $node.StateDirectory -or $plan.Node.AssistedSetupSessionId -ne $node.AssistedSetupSessionId -or
    $plan.Node.MaximumConcurrentWorkloads -ne $node.MaximumConcurrentWorkloads -or ($node | ConvertTo-Json -Compress) -ne $before) {
    throw 'Connection repair changed identity, capacity or the original configuration.'
}
foreach ($failure in @('pin', 'name', 'expired', 'missing', 'http')) {
    $observed = $certificate | ConvertTo-Json | ConvertFrom-Json
    $url = 'https://current.example.test/'
    switch ($failure) {
        pin { $observed.certificateSha256 = 'c' * 64 }
        name { $observed.policyErrors = 2 }
        expired { $observed.notAfter = [DateTime]::UtcNow.AddSeconds(-1) }
        missing { $observed.policyErrors = 4 }
        http { $url = 'http://current.example.test/' }
    }
    $rejected = $false
    try { $null = New-CSweetOfficeConnectionConfiguration $node $url $pin $observed } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe connection '$failure' was accepted." }
}
Write-Output 'Office connection repair: verified rotation, retained state, and five unsafe cases passed.'
