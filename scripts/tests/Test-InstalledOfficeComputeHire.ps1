# Run inside the disposable Windows Office through PowerShell Direct. Prepare before reboot.
[CmdletBinding(DefaultParameterSetName='Run')]
param(
    [Parameter(Mandatory, ParameterSetName='Prepare')][switch] $Prepare,
    [Parameter(Mandatory, ParameterSetName='Run')][datetime] $PreviousBootUtc,
    [Parameter(Mandatory, ParameterSetName='Run')][uri] $Headquarters,
    [Parameter(Mandatory, ParameterSetName='Run')][pscredential] $Owner,
    [Parameter(Mandatory, ParameterSetName='Run')][guid] $OrganizationId,
    [Parameter(Mandatory, ParameterSetName='Run')][guid] $TeamId,
    [Parameter(Mandatory, ParameterSetName='Run')][string] $AgentReference,
    [Parameter(Mandatory, ParameterSetName='Run')][guid] $RunId,
    [Parameter(Mandatory, ParameterSetName='Run')][string] $GuestProbe,
    [Parameter(Mandatory, ParameterSetName='Run')][ValidatePattern('^[a-fA-F0-9]{64}$')][string] $GuestProbeSha256,
    [hashtable] $ConfigurationSettings = @{}
)
$ErrorActionPreference = 'Stop'
if ((Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine') { throw 'A dedicated Hyper-V lab guest is required.' }
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime()
$subcategories = @('{0CCE9215-69AE-11D9-BED3-505054503030}', '{0CCE922B-69AE-11D9-BED3-505054503030}')
foreach ($id in $subcategories) {
    if ($Prepare) {
        & auditpol.exe /set "/subcategory:$id" /success:enable | Out-Null
        if ($LASTEXITCODE) { throw 'Unable to enable logon and process auditing in the guest.' }
    }
    $row = & auditpol.exe /get "/subcategory:$id" /r | ConvertFrom-Csv
    if ($LASTEXITCODE -or @($row).Count -ne 1 -or [int](@($row.PSObject.Properties.Value)[-1]) -notin 1,3) {
        throw 'Logon and process success auditing must both be active.'
    }
}
if ($Prepare) { return $boot }
if ($boot -le $PreviousBootUtc.ToUniversalTime()) { throw 'The guest must reboot after audit preparation.' }
$policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
if ($policy.EnableLUA -ne 1 -or $policy.PromptOnSecureDesktop -ne 1 -or $policy.ConsentPromptBehaviorAdmin -eq 0) {
    throw 'The UAC policy does not exercise a secure desktop consent prompt.'
}
if (!$Headquarters.IsLoopback -or $Headquarters.UserInfo -or $Headquarters.Scheme -notin 'http','https') {
    throw 'Supply the installed Headquarters loopback origin in this lab VM.'
}
$probe = Get-Item -LiteralPath $GuestProbe
if ($probe.Name -ne 'CSweet.Compute.TransportProbe.exe' -or (Get-FileHash -LiteralPath $probe.FullName).Hash -ine $GuestProbeSha256) {
    throw 'The acceptance probe differs from the selected build.'
}
function Assert-UnattendedBoot {
    $events = Get-WinEvent -FilterHashtable @{ LogName='Security'; ID=4624,4688,1102,4719; StartTime=$boot.ToLocalTime() }
    $processes = 0
    $logons = 0
    foreach ($event in $events) {
        if ($event.Id -eq 1102) { throw 'The Security log was cleared during the acceptance run.' }
        $fields = @{}
        ([xml]$event.ToXml()).Event.EventData.Data | ForEach-Object { $fields[$_.Name] = $_.'#text' }
        if ($event.Id -eq 4719 -and $fields.SubcategoryGuid -in $subcategories -and $fields.AuditPolicyChanges -match '%%8448') {
            throw 'A required success audit was removed during the run.'
        }
        if ($event.Id -eq 4624) {
            $logons++
            if ($fields.LogonType -in '2','7','10','11','12','13' -and $fields.TargetUserName -notmatch '^(DWM|UMFD)-\d+$') {
                throw 'An interactive user signed in after reboot.'
            }
        }
        if ($event.Id -eq 4688) {
            $processes++
            if ([IO.Path]::GetFileName($fields.NewProcessName) -ieq 'consent.exe') { throw 'Windows showed a consent prompt after reboot.' }
        }
    }
    if (!$logons -or !$processes) { throw 'The Security log lacks positive logon or process audit evidence.' }
}
Assert-UnattendedBoot
foreach ($service in 'CSweet.Api','CSweet.AgentHost','CSweet.WorkerHost','CSweet.ExecutionGateway','CSweet.Office.Node','CSweet.Office.RuntimeHost') {
    if ((Get-Service $service).Status -ne 'Running') { throw "Installed service $service is unavailable." }
}
$legacyProviders = @(Get-Service -Name 'CSweet.Compute.HyperV*' -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running')
if ($legacyProviders.Count) { throw 'The acceptance VM still has a legacy compute provider running.' }
$origin = $Headquarters.GetLeftPart([UriPartial]::Authority)
$web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$headers = @{}
function Get-LabApi([string] $Path) {
    Invoke-RestMethod "$origin$Path" -WebSession $web -Headers $headers -MaximumRedirection 0 -TimeoutSec 60
}
function Send-LabApi([string] $Path, [hashtable] $Body) {
    Invoke-RestMethod "$origin$Path" -Method Post -Body ($Body | ConvertTo-Json -Depth 12) -ContentType 'application/json' -WebSession $web -Headers $headers -MaximumRedirection 0 -TimeoutSec 120
}
$null = Send-LabApi '/api/auth/login' @{email=$Owner.UserName; password=$Owner.GetNetworkCredential().Password}
$session = Get-LabApi '/api/auth/session'
if (!$session.antiforgeryToken) { throw 'The test owner could not establish a Headquarters session.' }
$headers['X-CSWEET-CSRF'] = $session.antiforgeryToken
$hire = "/api/core/organizations/$OrganizationId/hiring"
$team = (Get-LabApi "/api/core/organizations/$OrganizationId/teams/$TeamId").team
if ($team.id -ne $TeamId -or $team.isArchived -or !$team.leadOrganizationUserId) {
    throw 'The hire requires an active team with a reporting manager.'
}
$key = "office-compute-test:$($RunId.ToString('N'))"
$preview = Send-LabApi "$hire/marketplace/preview" @{ agentReference=$AgentReference; roleTitle='Game Engineer'
    employeeDisplayName="Game Engineer $($RunId.ToString('N').Substring(0,8))"; teamId=$TeamId
    reportsToOrganizationUserId=$team.leadOrganizationUserId; idempotencyKey="$key-preview" }
if ($preview.agentReference -ne $AgentReference -or $preview.agentName -ne 'Adrian Silva' -or $preview.teamId -ne $TeamId -or
    $preview.reportsToOrganizationUserId -ne $team.leadOrganizationUserId -or $preview.requestedCapabilities -notcontains 'compute.provision.v1' -or
    $preview.subscriptions -notcontains 'com.csweet.compute.available.v1' -or $preview.subscriptions -notcontains 'com.csweet.project-assignment.assigned.v1') {
    throw 'The catalog preview does not contain the expected developer release and team.'
}
$op = Send-LabApi "$hire/workflows/$($preview.workflowId)/confirm" @{ idempotencyKey="$key-confirm"; configurationSettings=$ConfigurationSettings }
$deadline = (Get-Date).AddMinutes(20)
while ($op.status -in 'Starting','Queued','Building','CompletingHire' -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $op = Get-LabApi "/api/core/hiring/operations/$($op.id)"
}
if ($op.status -ne 'Succeeded' -or !$op.resultOrganizationUserId -or !$op.resultAgentInstallationId) { throw "Hire stopped at $($op.status)." }
do {
    $dashboard = Get-LabApi "/api/organizations/$OrganizationId/compute"
    $rows = @($dashboard.resources | Where-Object { $_.employeeId -eq $op.resultOrganizationUserId })
    if ($rows.Count -gt 1 -or $rows.state -contains 'Failed') { throw 'Duplicate or failed employee environment.' }
    if ($rows.Count -eq 1 -and $rows[0].state -eq 'Ready') { break }
    Start-Sleep -Seconds 3
} while ((Get-Date) -lt $deadline)
if ($rows.Count -ne 1 -or $rows[0].state -ne 'Ready' -or $rows[0].operatingSystem -ne 'linux') { throw 'No ready Linux environment for this hire.' }
$envId = [guid]$rows[0].id
$vm = @(Get-VM -Name "CSweet-ComputeEnvironment-$($envId.ToString('N'))")
if ($vm.Count -ne 1 -or $vm[0].State -ne 'Running' -or @(Get-VMNetworkAdapter -VM $vm[0]).Count -ne 0) {
    throw 'Hyper-V did not confirm exactly one isolated running VM for the Headquarters environment.'
}
& $probe.FullName check-office-guest $vm[0].Id.ToString('D') | Out-Host
if ($LASTEXITCODE) { throw 'The actual Linux guest failed the command and file round trip.' }
Assert-UnattendedBoot
[pscustomobject]@{ BootUtc=$boot; HireOperation=$op.id; Installation=$op.resultAgentInstallationId
    Environment=$envId; HyperVId=$vm[0].Id; GuestCommand='Passed'; InteractiveUserLogons=0; ConsentProcesses=0 }
