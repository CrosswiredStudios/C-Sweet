#requires -Version 5.1
#requires -PSEdition Desktop
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ResultPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Hyper-V component acceptance requires Windows administrator consent.'}
$taskDriver=Join-Path $PSScriptRoot '../src/CSweet.Compute.HyperV/HyperVComputeDriver.cs'
$taskSource=[IO.File]::ReadAllText([IO.Path]::GetFullPath($taskDriver))
$taskMatch=[regex]::Match($taskSource,'(?s)private const string NetworkFunctions = """\r?\n(.*?)\r?\n        """;')
if(!$taskMatch.Success){throw 'Fixed network functions were not found.'}
$taskFunctions=[scriptblock]::Create($taskMatch.Groups[1].Value)
$taskId=[guid]::NewGuid();$taskName='csweet-network-selftest-'+$taskId.ToString('N')
$taskOwner='CSweet.Compute.NetworkSelfTest.v1|'+$taskId.ToString('D')
$taskVm=$null;$taskPhase='create-diskless-test';$taskResult=@{state='Failed';phase=$taskPhase}
try {
 Import-Module Hyper-V -ErrorAction Stop
 $taskVm=New-VM -Name $taskName -Generation 2 -NoVHD -MemoryStartupBytes 256MB -ErrorAction Stop
 Set-VM -VM $taskVm -Notes $taskOwner -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -CheckpointType Disabled -ErrorAction Stop
 $taskPhase='configure-network'
 Get-VMNetworkAdapter -VM $taskVm -ErrorAction Stop | Remove-VMNetworkAdapter -Confirm:$false -ErrorAction Stop
 $env:CSWEET_COMPUTE_NETWORK='OutboundOnly'
 . $taskFunctions
 Add-ComputeOutboundNetwork $taskVm
 $taskPhase='assert-network'
 Assert-ComputeNetwork $taskVm
 $taskRules=@(Get-VMNetworkAdapter -VM $taskVm -Name 'CSweet Outbound' | Get-VMNetworkAdapterExtendedAcl)
 if($taskRules.Count -lt 17){throw 'Expected egress guards were not installed.'}
 $taskResult=@{state='Passed';phase='complete';ruleCount=$taskRules.Count}
} catch {
 $taskResult=@{state='Failed';phase=$taskPhase;errorId=$_.FullyQualifiedErrorId;message=$_.Exception.Message}
} finally {
 if($null -ne $taskVm){
  $taskCurrent=Get-VM -Id $taskVm.Id -ErrorAction Stop
  if($taskCurrent.Name -cne $taskName -or $taskCurrent.Notes -cne $taskOwner -or $taskCurrent.State.ToString() -ne 'Off'){throw 'Preserve unexpected self-test VM ownership/state.'}
  Remove-VM -VM $taskCurrent -Force -Confirm:$false -ErrorAction Stop
 }
 [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),($taskResult | ConvertTo-Json -Depth 5))
}
if($taskResult.state -ceq 'Passed'){
 $taskResultDirectory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ResultPath))
 $taskFixture=[IO.Path]::GetFullPath((Join-Path $taskResultDirectory ('cleanup-selftest-'+$taskId.ToString('N'))))
 if([IO.Path]::GetDirectoryName($taskFixture) -cne $taskResultDirectory -or (Test-Path -LiteralPath $taskFixture)){throw 'Preserve unexpected cleanup test path.'}
 try {
  $taskCleanupMatch=[regex]::Match($taskSource,'(?s)internal const string ProtectRemovedWorkloadScript = """\r?\n(.*?)\r?\n        """;')
  if(!$taskCleanupMatch.Success){throw 'Fixed cleanup script was not found.'}
  $taskCleanup=[scriptblock]::Create($taskCleanupMatch.Groups[1].Value)
  $taskWorkload=Join-Path $taskFixture $taskId.ToString('N')
  [IO.Directory]::CreateDirectory($taskWorkload) | Out-Null
  $taskFile=Join-Path $taskWorkload 'owned-fixture.bin'
  [IO.File]::WriteAllText($taskFile,'authored cleanup fixture')
  $taskAdmins=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
  $taskSystem=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
  $taskVmGroup=[Security.Principal.SecurityIdentifier]::new('S-1-5-83-0')
  foreach($taskEntry in @((Get-Item -LiteralPath $taskWorkload),(Get-Item -LiteralPath $taskFile))){
   $taskAcl=if($taskEntry.PSIsContainer){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
   $taskAcl.SetAccessRuleProtection($true,$false);$taskAcl.SetOwner($taskAdmins)
   foreach($taskSid in @($taskAdmins,$taskSystem,$taskVmGroup)){$taskAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($taskSid,'FullControl','Allow'))}
   Set-Acl -LiteralPath $taskEntry.FullName -AclObject $taskAcl
  }
  $env:CSWEET_COMPUTE_VM_ID=$taskVm.Id.ToString('D');$env:CSWEET_COMPUTE_WORKLOAD_PATH=$taskWorkload
  . $taskCleanup
  foreach($taskPath in @($taskWorkload,$taskFile)){
   $taskAcl=Get-Acl -LiteralPath $taskPath
   foreach($taskRule in $taskAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
    if(!$taskRule.IdentityReference.Equals($taskAdmins) -and !$taskRule.IdentityReference.Equals($taskSystem)){throw 'Broad automatic VM permission survived cleanup protection.'}
   }
  }
  $taskExternal=Join-Path $taskFixture 'outside-owned-fixture.bin'
  [IO.File]::WriteAllText($taskExternal,'hardlink target must remain untouched')
  $taskBefore=(Get-Acl -LiteralPath $taskExternal).Sddl
  $taskLink=Join-Path $taskWorkload 'linked-fixture.bin'
  New-Item -ItemType HardLink -Path $taskLink -Target $taskExternal | Out-Null
  $taskRejected=$false
  try{. $taskCleanup}catch{if($_.Exception.Message -notlike '*linked workload file*'){throw};$taskRejected=$true}
  if(!$taskRejected -or (Get-Acl -LiteralPath $taskExternal).Sddl -cne $taskBefore){throw 'Cleanup did not preserve a linked external file.'}
  Remove-Item -LiteralPath $taskLink -Force
  $taskResult.cleanupPassed=$true
 }catch{$taskResult=@{state='Failed';phase='cleanup-component';errorId=$_.FullyQualifiedErrorId;message=$_.Exception.Message}}
 finally{
  if(Test-Path -LiteralPath $taskFixture){
   if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($taskFixture)) -cne $taskResultDirectory){throw 'Unsafe cleanup test target.'}
   Remove-Item -LiteralPath $taskFixture -Recurse -Force
  }
  [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),($taskResult | ConvertTo-Json -Depth 5))
 }
}
if($taskResult.state -cne 'Passed'){exit 1}
