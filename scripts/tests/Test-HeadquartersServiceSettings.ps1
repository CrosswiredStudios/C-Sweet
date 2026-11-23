[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../release/windows/HeadquartersServiceSettings.ps1')

function Assert-Equal($Actual, $Expected, [string] $Reason) {
    if ($Actual -cne $Expected) { throw $Reason }
}

$profiles = @{}
foreach ($name in 'CSweet.Api','CSweet.AgentHost','CSweet.WorkerHost','CSweet.GitHost','CSweet.SourceControlProvisionerHost') {
    $profiles[$name] = '{}' | ConvertFrom-Json
}
$profiles['CSweet.Api'] = '{"Kestrel":{"Endpoints":{"Local":{"Url":"https://localhost:17443"}}}}' | ConvertFrom-Json
$local = Get-HeadquartersDefaults $profiles 'source-test' 'broker-test'
$prefix = 'CSweet:SourceControl:'
foreach ($server in 'CSweet.GitHost','CSweet.SourceControlProvisionerHost') {
    Assert-Equal $local[$server]['TrustedServiceAuthentication:SharedKeyBase64'] 'source-test' 'Missing server key'
    Assert-Equal $local[$server].ContainsKey($prefix + 'AgentBrokerKeyBase64') $false 'Broker key disclosed to a source-control service'
}
foreach ($client in 'CSweet.Api','CSweet.WorkerHost','CSweet.AgentHost') {
    Assert-Equal $local[$client][$prefix + 'TrustedServiceKeyBase64'] 'source-test' 'Missing client key'
    Assert-Equal $local[$client][$prefix + 'GitHostBaseUrl'] $local['CSweet.GitHost']['Urls'] 'Git endpoint mismatch'
}
Assert-Equal $local['CSweet.Api'][$prefix + 'ProvisionerHostBaseUrl'] $local['CSweet.SourceControlProvisionerHost']['Urls'] 'Provisioner endpoint mismatch'
Assert-Equal $local['CSweet.WorkerHost'].ContainsKey($prefix + 'AgentBrokerKeyBase64') $false 'Broker key disclosed to WorkerHost'
Assert-Equal $local['CSweet.AgentHost'][$prefix + 'AgentBrokerKeyBase64'] 'broker-test' 'Missing broker key'
Assert-Equal $local['CSweet.AgentHost'][$prefix + 'CoreBrokerBaseUrl'] 'https://localhost:17443/' 'API endpoint mismatch'
Assert-Equal $local.Count 5 'Unexpected credential recipient'

$profiles['CSweet.GitHost'] = '{"TrustedServiceAuthentication:KeyFile":"custom","Urls":"https://localhost:6001"}' | ConvertFrom-Json
$profiles['CSweet.Api'] = '{"CSweet":{"SourceControl":{"AgentBrokerKeyBase64":"custom"}},"Kestrel:Endpoints:Public:Url":"https://example.test"}' | ConvertFrom-Json
$custom = Get-HeadquartersDefaults $profiles 'unused' 'unused'
Assert-Equal $custom['CSweet.GitHost'].ContainsKey('TrustedServiceAuthentication:SharedKeyBase64') $false 'Key-file configuration masked'
Assert-Equal $custom['CSweet.Api'].ContainsKey($prefix + 'TrustedServiceKeyBase64') $false 'Implicit key generated for custom deployment'
Assert-Equal $custom['CSweet.AgentHost'].ContainsKey($prefix + 'AgentBrokerKeyBase64') $false 'Custom broker key masked'
Assert-Equal $custom['CSweet.AgentHost'].ContainsKey($prefix + 'CoreBrokerBaseUrl') $false 'Public API inferred as internal broker'
Assert-Equal $custom['CSweet.WorkerHost'][$prefix + 'GitHostBaseUrl'] 'https://localhost:6001/' 'Custom local listener ignored'

# Exercise interrupted setup and repair on an ordinary temporary directory.
# The installed-VM test covers service account ACLs under Program Files.
$root = Join-Path ([IO.Path]::GetTempPath()) ('csweet-hq-settings-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$sids = @{}
function Assert-ProtectedPath([string] $Path) {
    if ([IO.Path]::GetDirectoryName($Path) -cne $root) { throw 'Unexpected test path' }
}
function Set-PrivateAcl([string] $Path, [object[]] $Readers, $Writer) { Assert-ProtectedPath $Path }
try {
    Initialize-HeadquartersServiceSettings $profiles
    $seed = Join-Path $root 'local-service-keys.json'
    $original = [IO.File]::ReadAllText($seed)
    $keys = $original | ConvertFrom-Json
    if ($keys.SourceControl -ceq $keys.AgentBroker) { throw 'Separate credentials required' }
    [IO.File]::Delete((Join-Path $root 'CSweet.AgentHost.defaults.json'))
    Initialize-HeadquartersServiceSettings $profiles
    Assert-Equal ([IO.File]::ReadAllText($seed)) $original 'Repair rotated the keys'
    Assert-Equal ([IO.File]::Exists((Join-Path $root 'CSweet.AgentHost.defaults.json'))) $true 'Repair did not restore the profile'
    'Headquarters tests passed for fresh profiles, explicit authentication and interrupted setup.'
} finally {
    foreach ($file in [IO.Directory]::GetFiles($root)) {
        Assert-ProtectedPath $file
        [IO.File]::Delete($file)
    }
    [IO.Directory]::Delete($root)
}
