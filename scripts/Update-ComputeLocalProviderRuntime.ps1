[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][guid]$OrganizationId,
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$ReceiptPath,
    [Parameter(Mandatory=$true)][string]$AcceptancePath,
    [switch]$NetworkAcceptance
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator approval is required to update the protected compute service.'}
$taskPayload=[IO.Path]::GetFullPath($PayloadDirectory)
$taskReceipt=[IO.Path]::GetFullPath($ReceiptPath)
$taskAcceptance=[IO.Path]::GetFullPath($AcceptancePath)
$taskResults=[xml](Get-Content -LiteralPath $taskAcceptance -Raw)
$taskCounters=@($taskResults.SelectNodes("//*[local-name()='Counters']"))
if($taskCounters.Count -ne 1 -or [int]$taskCounters[0].passed -lt 1 -or [int]$taskCounters[0].failed -ne 0 -or [int]$taskCounters[0].error -ne 0){throw 'Passing compute component acceptance is required.'}
if($NetworkAcceptance){
    $taskNetworkCheck=Join-Path $PSScriptRoot '../tests/ComputeHyperVNetwork.Tests.ps1'
    $taskNetworkReceipt=Join-Path $taskPayload ('network-hyperv-upgrade-'+[guid]::NewGuid().ToString('N')+'.json')
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $taskNetworkCheck -ResultPath $taskNetworkReceipt
    if($LASTEXITCODE -ne 0){throw 'Real Hyper-V network/cleanup component acceptance failed before provider mutation.'}
    $taskNetworkResult=Get-Content -LiteralPath $taskNetworkReceipt -Raw | ConvertFrom-Json
    if($taskNetworkResult.state -cne 'Passed' -or $taskNetworkResult.ruleCount -ne 17 -or !$taskNetworkResult.cleanupPassed){throw 'Incomplete real Hyper-V acceptance receipt.'}
}
$taskManifest=Get-Content -LiteralPath (Join-Path $taskPayload 'sha256.json') -Raw | ConvertFrom-Json
$taskNames=@('CSweet.Compute.Contracts.dll','CSweet.Compute.Runtime.dll','CSweet.Compute.HyperV.dll')
if((@($taskManifest.PSObject.Properties.Name | Sort-Object) -join '|') -cne (($taskNames | Sort-Object) -join '|')){throw 'The runtime update manifest must contain exactly the three compute assemblies.'}
foreach($taskName in $taskNames){
    $taskFile=Get-Item -LiteralPath (Join-Path $taskPayload $taskName)
    if($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-FileHash -LiteralPath $taskFile.FullName).Hash -cne $taskManifest.$taskName){throw 'Runtime payload verification failed.'}
}
. (Join-Path $PSScriptRoot 'Get-ComputeLocalInstallation.ps1')
$taskInstallation=Get-ComputeLocalInstallation -OrganizationId $OrganizationId
$taskConfigurationPath=Join-Path $taskInstallation.Root 'provider.json'
$taskConfiguration=Get-Content -LiteralPath $taskConfigurationPath -Raw | ConvertFrom-Json
$taskConfigurationHash=(Get-FileHash -LiteralPath $taskConfigurationPath).Hash
$taskSettingsPath=$taskConfiguration.provisioningSettingsPath
$taskSettings=Get-Content -LiteralPath $taskSettingsPath -Raw | ConvertFrom-Json
$taskCatalog=Get-Content -LiteralPath $taskSettings.catalogPath -Raw | ConvertFrom-Json
$taskTemplates=@($taskCatalog.templates)
if($taskTemplates.Count -ne 1){throw 'The retained repair requires one unchanged installed template.'}
$taskImage=$taskTemplates[0].imagePath
$taskRuntime=[IO.Path]::GetFullPath((Join-Path $taskInstallation.Root 'runtime'))
$taskExecutable=Join-Path $taskRuntime 'CSweet.Compute.HyperV.exe'
$taskService=Get-CimInstance Win32_Service -Filter "Name='$($taskInstallation.ServiceName)'"
if($null -eq $taskService -or $taskService.PathName -cne ('"'+$taskExecutable+'" '+$taskInstallation.ServiceArguments)){throw 'The installed compute service identity differs from the protected installation.'}
$taskValidationArguments=@($taskInstallation.ValidationArguments)
& $taskExecutable @taskValidationArguments
if($LASTEXITCODE -ne 0){throw 'The existing protected installation did not pass preflight.'}
$taskBackup=Join-Path $taskInstallation.Root ('runtime-updates/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskBackup) | Out-Null
foreach($taskName in $taskNames){Copy-Item -LiteralPath (Join-Path $taskRuntime $taskName) -Destination (Join-Path $taskBackup $taskName)}
$taskMetadata=@($taskConfigurationPath,$taskSettingsPath,$taskSettings.catalogPath)
for($taskIndex=0;$taskIndex -lt $taskMetadata.Count;$taskIndex++){Copy-Item -LiteralPath $taskMetadata[$taskIndex] -Destination (Join-Path $taskBackup ('metadata-'+$taskIndex))}
$taskStopped=$false
$taskPhase='stop-service'
try{
    Stop-Service -Name $taskInstallation.ServiceName -ErrorAction Stop
    (Get-Service -Name $taskInstallation.ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45));$taskStopped=$true
    $taskPhase='copy-runtime'
    foreach($taskName in $taskNames){
        Copy-Item -LiteralPath (Join-Path $taskPayload $taskName) -Destination (Join-Path $taskRuntime $taskName) -Force
        if((Get-FileHash -LiteralPath (Join-Path $taskRuntime $taskName)).Hash -cne $taskManifest.$taskName){throw 'Deployed compute assembly hash differs.'}
    }
    $taskPhase='certify-runtime'
    $taskRepairArguments=@('repair-local',$taskImage,$taskAcceptance)
    if($taskInstallation.ServiceArguments -cne 'service'){$taskRepairArguments += $OrganizationId.ToString('N')}
    & $taskExecutable @taskRepairArguments
    if($LASTEXITCODE -ne 0){throw 'The updated runtime certification failed.'}
    if((Get-FileHash -LiteralPath $taskConfigurationPath).Hash -cne $taskConfigurationHash){throw 'Runtime certification changed the retained provider configuration.'}
    $taskPhase='updated-preflight'
    & $taskExecutable @taskValidationArguments
    if($LASTEXITCODE -ne 0){throw 'The updated protected runtime failed preflight.'}
    $taskPhase='start-service'
    Start-Service -Name $taskInstallation.ServiceName -ErrorAction Stop
    (Get-Service -Name $taskInstallation.ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(45))
    @{state='Completed';service=$taskInstallation.ServiceName;hashes=$taskManifest;configurationPreserved=$true;workloadsPreserved=$true;completedAt=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskReceipt
}catch{
    $taskFailureType=$_.Exception.GetType().FullName
    $taskFailureId=$_.FullyQualifiedErrorId
    if($taskStopped){
        Stop-Service -Name $taskInstallation.ServiceName -ErrorAction SilentlyContinue
        (Get-Service -Name $taskInstallation.ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45))
        foreach($taskName in $taskNames){Copy-Item -LiteralPath (Join-Path $taskBackup $taskName) -Destination (Join-Path $taskRuntime $taskName) -Force}
        for($taskIndex=0;$taskIndex -lt $taskMetadata.Count;$taskIndex++){Copy-Item -LiteralPath (Join-Path $taskBackup ('metadata-'+$taskIndex)) -Destination $taskMetadata[$taskIndex] -Force}
        Start-Service -Name $taskInstallation.ServiceName -ErrorAction Stop
    }
    # Phase/type identify an updater failure without exporting protected configuration or raw exception text.
    @{state='Failed';error='compute_runtime_upgrade_failed';phase=$taskPhase;exceptionType=$taskFailureType;errorId=$taskFailureId;configurationPreserved=$true;workloadsPreserved=$true} | ConvertTo-Json | Set-Content -LiteralPath $taskReceipt
    throw
}
