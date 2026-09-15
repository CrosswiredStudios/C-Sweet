[CmdletBinding()]
param([string] $ConfigurationPath = (Join-Path $PSScriptRoot 'ComputeServiceRecovery.json'))
$ErrorActionPreference = 'Stop'
$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ($configuration.resetSeconds -lt 0 -or @($configuration.restartDelaysMilliseconds).Count -eq 0) {
    throw 'Invalid compute service recovery configuration.'
}
$actions = @($configuration.restartDelaysMilliseconds | ForEach-Object {
    if ($_ -le 0 -or $_ -gt [uint32]::MaxValue) { throw 'Invalid compute service restart delay.' }
    'restart/' + [uint32]$_
}) -join '/'
$serviceControl = Join-Path ([Environment]::GetFolderPath('System')) 'sc.exe'
& $serviceControl failure 'CSweet.Compute.HyperV' 'reset=' ([uint32]$configuration.resetSeconds).ToString() 'actions=' $actions
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure compute service recovery.' }
& $serviceControl failureflag 'CSweet.Compute.HyperV' '1'
if ($LASTEXITCODE -ne 0) { throw 'Failed to enable compute service recovery after an error exit.' }
