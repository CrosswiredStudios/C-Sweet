[CmdletBinding()]
param(
    [string] $RepositoriesRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
# Standalone agent builds do not require sibling checkouts. This cross-repository
# contributor check deliberately requires both, and fails on new/missing core files.
$engineer = Join-Path $RepositoriesRoot 'CSweet.Agent.Engineer.VideoGame'
$developer = Join-Path $RepositoriesRoot 'CSweet.Agent.SoftwareDeveloper'

function Normalize-Core([string] $text) {
    return $text.Replace('CSweet.Agent.Engineer.VideoGame', 'CSweet.Agents.SoftwareDeveloper').
        Replace('GameEngineer', 'SoftwareDeveloper').
        Replace('Adrian Silva', 'Daniel Kim').Replace('Adrian', 'Daniel').
        Replace('Video Game Engineer', 'Software Developer').
        Replace('video game engineer', 'software developer').Replace("`r", '')
}

function Get-CoreFiles([string] $root, [string] $project) {
    $source = Join-Path $root "src/$project"
    if (!(Test-Path -LiteralPath $source)) { throw "Missing agent source: $source" }
    $files = @{}
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -Filter '*.cs') {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        if ($relative -match '^(bin|obj)/') { continue }
        # Identity, version and role prompts are intentionally distinct.
        if ($relative -in @('GameEngineerProfile.cs', 'SoftwareDeveloperProfile.cs')) { continue }
        $files[(Normalize-Core $relative)] = $file.FullName
    }
    foreach ($test in @('HarnessExecutionTests.cs', 'WorkspaceProgressTests.cs', 'WorkspaceShellTests.cs',
        'WorkspaceResourceSizingTests.cs', 'BrowserDeliveryRecipeTests.cs', 'PlanningExecutionTests.cs', 'PlanCompletionRecoveryTests.cs', 'GeneratedCommitMessageTests.cs')) {
        $files["tests/$test"] = Join-Path $root "tests/$project.Tests/$test"
    }
    return $files
}

$left = Get-CoreFiles $engineer 'CSweet.Agent.Engineer.VideoGame'
$right = Get-CoreFiles $developer 'CSweet.Agents.SoftwareDeveloper'
$failures = @()
foreach ($key in @(@($left.Keys) + @($right.Keys) | Sort-Object -Unique)) {
    if (!$left.ContainsKey($key) -or !$right.ContainsKey($key) -or
        !(Test-Path -LiteralPath $left[$key]) -or !(Test-Path -LiteralPath $right[$key])) {
        $failures += "Missing counterpart: $key"
        continue
    }
    if ((Normalize-Core ([IO.File]::ReadAllText($left[$key]))) -cne
        (Normalize-Core ([IO.File]::ReadAllText($right[$key])))) {
        $failures += "Core differs: $key"
    }
}
if ($failures.Count -gt 0) { throw ($failures -join "`n") }
Write-Output "Developer core parity passed: $($left.Count) files match after explicit identity substitutions."
