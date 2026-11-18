[CmdletBinding()]
param([switch]$NoBuild, [string]$GameDirectorRepositoryRoot = $env:CSWEET_GAME_DIRECTOR_REPOSITORY)
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
    $testArgs=@('test',(Join-Path $root 'tests/CSweet.UnitTests/CSweet.UnitTests.csproj'),'-c','Release','-m:1','-p:UseSharedCompilation=false',
        '-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false','--filter',$filter)
    if($NoBuild){$testArgs+='--no-build'}
    & dotnet @testArgs
    if($LASTEXITCODE -ne 0){throw 'Required planning lifecycle acceptance failed.'}
    $required = @('FreshBoardPublishesThroughPublicProtocol', 'Assigned_implementer_claims_the_first_dispatched_sprint_leaf',
        'ContributionDiscoveryPagesOnlyOriginalRequestsFromTheCurrentBoardTeam', 'Recovery_decision_applies_to_its_Story',
        'Admission_must_name_the_Story')
    $resultsDir = Join-Path ([IO.Path]::GetTempPath()) ("csweet-planning-results-" + [guid]::NewGuid().ToString('N'))
    $requiredFilter = ($required | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    & dotnet test (Join-Path $root 'tests/CSweet.UnitTests/CSweet.UnitTests.csproj') -c Release --no-build --filter $requiredFilter --results-directory $resultsDir --logger 'trx;LogFileName=handoff.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Required first-sprint regressions failed.' }
    [xml]$trx = Get-Content -Raw -LiteralPath (Join-Path $resultsDir 'handoff.trx')
    foreach ($method in $required) {
        $cases = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like "*$method*" })
        if ($cases.Count -eq 0 -or @($cases | Where-Object outcome -ne 'Passed').Count) { throw "Missing or unsuccessful required case: $method" }
    }
    $journeyArgs = @('test', (Join-Path $root 'tests/CSweet.GameRoleWorkflow.Tests/CSweet.GameRoleWorkflow.Tests.csproj'), '-c', 'Release',
        '-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false','--results-directory',$resultsDir,'--logger','trx;LogFileName=roles.trx')
    if ($GameDirectorRepositoryRoot) { $journeyArgs += "-p:CSweetGameDirectorRepositoryRoot=$GameDirectorRepositoryRoot" }
    if ($NoBuild) { $journeyArgs += '--no-build' }
    & dotnet @journeyArgs
    if ($LASTEXITCODE -ne 0) { throw 'Game-role coordination journeys failed.' }
    [xml]$roles = Get-Content -Raw -LiteralPath (Join-Path $resultsDir 'roles.trx')
    $cases = @($roles.TestRun.Results.UnitTestResult)
    if (!$cases.Count -or @($cases | Where-Object outcome -ne 'Passed').Count) { throw 'The game-role journeys must run and pass.' }
} finally {$env:CSWEET_REQUIRE_LIFECYCLE_DATABASE=$previousRequired}
