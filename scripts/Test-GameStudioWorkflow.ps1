param(
    [string]$SourceRepositoriesRoot = (Split-Path $PSScriptRoot -Parent | Split-Path -Parent),
    [string]$GameDirectorRepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
if (!$GameDirectorRepositoryRoot) { $GameDirectorRepositoryRoot = Join-Path $SourceRepositoriesRoot 'CSweet.Agent.Director.VideoGame' }
$platform = Split-Path $PSScriptRoot -Parent
$common = @('-c', 'Release', '-p:UseLocalCSweetAgentSdk=false', '-p:UseLocalCSweetWorkManagementContracts=false', '-p:UseLocalCSweetMemory=false', '--verbosity', 'minimal')
$agents = @($GameDirectorRepositoryRoot)
foreach ($name in @('CSweet.Agent.ChiefOfStaff', 'CSweet.Agent.CreativeDirector.VideoGame', 'CSweet.Agent.Producer.VideoGame', 'CSweet.Agent.TechnicalDirector.VideoGame')) {
    $agents += Join-Path $SourceRepositoriesRoot $name
}
foreach ($repository in $agents) {
    if (!(Test-Path -LiteralPath $repository)) { throw "Missing agent checkout: $repository" }
    Push-Location $repository
    try { dotnet test @common; if ($LASTEXITCODE -ne 0) { throw "Agent tests failed: $repository" } }
    finally { Pop-Location }
}
Push-Location $platform
try {
    dotnet test tests/CSweet.GameRoleWorkflow.Tests @common "-p:CSweetSourceRepositoriesRoot=$SourceRepositoriesRoot" "-p:CSweetGameDirectorRepositoryRoot=$GameDirectorRepositoryRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Game-role coordination journey failed.' }
    dotnet test tests/CSweet.DeliveryWorkflow.Tests @common "-p:CSweetSourceRepositoriesRoot=$SourceRepositoriesRoot" -p:UseLocalOfficeContracts=false -p:UseLocalIsolation=false
    if ($LASTEXITCODE -ne 0) { throw 'Delivery journey failed.' }
} finally { Pop-Location }
