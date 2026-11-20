[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$VMId,
    [Parameter(Mandatory)][pscredential]$GuestCredential,
    [Parameter(Mandatory)][uri]$HeadquartersUri,
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
if ($HeadquartersUri.Scheme -notin @('http','https') -or -not $HeadquartersUri.IsLoopback -or
    $HeadquartersUri.UserInfo -or $HeadquartersUri.Query -or $HeadquartersUri.Fragment) {
    throw 'Supply the local Headquarters UI URI inside the test VM.'
}
# The caller reboots the acceptance VM first. PowerShell Direct uses the VM bus
# without opening a console, attaching a network adapter, or starting a service.
Invoke-Command -VMId $VMId -Credential $GuestCredential -ArgumentList $HeadquartersUri.AbsoluteUri,$TimeoutSeconds -ScriptBlock {
    param($uri,$timeout)
    $ErrorActionPreference = 'Stop'
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $expected = @{}
    foreach ($name in @('CSweet.Api','CSweet.AgentHost','CSweet.WorkerHost','CSweet.ExecutionGateway',
        'CSweet.GitHost','CSweet.SourceControlProvisionerHost','CSweet.Office.Node','CSweet.Office.RuntimeHost')) {
        $expected[$name] = "NT SERVICE\$name"
    }
    $expected['CSweet.Office.Maintenance'] = 'LocalSystem'
    $end = [DateTime]::UtcNow.AddSeconds($timeout)
    do {
        $services = @(Get-CimInstance Win32_Service -Filter "Name LIKE 'CSweet.%'" | Where-Object { $expected.ContainsKey($_.Name) })
        $bad = @($services | Where-Object { $_.State -ne 'Running' -or $_.StartMode -ne 'Auto' -or $_.StartName -ine $expected[$_.Name] })
        if ($services.Count -eq $expected.Count -and $bad.Count -eq 0) {
            try { $http = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri $uri).StatusCode } catch { $http = $null }
            if ($http -eq 200) { break }
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $end)
    if ($services.Count -ne $expected.Count -or $bad.Count -or $http -ne 200) {
        $missing = $expected.Keys | Where-Object { $_ -notin $services.Name }
        throw "Cold boot failed. Missing services [$($missing -join ', ')], incorrect state or identity [$($bad.Name -join ', ')], HTTP $http."
    }
    # Security 4624 also covers a console/RDP user who logged in and left before this probe.
    # Require success auditing for logons so an empty history can support the test.
    $logonSubcategory = '{0CCE9215-69AE-11D9-BED3-505054503030}'
    $audit = & auditpol.exe /get "/subcategory:$logonSubcategory" /r
    $auditExit = $LASTEXITCODE
    # auditpol localizes its CSV headings and setting descriptions. The numeric setting
    # is the final column (success=1, success and failure=3), on the row for this GUID.
    $rows = @($audit | ConvertFrom-Csv | Where-Object { $logonSubcategory -in $_.PSObject.Properties.Value })
    if ($auditExit -ne 0 -or $rows.Count -ne 1 -or @($rows[0].PSObject.Properties.Value)[-1] -notin '1','3') {
        throw 'The Windows logon success audit setting could not be confirmed.'
    }
    $filter = @{LogName='Security'; Id=4624; StartTime=$boot}
    try { $events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop) }
    catch { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }; $events = @() }
    foreach ($event in $events) {
        $fields = @{}
        foreach ($field in ([xml]$event.ToXml()).Event.EventData.Data) { $fields[$field.Name] = $field.'#text' }
        if ($fields.LogonType -in '2','10','11','12' -and
            $fields.TargetUserSid -notmatch '^S-1-5-(18$|19$|20$|90-|96-)') {
            throw 'An interactive user logged in after this boot.'
        }
    }
    if ((Get-CimInstance Win32_ComputerSystem).UserName) { throw 'An interactive console session is active.' }
    [pscustomobject]@{ Machine=$env:COMPUTERNAME; Boot=$boot; AutomaticServices=$services.Count; HeadquartersHttp=$http; InteractiveLogons=0 }
}
