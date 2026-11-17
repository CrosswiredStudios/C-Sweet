$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../src/CSweet.Api/Setup/Sync-CSweetOfficeConnection.ps1')
$pin = 'a' * 64
$node = [pscustomobject]@{ ControlPlaneUrl = 'https://old.example.test/'; ControlPlaneCertificateSha256 = 'b' * 64
    StateDirectory = 'D:\protected\node'; AssistedSetupSessionId = 'retained-session'; MaximumConcurrentWorkloads = 6 }
$certificate = [pscustomobject]@{ certificateSha256 = $pin; policyErrors = [int][Net.Security.SslPolicyErrors]::RemoteCertificateChainErrors
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
        name { $observed.policyErrors = [int][Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch }
        expired { $observed.notAfter = [DateTime]::UtcNow.AddSeconds(-1) }
        missing { $observed.policyErrors = [int][Net.Security.SslPolicyErrors]::RemoteCertificateNotAvailable }
        http { $url = 'http://current.example.test/' }
    }
    $rejected = $false
    try { $null = New-CSweetOfficeConnectionConfiguration $node $url $pin $observed } catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe connection '$failure' was accepted." }
}
Write-Output 'Office connection repair: verified rotation, retained state, and five unsafe cases passed.'
$baseCommand = '"D:\protected\node\CSweet.Office.Node.exe" --contentRoot "D:\protected"'
foreach ($suffix in @('', ' --environment Production', ' --environment "Production"')) {
    $command = Read-CSweetOfficeServiceCommand ($baseCommand + $suffix)
    if ($command.ContentRoot -ne 'D:\protected' -or $command.Executable -ne 'D:\protected\node\CSweet.Office.Node.exe') { throw 'The installed service command was not retained.' }
}
foreach ($suffix in @(' --environment Development', ' --other-option dangerous', ' --environment Production --other-option dangerous')) {
    $rejected = $false
    try { $null = Read-CSweetOfficeServiceCommand ($baseCommand + $suffix) } catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsupported service command was accepted.' }
}
Write-Output 'Office service command: legacy and Production registrations accepted; unsupported arguments rejected.'
