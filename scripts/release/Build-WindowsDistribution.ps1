#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This build targets Windows and must run on Windows.' }

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

$repositoryRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$outputRoot = Join-Path $repositoryRoot 'artifacts/windows'
# A fresh staging directory prevents stale binaries from entering a new build.
$staging = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $staging "csweet-$Version-win-x64"
$feed = Join-Path $staging 'feed'
$packages = Join-Path $staging 'packages'
New-Item -ItemType Directory -Path $packageRoot, $feed -Force | Out-Null

Push-Location $repositoryRoot
try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit.' }
    $dependencies = Get-Content "$PSScriptRoot/build-dependencies.json" -Raw | ConvertFrom-Json
    [xml]$centralPackages = Get-Content 'Directory.Packages.props' -Raw
    $officePin = $centralPackages.SelectSingleNode("//PackageVersion[@Include='CSweet.Office.Contracts']").Version
    $officeDependency = $dependencies | Where-Object package -eq 'CSweet.Office.Contracts'
    if ($officeDependency.version -ne $officePin) {
        throw 'Update build-dependencies.json to match the Office.Contracts package pin.'
    }

    # Explicit sources prevent a developer's private feeds from hiding missing packages.
    $config = Join-Path $staging 'NuGet.Config'
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="build-dependencies" value="$escapedFeed" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
"@ | Set-Content $config -Encoding utf8

    # These contracts are not yet published on NuGet. Build immutable revisions,
    # then consume the resulting packages with all local project references off.
    foreach ($dependency in $dependencies) {
        $checkout = Join-Path $staging $dependency.package
        Invoke-Checked git @('init', '--quiet', $checkout)
        Invoke-Checked git @('-C', $checkout, 'remote', 'add', 'origin', $dependency.repository)
        Invoke-Checked git @('-C', $checkout, 'fetch', '--depth', '1', 'origin', $dependency.commit)
        Invoke-Checked git @('-C', $checkout, 'checkout', '--quiet', '--detach', 'FETCH_HEAD')
        $project = Join-Path $checkout $dependency.project
        Invoke-Checked dotnet @('pack', $project, '-c', 'Release', '-o', $feed,
            "-p:RestoreConfigFile=$config", "-p:RestorePackagesPath=$packages", '-p:UseLocalIsolation=false',
            '-p:ManagePackageVersionsCentrally=false', '-p:ContinuousIntegrationBuild=true')
        $nupkg = Join-Path $feed "$($dependency.package).$($dependency.version).nupkg"
        if (-not (Test-Path $nupkg)) { throw "Expected dependency package was not produced: $nupkg" }
        $archive = [IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            $nuspec = @($archive.Entries | Where-Object FullName -like '*.nuspec')
            if ($nuspec.Count -ne 1) { throw "Invalid package: $nupkg" }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($metadata.package.metadata.id -ne $dependency.package -or
                $metadata.package.metadata.version -ne $dependency.version) {
                throw "Dependency package identity does not match build-dependencies.json: $nupkg"
            }
        } finally { $archive.Dispose() }
    }

    $properties = @(
        "-p:RestoreConfigFile=$config", "-p:RestorePackagesPath=$packages", '-p:UseLocalCSweetAgentSdk=false',
        '-p:UseLocalCSweetMemory=false', '-p:UseLocalCSweetWorkManagementContracts=false',
        '-p:UseLocalOfficeContracts=false', '-p:UseLocalIsolation=false',
        '-p:ContinuousIntegrationBuild=true', "-p:Version=$Version", "-p:SourceRevisionId=$commit"
    )
    $services = @('CSweet.Api', 'CSweet.AgentHost', 'CSweet.WorkerHost',
        'CSweet.ExecutionGateway', 'CSweet.Migrator', 'CSweet.GitHost', 'CSweet.SourceControlProvisionerHost')
    foreach ($service in $services) {
        $destination = Join-Path $packageRoot $service
        Invoke-Checked dotnet (@('publish', "src/$service/$service.csproj", '-c', 'Release',
            '-r', 'win-x64', '--self-contained', 'true', '-o', $destination,
            '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None') + $properties)
        foreach ($required in @("$service.exe", "$service.runtimeconfig.json", 'coreclr.dll')) {
            if (-not (Test-Path (Join-Path $destination $required))) {
                throw "Incomplete self-contained publish for ${service}: missing $required"
            }
        }
    }

    # Blazor WebAssembly publishes static browser assets, not a Windows executable.
    $webPublish = Join-Path $staging 'web-publish'
    Invoke-Checked dotnet (@('publish', 'src/CSweet.App/CSweet.App.csproj', '-c', 'Release',
        '-o', $webPublish, '-p:DebugType=None') + $properties)
    Copy-Item (Join-Path $webPublish 'wwwroot') (Join-Path $packageRoot 'web') -Recurse
    $indexPath = Join-Path $packageRoot 'web/index.html'
    if (-not (Test-Path $indexPath)) {
        throw 'The browser frontend publish is incomplete.'
    }
    $entryPoint = [regex]::Match((Get-Content $indexPath -Raw),
        'src="(_framework/blazor\.webassembly(?:\.[a-zA-Z0-9_-]+)?\.js)"')
    if (-not $entryPoint.Success -or
        -not (Test-Path (Join-Path "$packageRoot/web" $entryPoint.Groups[1].Value))) {
        throw 'The browser frontend entry script referenced by index.html is missing.'
    }
    # Development overrides are not part of the distributable.
    Get-ChildItem $packageRoot -Recurse -File -Filter 'appsettings.Development.json' |
        Remove-Item
    Copy-Item 'docs/deployment/windows-build.md' "$packageRoot/README.md"
    if (Test-Path 'LICENSE') { Copy-Item 'LICENSE' $packageRoot }
    Copy-Item 'Directory.Packages.props' "$packageRoot/dependency-versions.props"
    @{
        version = $Version; commit = $commit; runtime = 'win-x64'
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sdk = (& dotnet --version).Trim(); services = $services
        buildDependencies = $dependencies
    } | ConvertTo-Json -Depth 6 | Set-Content "$packageRoot/build-info.json" -Encoding utf8

    $zip = Join-Path $outputRoot "csweet-$Version-win-x64.zip"
    if (Test-Path $zip) { throw "Output already exists; choose another version or move it first: $zip" }
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zip)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "Created $zip"
} finally {
    Pop-Location
}
