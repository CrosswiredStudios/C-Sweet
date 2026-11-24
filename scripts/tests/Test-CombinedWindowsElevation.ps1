[CmdletBinding(DefaultParameterSetName='Launch')]
param(
    [Parameter(Mandatory, ParameterSetName='Prepare')][switch]$Prepare,
    [Parameter(Mandatory, ParameterSetName='Verify')]
    [Parameter(Mandatory, ParameterSetName='Launch')][string]$Bundle,
    [Parameter(Mandatory, ParameterSetName='Verify')]
    [Parameter(Mandatory, ParameterSetName='Launch')][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
    [Parameter(Mandatory, ParameterSetName='Verify')][switch]$Verify,
    [Parameter(Mandatory, ParameterSetName='Verify')]
    [Parameter(Mandatory, ParameterSetName='Launch')][string]$WitnessPath
)
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ((Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine') {
    throw 'Run this first-install test on a dedicated Hyper-V guest.'
}
$policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
if ($policy.EnableLUA -ne 1 -or $policy.PromptOnSecureDesktop -ne 1 -or $policy.ConsentPromptBehaviorAdmin -eq 0) {
    throw 'This test needs Windows UAC and secure desktop prompts enabled.'
}
$services = @('CSweet.Api','CSweet.AgentHost','CSweet.WorkerHost','CSweet.ExecutionGateway',
    'CSweet.GitHost','CSweet.SourceControlProvisionerHost','CSweet.Office.Node','CSweet.Office.RuntimeHost','CSweet.Office.Maintenance')
if ($Prepare) {
    if (!$admin) { throw 'Prepare the guest from an administrative deployment session.' }
    if (@(Get-Service -Name $services -ErrorAction SilentlyContinue).Count) { throw 'First install requires a clean guest.' }
    & "$env:SystemRoot\System32\auditpol.exe" /set '/subcategory:{0CCE922B-69AE-11D9-BED3-505054503030}' /success:enable | Out-Null
    if ($LASTEXITCODE) { throw 'Windows could not enable process creation auditing.' }
    return
}
$file = Get-Item -LiteralPath $Bundle
if ($file.Name -ine 'C-Sweet-Setup.exe' -or (Get-FileHash -LiteralPath $file.FullName).Hash -ine $Sha256 -or
    (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') { throw 'Unexpected combined installer.' }
if (!$Verify) {
    if ($admin -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or ![Environment]::UserInteractive) {
        throw 'Launch from an ordinary interactive Windows desktop.'
    }
    if (@(Get-Service -Name $services -ErrorAction SilentlyContinue).Count) { throw 'First install requires a clean guest.' }
    $stream = [IO.File]::Open($WitnessPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $started = [DateTimeOffset]::UtcNow
        $process = Start-Process -FilePath $file.FullName -ArgumentList '/quiet','/norestart' -PassThru -Wait
        $result = [ordered]@{ computer=$env:COMPUTERNAME; bundle=$file.FullName; sha256=$Sha256
            processId=$process.Id; started=$started.ToString('o'); finished=[DateTimeOffset]::UtcNow.ToString('o')
            exitCode=$process.ExitCode }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json))
        $stream.Write($bytes,0,$bytes.Length)
        if ($process.ExitCode -ne 0) { throw "Combined install exited with $($process.ExitCode)." }
    } finally { $stream.Dispose() }
    return
}
if (!$admin) { throw 'Verify the Windows Security audit from an administrative deployment session.' }
$result = Get-Content -LiteralPath $WitnessPath -Raw | ConvertFrom-Json
$started = [DateTimeOffset]$result.started
$finished = [DateTimeOffset]$result.finished
if ($result.computer -cne $env:COMPUTERNAME -or $result.exitCode -ne 0 -or $finished -le $started -or
    $result.processId -le 0 -or $result.bundle -ine $file.FullName -or $result.sha256 -ine $Sha256) {
    throw 'The install witness and bundle must match this successful guest run.'
}
$launches = 0
$prompts = 0
$launchPaths = @($file.FullName)
$drive = [IO.Path]::GetPathRoot($file.FullName)
if ($drive -match '^[A-Za-z]:\\$') {
    # Event 4688 can record the device path of an executable launched from a DVD.
    # Resolve the actual drive mapping before comparing the full path and process ID.
    if (-not ('CSweet.InstallAudit.NativePaths' -as [type])) {
        Add-Type -Namespace CSweet.InstallAudit -Name NativePaths -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
public static extern uint QueryDosDevice(string device, System.Text.StringBuilder target, int length);
'@
    }
    $device = [Text.StringBuilder]::new(32768)
    if (-not [CSweet.InstallAudit.NativePaths]::QueryDosDevice($drive.TrimEnd('\'), $device, $device.Capacity)) {
        throw 'Windows could not resolve the installer drive for the process audit.'
    }
    $launchPaths += $device.ToString() + $file.FullName.Substring(2)
}
$timeline = [Collections.Generic.List[string]]::new()
foreach ($event in Get-WinEvent -FilterHashtable @{LogName='Security';Id=4688;StartTime=$started.LocalDateTime;EndTime=$finished.LocalDateTime} -Oldest) {
    $data = @{}
    foreach ($field in ([xml]$event.ToXml()).Event.EventData.Data) { $data[$field.Name] = $field.'#text' }
    $isConsent = $data.NewProcessName -ieq (Join-Path $env:SystemRoot 'System32\consent.exe')
    if ($isConsent) { $prompts++ }
    $image = [IO.Path]::GetFileName($data.NewProcessName)
    if ($isConsent -or $image -ieq $file.Name -or $data.TokenElevationType -eq '%%1937') {
        $timeline.Add(('{0:o} {1} ({2}), parent {3} ({4}), elevation {5}' -f
            $event.TimeCreated.ToUniversalTime(), $image, $data.NewProcessId,
            [IO.Path]::GetFileName($data.ParentProcessName), $data.ProcessId, $data.TokenElevationType))
    }
    if ($data.NewProcessName -in $launchPaths -and [Convert]::ToInt64($data.NewProcessId,16) -eq $result.processId) {
        if ($data.TokenElevationType -notin '%%1936','%%1938') { throw 'Windows did not record an unelevated installer launch.' }
        $launches++
    }
}
if ($launches -ne 1 -or $prompts -ne 1) {
    foreach ($entry in $timeline) { Write-Warning $entry }
    throw "Windows recorded $launches matching installer launches and $prompts machine-wide consent processes during installation. Check the process timeline against the bundle log."
}
foreach ($name in $services) {
    $service = Get-Service -Name $name
    $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(90))
}
[pscustomobject]@{ BundleSha256=$result.sha256; InstallerExitCode=$result.exitCode; ConsentPrompts=$prompts; RunningServices=$services.Count }
