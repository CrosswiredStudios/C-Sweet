[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$HeadquartersUri,
    [Parameter(Mandatory)][guid]$OfficeId,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$OwnerSession,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$TargetVersion,
    [ValidateRange(60, 7200)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
if ($OfficeId -eq [guid]::Empty -or $HeadquartersUri.Scheme -ne 'https' -or $HeadquartersUri.UserInfo -or
    $HeadquartersUri.Fragment -or $HeadquartersUri.Query) {
    throw 'Supply an Office ID and the HTTPS Headquarters address for the acceptance deployment.'
}
$authority = $HeadquartersUri.AbsoluteUri.TrimEnd('/')
$setup = "$authority/api/setup/execution-capacity"
$request = @{ WebSession=$OwnerSession; MaximumRedirection=0; TimeoutSec=30; ErrorAction='Stop' }
$auth = Invoke-RestMethod @request -Uri "$authority/api/auth/session"
if (-not $auth.isAuthenticated -or -not $auth.antiforgeryToken) { throw 'The test needs an authenticated Headquarters owner session.' }
$request.Headers = @{'X-CSWEET-CSRF'=$auth.antiforgeryToken}
$fleet = Invoke-RestMethod @request -Uri $setup
$office = @($fleet.nodes | Where-Object { $_.id -eq $OfficeId })
if ($office.Count -ne 1 -or $office[0].operatingSystem -ne 'windows') { throw 'The selected Windows Office is unavailable.' }
$office = $office[0]
$updating = -not [string]::IsNullOrEmpty($TargetVersion)
if ($updating -and ([version]$TargetVersion -le [version]$office.nodeVersion)) { throw 'Choose a newer released Office version to exercise the upgrade.' }
$body = @{presetKey='custom'; upgradeOfficeId=$OfficeId; repair=(-not $updating); automaticUpdate=$updating; useLocalSource=$false}
if ($updating) { $body.targetVersion = $TargetVersion }
$started = [DateTimeOffset]::UtcNow
$created = Invoke-RestMethod @request -Uri "$setup/local-sessions" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json)
$session = $created.session
if (-not $created.succeeded -or $created.errorCode -or $session.upgradeOfficeId -ne $OfficeId -or
    $session.launchMethod -ne 'agent' -or -not $session.launchUri) {
    throw 'Headquarters did not create a new maintenance-service request for this Office.'
}
$statusUri = "$setup/local-sessions/$($session.id)"
# Only Headquarters receives the one-use handoff. Never pass the URI to the shell or write it to the test log.
$launch = Invoke-RestMethod @request -Uri "$statusUri/launch" -Method Post -ContentType 'application/json' -Body (@{launchUri=$session.launchUri} | ConvertTo-Json)
if (-not $launch.succeeded -or $launch.session.launchMethod -ne 'agent') { throw 'The Office maintenance launch was rejected.' }
$deadline = $started.AddSeconds($TimeoutSeconds)
do {
    $result = Invoke-RestMethod @request -Uri $statusUri
    $state = $result.session.state
    if ($state -in 'failed','expired','revoked','removed','recovery') { throw "Office maintenance ended in $state ($($result.session.errorCode))." }
    $observed = @($result.status.nodes | Where-Object { $_.id -eq $OfficeId })
    if ($result.succeeded -and $state -eq 'ready' -and $observed.Count -eq 1 -and
        $observed[0].lastHeartbeatAt -and [DateTimeOffset]$observed[0].lastHeartbeatAt -ge $started -and
        (-not $updating -or $observed[0].nodeVersion -eq $TargetVersion)) {
        [pscustomobject]@{ OfficeId=$OfficeId; SessionId=$session.id; Operation=$session.recoveryAction
            Version=$observed[0].nodeVersion; LaunchMethod=$session.launchMethod; Heartbeat=$observed[0].lastHeartbeatAt }
        return
    }
    Start-Sleep -Seconds 3
} while ([DateTimeOffset]::UtcNow -lt $deadline)
throw "Office maintenance timed out in $state ($($result.session.phaseKey))."
