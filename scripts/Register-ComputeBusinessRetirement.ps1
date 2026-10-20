# Invoked by the privileged installer before copying any provider/image payload.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$HandoffPath, [Parameter(Mandatory=$true)][string]$ProtectedRoot,
    [Parameter(Mandatory=$true)][string]$ServiceName)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$setup = Get-Content -LiteralPath $HandoffPath -Raw | ConvertFrom-Json
if (-not $setup.PSObject.Properties['retirement'] -or -not $setup.retirement) { throw 'A durable retirement handoff is required.' }
$business = ([guid]$setup.organizationId).ToString('N')
$node = ([guid]$setup.setupId).ToString('N')
$machine = Join-Path $env:ProgramData 'CSweet'
$expected = [IO.Path]::GetFullPath((Join-Path $machine ('ComputeBusinesses\' + $business)))
$shared = $ServiceName -eq 'CSweet.Compute.HyperV'
if (-not $shared -and [IO.Path]::GetFullPath($ProtectedRoot) -ine $expected) { throw 'Invalid provider retirement boundary.' }
if (([guid]$setup.retirement.businessId).ToString('N') -ne $business -or
    ([guid]$setup.retirement.nodeId).ToString('N') -ne $node) { throw 'Invalid retirement handoff identity.' }
$parent = Join-Path $machine 'ComputeRetirement'
$directory = Join-Path $parent $business
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
    $identity = New-Object Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
foreach ($path in @($parent,$directory)) {
    $cursor = [IO.Path]::GetFullPath($path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked retirement storage.' }
        $cursor = Split-Path -Parent $cursor
    }
    $null = New-Item -ItemType Directory -Path $path -Force
    Set-Acl -LiteralPath $path -AclObject $acl
}
# Match WindowsComputeProtectedPaths: ancestor delete/permission rights could otherwise
# let a local unprivileged process replace the script executed by the SYSTEM task.
$cursor = $directory
$leaf = $true
$trusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
while ($cursor) {
    $security = Get-Acl -LiteralPath $cursor
    $owner = $security.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $trusted) { throw 'Retirement path has an untrusted owner.' }
    $dangerous = [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        [Security.AccessControl.FileSystemRights]::Delete
    if ($leaf) { $dangerous = $dangerous -bor [Security.AccessControl.FileSystemRights]::Write }
    foreach ($rule in $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($leaf -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0) -and
            ($rule.FileSystemRights -band $dangerous) -ne 0 -and $rule.IdentityReference.Value -notin $trusted) {
            throw 'An unprivileged identity can replace retirement task files.'
        }
    }
    $leaf = $false
    $cursor = Split-Path -Parent $cursor
}
$scriptPath = Join-Path $directory 'retire.ps1'
Copy-Item -LiteralPath (Join-Path $setup.repositoryRoot 'src\CSweet.Compute.HyperV\Retire-ComputeBusiness.ps1') -Destination $scriptPath -Force
$request = @{cleanupId=$setup.retirement.cleanupId;businessId=$setup.organizationId;nodeId=$setup.setupId;
    receiptToken=$setup.retirement.receiptToken;coreOrigin=$setup.coreOrigin;coreCertificateSha256=$setup.coreCertificateSha256;
    certificateThumbprint=$null;preserveSharedProvider=$shared}
$incoming = Join-Path $directory 'request.incoming'
$request | ConvertTo-Json -Compress | Set-Content -LiteralPath $incoming -Encoding UTF8
Move-Item -LiteralPath $incoming -Destination (Join-Path $directory 'request.json') -Force
$action = New-ScheduledTaskAction -Execute (Join-Path $PSHOME 'powershell.exe') -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath + '"')
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
$null = Register-ScheduledTask -TaskName ('CSweet.Compute.Retire.' + $business) -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force
