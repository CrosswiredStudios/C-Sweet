[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BaseUrl,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Model,
    [string]$Checkpoint = (Join-Path $PSScriptRoot '../tests/CSweet.GameRoleWorkflow.Tests/Checkpoints/victor-stack.json'),
    [string]$ApiKeyVariable,
    [ValidateRange(1, 255999)][int]$MaxOutputTokens = 4096,
    [string]$SourceRepositoriesRoot,
    [string]$GameDirectorRepositoryRoot = $env:CSWEET_GAME_DIRECTOR_REPOSITORY,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$SourceRepositoriesRoot) {
    $gitDir = git -C $root rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'Supply -SourceRepositoriesRoot for a checkout without Git metadata.' }
    $SourceRepositoriesRoot = Split-Path -Parent (Split-Path -Parent $gitDir)
}
$fixture = (Resolve-Path -LiteralPath $Checkpoint).Path
$project = Join-Path $root 'tests/CSweet.GameRoleWorkflow.Tests/CSweet.GameRoleWorkflow.Tests.csproj'
$results = Join-Path $root 'artifacts/TestResults'
$receipt = 'victor-stack-' + [guid]::NewGuid().ToString('N') + '.trx'
$method = 'CSweet.GameRoleWorkflow.Tests.StackCheckpointTests.Victor_stack_choice_reaches_Gabriels_published_setup_ticket'
$testArgs = @('test', $project, '-c', 'Release', '--filter', "FullyQualifiedName=$method",
    "-p:CSweetSourceRepositoriesRoot=$SourceRepositoriesRoot", '-p:UseLocalCSweetAgentSdk=false',
    '-p:UseLocalCSweetWorkManagementContracts=false', '--environment', 'CSWEET_STACK_CHECKPOINT_MODE=live',
    '--environment', "CSWEET_STACK_CHECKPOINT_URL=$BaseUrl", '--environment', "CSWEET_STACK_CHECKPOINT_MODEL=$Model",
    '--environment', "CSWEET_STACK_CHECKPOINT_INPUT=$fixture", '--environment', "CSWEET_STACK_CHECKPOINT_KEY_VARIABLE=$ApiKeyVariable",
    '--environment', "CSWEET_STACK_CHECKPOINT_OUTPUT_TOKENS=$MaxOutputTokens", '--results-directory', $results,
    '--logger', "trx;LogFileName=$receipt", '--logger', 'console;verbosity=detailed')
if ($GameDirectorRepositoryRoot) { $testArgs += "-p:CSweetGameDirectorRepositoryRoot=$GameDirectorRepositoryRoot" }
if ($NoBuild) { $testArgs += '--no-build' }
& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Victor stack checkpoint failed. See the test output for the failed step.' }
[xml]$run = Get-Content -Raw -LiteralPath (Join-Path $results $receipt)
$executed = @($run.TestRun.Results.UnitTestResult)
if ($executed.Count -ne 1 -or $executed[0].testName -ne $method -or $executed[0].outcome -ne 'Passed') {
    throw 'The selected live checkpoint must execute and pass exactly once.'
}
