[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$VMId,
    [Parameter(Mandatory)][pscredential]$GuestCredential,
    [Parameter(Mandatory)][uri]$HeadquartersUri,
    [switch]$ColdBoot,
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
if ($HeadquartersUri.Scheme -notin @('http','https') -or -not $HeadquartersUri.IsLoopback -or
    $HeadquartersUri.UserInfo -or $HeadquartersUri.Query -or $HeadquartersUri.Fragment) {
    throw 'Supply the local Headquarters UI URI inside the test VM.'
}
$probe = {
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
    # The native policy flags are independent of auditpol's localized CSV layout.
    if (-not ('CSweetAcceptance.LogonPolicy' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace CSweetAcceptance {
    public static class LogonPolicy {
        [StructLayout(LayoutKind.Sequential)]
        private struct Policy { public Guid Subcategory; public uint Flags; public Guid Category; }
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool AuditQuerySystemPolicy(ref Guid subcategory, uint count, out IntPtr buffer);
        [DllImport("advapi32.dll")]
        private static extern void AuditFree(IntPtr buffer);
        public static bool RecordsSuccess() {
            var id = new Guid("0cce9215-69ae-11d9-bed3-505054503030");
            IntPtr buffer;
            if (!AuditQuerySystemPolicy(ref id, 1, out buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var policy = (Policy)Marshal.PtrToStructure(buffer, typeof(Policy));
                return policy.Subcategory == id && (policy.Flags & 1) != 0;
            } finally { AuditFree(buffer); }
        }
    }
}
'@
    }
    if (-not [CSweetAcceptance.LogonPolicy]::RecordsSuccess()) { throw 'Successful Windows logons are not audited.' }
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

if (-not $ColdBoot) {
    Invoke-Command -VMId $VMId -Credential $GuestCredential -ScriptBlock $probe -ArgumentList $HeadquartersUri.AbsoluteUri,$TimeoutSeconds
    return
}

# PowerShell Direct can itself generate a type-2 logon. Collect the evidence at boot and
# read the result over Hyper-V Data Exchange before authenticating to Windows again.
$vm = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -Filter "Name='$VMId'"
if (-not $vm) { throw 'The selected Hyper-V VM was not found.' }
$name = 'CSweet-BootCheck-' + [guid]::NewGuid().ToString('N')
$runner = {
    $ErrorActionPreference = 'Stop'
    $settings = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'settings.json') | ConvertFrom-Json
    try {
        # If the host disconnects before cleanup, never repeat this run on another boot.
        $claim = [IO.File]::Open((Join-Path $PSScriptRoot 'started'),[IO.FileMode]::CreateNew)
        $claim.Dispose()
        $observed = & (Join-Path $PSScriptRoot 'probe.ps1') $settings.Uri $settings.Timeout
        $result = @{ BootUtc=$observed.Boot.ToUniversalTime().ToString('o'); Machine=$observed.Machine
            AutomaticServices=$observed.AutomaticServices; HeadquartersHttp=$observed.HeadquartersHttp
            InteractiveLogons=$observed.InteractiveLogons }
    } catch { $result = @{ Error=$_.Exception.Message } }
    Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest' -Name $settings.Name -Type String -Value ($result | ConvertTo-Json -Compress)
}
try {
    $previous = Invoke-Command -VMId $VMId -Credential $GuestCredential -ArgumentList $name,$probe.ToString(),$runner.ToString(),$HeadquartersUri.AbsoluteUri,$TimeoutSeconds -ScriptBlock {
        param($name,$probe,$runner,$uri,$timeout)
        $ErrorActionPreference = 'Stop'
        $folder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $name
        if (Test-Path -LiteralPath $folder) { throw 'The startup probe directory already exists.' }
        $null = New-Item -ItemType Directory -Path $folder -ErrorAction Stop
        [IO.File]::WriteAllText((Join-Path $folder 'probe.ps1'),$probe)
        $entry = Join-Path $folder 'run.ps1'
        [IO.File]::WriteAllText($entry,$runner)
        @{Name=$name; Uri=$uri; Timeout=$timeout} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'settings.json')
        $exe = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $action = New-ScheduledTaskAction -Execute $exe -Argument ('-NoProfile -NonInteractive -File "{0}"' -f $entry)
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $limits = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds ($timeout + 30))
        $null = Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $limits -User 'SYSTEM'
        $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime()
        & (Join-Path ([Environment]::SystemDirectory) 'shutdown.exe') /s /t 5 /d p:0:0
        if ($LASTEXITCODE -ne 0) { throw 'Windows did not accept the planned shutdown.' }
        $boot
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ((Get-VM -Id $VMId).State -ne 'Off') {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Windows did not finish shutting down.' }
        Start-Sleep -Seconds 2
    }
    Start-VM -VM (Get-VM -Id $VMId) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds + 60)
    do {
        $kvp = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_KvpExchangeComponent
        foreach ($item in $kvp.GuestExchangeItems) {
            $xml = [xml]$item
            if ($xml.SelectSingleNode("/INSTANCE/PROPERTY[@NAME='Name']/VALUE").InnerText -ne $name) { continue }
            $report = $xml.SelectSingleNode("/INSTANCE/PROPERTY[@NAME='Data']/VALUE").InnerText | ConvertFrom-Json
            if ($report.Error) { throw $report.Error }
            $boot = [DateTimeOffset]::Parse($report.BootUtc).UtcDateTime
            if ($boot -le $previous -or $report.AutomaticServices -ne 9 -or $report.HeadquartersHttp -ne 200 -or
                $null -eq $report.InteractiveLogons -or $report.InteractiveLogons -ne 0) { throw 'Invalid cold-boot evidence.' }
            [pscustomobject]@{Machine=$report.Machine; Boot=$boot; AutomaticServices=$report.AutomaticServices
                HeadquartersHttp=$report.HeadquartersHttp; InteractiveLogons=$report.InteractiveLogons}
            return
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The guest did not report a boot result through Hyper-V Data Exchange.'
} finally {
    if ((Get-VM -Id $VMId).State -eq 'Running') {
        Invoke-Command -VMId $VMId -Credential $GuestCredential -ArgumentList $name -ScriptBlock {
            param($name)
            $folder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $name
            Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
            Remove-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest' -Name $name -ErrorAction SilentlyContinue
            foreach ($file in 'probe.ps1','run.ps1','settings.json','started') { [IO.File]::Delete((Join-Path $folder $file)) }
            if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder) }
        }
    }
}
