[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($env:CSWEET_COMPUTE_TEST_DATABASE)) {
    throw 'Set CSWEET_COMPUTE_TEST_DATABASE to an isolated local PostgreSQL administrator connection. Required lifecycle coverage cannot be skipped.'
}
$settings=[System.Data.Common.DbConnectionStringBuilder]::new()
$settings.set_ConnectionString($env:CSWEET_COMPUTE_TEST_DATABASE)
if ($settings.get_Item('Host') -notin @('127.0.0.1','localhost')) {throw 'Lifecycle fixtures require a local disposable PostgreSQL server.'}
$previousRequired=$env:CSWEET_REQUIRE_LIFECYCLE_DATABASE
try {
    $env:CSWEET_REQUIRE_LIFECYCLE_DATABASE='1'
    $filter='FullyQualifiedName~FreshPlanningPostgresJourneyTests|FullyQualifiedName~WorkIssue|FullyQualifiedName~SelfResolvingInteractionTests|FullyQualifiedName~OwnerOperationFacts|FullyQualifiedName~ManagerWorkRecovery|FullyQualifiedName~PlanningLifecycleJourneyTests|FullyQualifiedName~HierarchicalTaskRuntimeTests|FullyQualifiedName~WorkDispatchRecoveryTests|FullyQualifiedName~WorkManagementCapabilityHandlerTests|FullyQualifiedName~WorkstreamDeliveryCompletion'
    $testArgs=@('test',(Join-Path $root 'tests/CSweet.UnitTests/CSweet.UnitTests.csproj'),'-c','Release','-m:1','-p:UseSharedCompilation=false','--filter',$filter)
    if($NoBuild){$testArgs+='--no-build'}
    & dotnet @testArgs
    if($LASTEXITCODE -ne 0){throw 'Required planning lifecycle acceptance failed.'}
} finally {$env:CSWEET_REQUIRE_LIFECYCLE_DATABASE=$previousRequired}
