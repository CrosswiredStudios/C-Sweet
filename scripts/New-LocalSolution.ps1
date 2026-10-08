[CmdletBinding()]
param([ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]*$')][string]$AdditionalConfiguration)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseSolution = Join-Path $repositoryRoot 'CSweet.slnx'
$outputSolution = Join-Path $repositoryRoot 'CSweet.Local.slnx'
$artifactDirectory = Join-Path $repositoryRoot 'artifacts'
$graphPath = Join-Path $artifactDirectory 'local-solution-project-graph.json'
[IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

Push-Location $repositoryRoot
try {
    # Evaluate the same conditional project references used by the normal build.
    # This generates a graph only; it does not switch dependencies or restore packages.
    & dotnet msbuild $baseSolution -t:GenerateRestoreGraphFile "-p:RestoreGraphOutputPath=$graphPath" -p:RestoreUseStaticGraphEvaluation=true -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the local project graph. Fix the MSBuild errors above before generating the solution.' }
    $graph = Get-Content -LiteralPath $graphPath -Raw | ConvertFrom-Json
    $solution = New-Object Xml.XmlDocument
    $solution.Load($baseSolution)
    if ($AdditionalConfiguration -and $AdditionalConfiguration -notin @('Debug', 'Release')) {
        $configurations = $solution.CreateElement('Configurations')
        foreach ($name in @('Debug', 'Release', $AdditionalConfiguration)) {
            $buildType = $solution.CreateElement('BuildType')
            $buildType.SetAttribute('Name', $name)
            $configurations.AppendChild($buildType) | Out-Null
        }
        $solution.DocumentElement.PrependChild($configurations) | Out-Null
    }
    $included = @{}
    foreach ($project in $solution.SelectNodes('//Project')) {
        $included[[IO.Path]::GetFullPath((Join-Path $repositoryRoot $project.GetAttribute('Path')))] = $true
    }
    $folder = $solution.CreateElement('Folder')
    $folder.SetAttribute('Name', '/local dependencies/')
    $rootUri = New-Object Uri ($repositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar)
    $added = 0
    foreach ($path in ($graph.projects.PSObject.Properties.Name | Sort-Object)) {
        $absolutePath = [IO.Path]::GetFullPath($path)
        if ($included.ContainsKey($absolutePath)) { continue }
        if (!(Test-Path -LiteralPath $absolutePath -PathType Leaf)) { throw "Evaluated project is missing: $absolutePath" }
        $relativePath = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri((New-Object Uri $absolutePath)).ToString())
        $project = $solution.CreateElement('Project')
        $project.SetAttribute('Path', $relativePath)
        $folder.AppendChild($project) | Out-Null
        $included[$absolutePath] = $true
        $added++
    }
    if ($added -gt 0) { $solution.DocumentElement.AppendChild($folder) | Out-Null }
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true
    $settings.Encoding = New-Object Text.UTF8Encoding $false
    $writer = [Xml.XmlWriter]::Create($outputSolution, $settings)
    try { $solution.Save($writer) } finally { $writer.Dispose() }
    Write-Host "Created $outputSolution with $added detected local dependency projects."
    Write-Host 'Open this solution in Visual Studio and keep the local dependencies loaded. Regenerate after changing sibling checkouts or dependency switches.'
}
finally { Pop-Location }
