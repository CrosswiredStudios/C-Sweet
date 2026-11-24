[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../release/windows/HeadquartersServiceSettings.ps1')

function Assert-Equal($Actual, $Expected, [string] $Reason) {
    if ($Actual -cne $Expected) { throw $Reason }
}

$profiles = @{}
foreach ($name in 'CSweet.Api','CSweet.AgentHost','CSweet.WorkerHost','CSweet.GitHost','CSweet.SourceControlProvisionerHost','CSweet.ExecutionGateway') {
    $profiles[$name] = '{}' | ConvertFrom-Json
}
$profiles['CSweet.Api'] = '{"Kestrel":{"Endpoints":{"Local":{"Url":"https://localhost:17443"}}}}' | ConvertFrom-Json
$packaged = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../src/CSweet.AgentHost/appsettings.json') -Raw | ConvertFrom-Json
$local = Get-HeadquartersDefaults $profiles 'source-test' 'broker-test' $packaged
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
$gateway = 'CSweet.ExecutionGateway'
$target = 'CSweet:AgentRuntime:AgentHostBroker:BaseUrl'
Assert-Equal $local[$gateway][$target] 'http://localhost:8081/' 'Gateway cannot reach the packaged AgentHost MCP listener'
Assert-Equal $local[$gateway].Count 1 'Gateway should receive only the endpoint'
Assert-Equal $local.Count 6 'Unexpected service profile'

$profiles['CSweet.AgentHost'] = '{"Kestrel:Endpoints:Mcp:Url":"https://localhost:17501","Kestrel:Endpoints:Metrics:Url":"http://localhost:17502"}' | ConvertFrom-Json
$customPort = Get-HeadquartersDefaults $profiles 'source-test' 'broker-test' $packaged
Assert-Equal $customPort[$gateway][$target] 'https://localhost:17501/' 'Gateway must select the deployed MCP listener'
foreach ($address in 'http://[::]:17503','http://*:17503','http://+:17503') {
    $profile = [pscustomobject]@{ 'Kestrel:Endpoints:Mcp:Url' = $address }
    Assert-Equal (Get-LocalMcpOrigin $profile $packaged) 'http://localhost:17503/' 'Wildcard listener is not a client address'
}
$profiles['CSweet.AgentHost'] = '{"Kestrel":{"Endpoints":{"Mcp":{"Url":"https://example.test:17501"}}}}' | ConvertFrom-Json
$remote = Get-HeadquartersDefaults $profiles 'source-test' 'broker-test' $packaged
Assert-Equal $remote[$gateway].Count 0 'Remote listener inferred as trusted local broker'
$profiles['CSweet.AgentHost'] = '{}' | ConvertFrom-Json
foreach ($explicit in $target,'AGENTHOST_MCP') {
    $profiles[$gateway] = [pscustomobject]@{ $explicit = 'https://configured.test/' }
    $configured = Get-HeadquartersDefaults $profiles 'source-test' 'broker-test' $packaged
    Assert-Equal $configured[$gateway].Count 0 'Explicit broker route masked by installer'
}
$profiles[$gateway] = '{}' | ConvertFrom-Json

$profiles['CSweet.GitHost'] = '{"TrustedServiceAuthentication:KeyFile":"custom","Urls":"https://localhost:6001"}' | ConvertFrom-Json
$profiles['CSweet.Api'] = '{"CSweet":{"SourceControl":{"AgentBrokerKeyBase64":"custom"}},"Kestrel:Endpoints:Public:Url":"https://example.test"}' | ConvertFrom-Json
$custom = Get-HeadquartersDefaults $profiles 'unused' 'unused' $packaged
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
    Initialize-HeadquartersServiceSettings $profiles $packaged
    $seed = Join-Path $root 'local-service-keys.json'
    $original = [IO.File]::ReadAllText($seed)
    $keys = $original | ConvertFrom-Json
    if ($keys.SourceControl -ceq $keys.AgentBroker) { throw 'Separate credentials required' }
    [IO.File]::Delete((Join-Path $root 'CSweet.AgentHost.defaults.json'))
    Initialize-HeadquartersServiceSettings $profiles $packaged
    $gatewayFile = Join-Path $root "$gateway.defaults.json"
    $gatewaySettings = Get-Content -LiteralPath $gatewayFile -Raw | ConvertFrom-Json
    Assert-Equal $gatewaySettings.$target 'http://localhost:8081/' 'Repair did not produce a usable broker route'
    $profiles[$gateway] = [pscustomobject]@{ $target = 'https://configured.test/' }
    Initialize-HeadquartersServiceSettings $profiles $packaged
    $repaired = Get-Content -LiteralPath $gatewayFile -Raw | ConvertFrom-Json
    Assert-Equal ($null -eq $repaired.PSObject.Properties[$target]) $true 'Repair retained an obsolete local route'
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
