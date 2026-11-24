# These settings connect the services installed by this MSI on the same machine.
# Deployment settings stay in the original service profiles.
function Get-ProfileValue($Profile, [string] $Key) {
    $property = $Profile.PSObject.Properties[$Key]
    if ($property) { return $property.Value }
    $parts = $Key -split ':', 2
    if ($parts.Length -eq 2) {
        $section = $Profile.PSObject.Properties[$parts[0]]
        if ($section -and $null -ne $section.Value) { return Get-ProfileValue $section.Value $parts[1] }
    }
}

function Get-LocalServiceUrl($Profile, [string] $Default) {
    $endpoints = Get-ProfileValue $Profile 'Kestrel:Endpoints'
    $urls = @(if ($endpoints) {
        foreach ($endpoint in $endpoints.PSObject.Properties) { Get-ProfileValue $endpoint.Value 'Url' }
    } else {
        foreach ($property in $Profile.PSObject.Properties) {
            if ($property.Name -match '^Kestrel:Endpoints:[^:]+:Url$') { $property.Value }
        }
    })
    if (!$urls.Count) {
        $binding = Get-ProfileValue $Profile 'Urls'
        if ($binding) { $urls = @($binding.Split(';')) } else { return $Default }
    }
    $local = @(foreach ($url in $urls) {
        $parsed = $null
        if ([uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -and $parsed.IsLoopback -and
            $parsed.Scheme -in 'http','https' -and !$parsed.UserInfo -and !$parsed.Query -and !$parsed.Fragment) { $parsed.AbsoluteUri }
    })
    if ($local.Count -eq 1) { return $local[0] }
}

function Get-LocalMcpOrigin($AgentProfile, $PackagedAgentSettings) {
    $key = 'Kestrel:Endpoints:Mcp:Url'
    $binding = Get-ProfileValue $AgentProfile $key
    if (!$binding) { $binding = Get-ProfileValue $PackagedAgentSettings $key }
    if (!$binding) { return }

    # A wildcard is a listen address. The gateway calls this service on the same machine.
    $address = $binding -replace '^(https?://)(\*|\+|0\.0\.0\.0|\[::\])(?=[:/]|$)', '$1localhost'
    $uri = $null
    if ([uri]::TryCreate($address, [UriKind]::Absolute, [ref]$uri) -and $uri.IsLoopback -and
        $uri.Scheme -in 'http','https' -and !$uri.UserInfo -and !$uri.Query -and !$uri.Fragment -and
        $uri.AbsolutePath -eq '/' -and $uri.Port -gt 0) { return $uri.AbsoluteUri }
}

function Get-HeadquartersDefaults([hashtable] $Profiles, [string] $SourceKey, [string] $BrokerKey, $PackagedAgentSettings) {
    $result = @{}
    $clients = @('CSweet.Api','CSweet.WorkerHost','CSweet.AgentHost')
    $broker = @('CSweet.Api','CSweet.AgentHost')
    $servers = [ordered]@{
        'CSweet.GitHost' = @('GitHostBaseUrl', 'http://127.0.0.1:56173')
        'CSweet.SourceControlProvisionerHost' = @('ProvisionerHostBaseUrl', 'http://127.0.0.1:56172')
    }
    $prefix = 'CSweet:SourceControl'
    foreach ($name in ($clients + @($servers.Keys))) { $result[$name] = @{} }
    $customSource = @(
        foreach ($name in $servers.Keys) {
            foreach ($setting in 'SharedKeyBase64','KeyFile') { Get-ProfileValue $Profiles[$name] "TrustedServiceAuthentication:$setting" }
        }
        foreach ($name in $clients) {
            foreach ($setting in 'TrustedServiceKeyBase64','TrustedServiceKeyFile') { Get-ProfileValue $Profiles[$name] "${prefix}:$setting" }
        }
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) }
    foreach ($name in $servers.Keys) {
        $endpoint = $servers[$name]
        $url = Get-LocalServiceUrl $Profiles[$name] $endpoint[1]
        if ($url -eq $endpoint[1]) { $result[$name]['Urls'] = $url }
        if (!$customSource) { $result[$name]['TrustedServiceAuthentication:SharedKeyBase64'] = $SourceKey }
        foreach ($client in $clients) {
            if ($client -eq 'CSweet.AgentHost' -and $name -eq 'CSweet.SourceControlProvisionerHost') { continue }
            if ($url) { $result[$client]["${prefix}:$($endpoint[0])"] = $url }
        }
    }
    if (!$customSource) {
        foreach ($client in $clients) { $result[$client]["${prefix}:TrustedServiceKeyBase64"] = $SourceKey }
    }
    $customBroker = @($broker | ForEach-Object { Get-ProfileValue $Profiles[$_] "${prefix}:AgentBrokerKeyBase64" }) |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) }
    if (!$customBroker) {
        foreach ($name in $broker) { $result[$name]["${prefix}:AgentBrokerKeyBase64"] = $BrokerKey }
    }
    $apiUrl = Get-LocalServiceUrl $Profiles['CSweet.Api'] $null
    if ($apiUrl) { $result['CSweet.AgentHost']["${prefix}:CoreBrokerBaseUrl"] = $apiUrl }
    $gateway = 'CSweet.ExecutionGateway'
    $target = 'CSweet:AgentRuntime:AgentHostBroker:BaseUrl'
    $result[$gateway] = @{}
    if (!(Get-ProfileValue $Profiles[$gateway] $target) -and !(Get-ProfileValue $Profiles[$gateway] 'AGENTHOST_MCP')) {
        $origin = Get-LocalMcpOrigin $Profiles['CSweet.AgentHost'] $PackagedAgentSettings
        if ($origin) { $result[$gateway][$target] = $origin }
    }
    return $result
}

function Write-HeadquartersSettings([string] $Path, [hashtable] $Settings, [object[]] $Readers, [switch] $CreateOnly) {
    $pending = Join-Path $root ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($pending, ($Settings | ConvertTo-Json))
        Set-PrivateAcl $pending $Readers $null
        if (!$CreateOnly -and [IO.File]::Exists($Path)) {
            Assert-ProtectedPath $Path
            [IO.File]::Replace($pending, $Path, [Management.Automation.Language.NullString]::Value)
        } else { [IO.File]::Move($pending, $Path) }
        Set-PrivateAcl $Path $Readers $null
    } finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
}

function Initialize-HeadquartersServiceSettings([hashtable] $Profiles, $PackagedAgentSettings) {
    $path = Join-Path $root 'local-service-keys.json'
    if (![IO.File]::Exists($path)) {
        $keys = @{}
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            foreach ($group in 'SourceControl','AgentBroker') {
                $bytes = [byte[]]::new(32)
                $rng.GetBytes($bytes)
                $keys[$group] = [Convert]::ToBase64String($bytes)
            }
        } finally { $rng.Dispose() }
        Write-HeadquartersSettings $path $keys @() -CreateOnly
    }
    Assert-ProtectedPath $path
    Set-PrivateAcl $path @() $null
    $keys = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    foreach ($group in 'SourceControl','AgentBroker') {
        if ([Convert]::FromBase64String($keys.$group).Length -ne 32) { throw 'Restore the installer-managed Headquarters authentication keys.' }
    }
    $defaults = Get-HeadquartersDefaults $Profiles $keys.SourceControl $keys.AgentBroker $PackagedAgentSettings
    foreach ($name in $defaults.Keys) {
        Write-HeadquartersSettings (Join-Path $root "$name.defaults.json") $defaults[$name] @($sids[$name])
    }
}
