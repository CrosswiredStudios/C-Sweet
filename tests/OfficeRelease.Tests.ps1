$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../src/CSweet.Api/Setup/CSweet.OfficeRelease.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "office-release-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $testRoot | Out-Null
$script:checks = 0
function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }; $script:checks++
}
function Reject([scriptblock] $Action, [string] $Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
function New-TestZip([string] $Name, [string[]] $Entries) {
    $path = Join-Path $testRoot $Name
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Entries) {
            $stream = [IO.StreamWriter]::new($zip.CreateEntry($entry).Open())
            try { $stream.Write('fixture') } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $path
}
function New-TestRelease([string] $Version, [bool] $HasBundle = $true) {
    return [pscustomobject]@{ tag_name = "v$Version"; draft = $false; prerelease = $false
        assets = @($(if ($HasBundle) { [pscustomobject]@{ name = 'office-bootstrap.json' } })) }
}
function New-TestManifest([string] $Version) {
    return [pscustomobject]@{ schemaVersion = 1; officeVersion = $Version; protocolVersion = '1.0'
        signing = 'development'; requiresHostCertification = $true
        assets = @('windows-support','windows-x64' | ForEach-Object {
            $name = "csweet-office-$Version-$_.zip"
            [pscustomobject]@{ name = $name; size = 100; sha256 = 'a' * 64
                url = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$Version/$name" }
        }) }
}
try {
    # Exercise the real metadata failure policy before stubbing parsed release responses.
    $download = ${function:Invoke-CSweetReleaseDownload}
    function Invoke-CSweetReleaseDownload { param($Uri, $OutputPath, $MaximumBytes, $TimeoutSeconds); throw $script:discoveryFailure }
    foreach ($failure in @(
        [Net.Http.HttpRequestException]::new('Offline or unavailable'),
        [Threading.Tasks.TaskCanceledException]::new('Discovery timed out'),
        [InvalidOperationException]::new('Invocation wrapper', [Net.Http.HttpRequestException]::new('Network unavailable'))
    )) {
        $script:discoveryFailure = $failure
        Check ($null -eq (Get-CSweetReleaseJson 'https://api.github.com/fixture')) 'Unavailable discovery, including wrapped transport failures, must permit source fallback.'
    }
    $script:discoveryFailure = [IO.InvalidDataException]::new('Metadata exceeded its limit')
    Reject { Get-CSweetReleaseJson 'https://api.github.com/fixture' } 'Invalid metadata must not be treated as an offline release.'
    function Invoke-CSweetReleaseDownload { param($Uri, $OutputPath, $MaximumBytes, $TimeoutSeconds); return '{broken-json' }
    Reject { Get-CSweetReleaseJson 'https://api.github.com/fixture' } 'Malformed downloaded metadata must fail closed.'
    Set-Item Function:Invoke-CSweetReleaseDownload $download
    foreach ($url in @('http://github.com/fixture', 'https://github.com.example.org/fixture', 'https://example.org/fixture')) {
        Reject { Invoke-CSweetReleaseDownload -Uri $url -MaximumBytes 1KB } 'Reject untrusted download origins before making a request.'
    }
    $script:releases = @((New-TestRelease '0.7.0' $false), (New-TestRelease '0.5.0'), (New-TestRelease '0.6.0'))
    $script:manifest = New-TestManifest '0.6.0'
    function Get-CSweetReleaseJson([string] $Url) {
        if ($Url.Contains('api.github.com')) { return $script:releases }
        return $script:manifest
    }
    Check ((Resolve-CSweetOfficeRelease 'x64').Version -eq '0.6.0') 'Select the newest compatible tagged bundle, skipping signed-only releases.'
    Check ($null -eq (Resolve-CSweetOfficeRelease 'arm64')) 'Unsupported architectures must not install x64.'
    $script:releases = $null
    Check ($null -eq (Resolve-CSweetOfficeRelease 'x64')) 'Offline discovery must allow source fallback.'
    $script:releases = @((New-TestRelease '0.6.0'))
    $script:releases[0].prerelease = $true
    Check ($null -eq (Resolve-CSweetOfficeRelease 'x64')) 'Do not automatically install prereleases.'
    $script:releases[0].prerelease = $false
    $script:manifest.officeVersion = '0.5.0'
    Reject { Resolve-CSweetOfficeRelease 'x64' } 'Reject tag/version disagreement.'
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.assets[0].url = 'https://example.org/evil.zip'
    Reject { Resolve-CSweetOfficeRelease 'x64' } 'Reject assets outside the selected tag and repository.'
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.assets[0].sha256 = 'bad'
    Reject { Resolve-CSweetOfficeRelease 'x64' } 'Reject invalid checksums.'
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.requiresHostCertification = $false
    Reject { Resolve-CSweetOfficeRelease 'x64' } 'Never accept a bundle that skips host certification.'
    $archive = New-TestZip 'good.zip' @('scripts/windows/setup.ps1', 'guest/image.vhdx')
    Expand-CSweetOfficeArchive $archive "$testRoot/good"
    Check ((Get-Content "$testRoot/good/scripts/windows/setup.ps1") -eq 'fixture') 'Valid archives must extract.'
    foreach ($entry in @('../escape', '/absolute', 'C:/drive', 'directory/../escape', 'safe/file:stream', 'bad./file')) {
        $id = [guid]::NewGuid().ToString('N')
        $bad = New-TestZip "$id.zip" @($entry)
        Reject { Expand-CSweetOfficeArchive $bad "$testRoot/$id" } "Reject unsafe archive entry: $entry"
    }
    $bad = New-TestZip 'duplicate.zip' @('file', 'FILE')
    Reject { Expand-CSweetOfficeArchive $bad "$testRoot/duplicate" } 'Reject case-insensitive duplicate paths.'
    $script:fixture = $archive
    function Invoke-CSweetReleaseDownload { param($Uri, $OutputPath, $MaximumBytes, $TimeoutSeconds); Copy-Item $script:fixture $OutputPath }
    $asset = [pscustomobject]@{ url = 'https://github.com/fixture'; size = (Get-Item $archive).Length; sha256 = '0' * 64 }
    Reject { Receive-CSweetOfficeAsset $asset "$testRoot/tampered" } 'A bad checksum must never execute or fall back.'
    Check (-not (Test-Path "$testRoot/tampered")) 'Validate integrity before extraction.'
    $asset.sha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Receive-CSweetOfficeAsset $asset "$testRoot/verified" | Out-Null
    Check (Test-Path "$testRoot/verified/guest/image.vhdx") 'Verified downloads must be usable.'
    Write-Host "Passed $script:checks Office release checks."
} finally {
    # This exact GUID directory was created above and contains only test fixtures.
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
