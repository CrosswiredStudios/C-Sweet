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
        environmentProfiles = @('software-development-polyglot-v1'); guestBrokerProtocolVersion = '1.1'
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
    $script:releases = @((New-TestRelease '0.6.0'))
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.PSObject.Properties.Remove('environmentProfiles')
    Check ($null -eq (Resolve-CSweetOfficeRelease 'x64')) 'An ordinary-only legacy bundle must permit configured source preparation instead of downloading the wrong image.'
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.guestBrokerProtocolVersion = '1.0'
    Check ($null -eq (Resolve-CSweetOfficeRelease 'x64')) 'A development guest needs protocol 1.1.'
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.environmentProfiles = @('ordinary')
    Check ($null -eq (Resolve-CSweetOfficeRelease 'x64')) 'Binary version alone cannot establish guest profile support.'
    $script:manifest = New-TestManifest '0.6.0'
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
    $asset = [pscustomobject]@{ name = 'csweet-office-0.6.0-windows-x64.zip'
        url = 'https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v0.6.0/csweet-office-0.6.0-windows-x64.zip'
        size = (Get-Item $archive).Length; sha256 = '0' * 64 }
    Reject { Receive-CSweetOfficeAsset $asset "$testRoot/tampered" } 'A bad checksum must never execute or fall back.'
    Check (-not (Test-Path "$testRoot/tampered")) 'Validate integrity before extraction.'
    $asset.sha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Receive-CSweetOfficeAsset $asset "$testRoot/verified" | Out-Null
    Check (Test-Path "$testRoot/verified/guest/image.vhdx") 'Verified downloads must be usable.'

    # Real ZIP bytes split at a small boundary exercise the production transport without GiB fixtures.
    $bytes = [IO.File]::ReadAllBytes($archive)
    $partBytes = [int][Math]::Ceiling($bytes.Length / 3.0)
    $script:partFiles = @{}
    $parts = @()
    for ($offset = 0; $offset -lt $bytes.Length; $offset += $partBytes) {
        $name = '{0}.part{1:D3}' -f $asset.name, ($parts.Count + 1)
        $path = Join-Path $testRoot $name
        $length = [Math]::Min($partBytes, $bytes.Length - $offset)
        $chunk = [byte[]]::new($length); [Array]::Copy($bytes, $offset, $chunk, 0, $length)
        [IO.File]::WriteAllBytes($path, $chunk)
        $url = "$($asset.url).part$('{0:D3}' -f ($parts.Count + 1))"
        $parts += [pscustomobject]@{ name = $name; size = $length; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant(); url = $url }
        $script:partFiles[$url] = $path
    }
    $multipart = [pscustomobject]@{ name = $asset.name; size = $bytes.Length; sha256 = $asset.sha256; parts = $parts }
    $multipartJson = $multipart | ConvertTo-Json -Depth 6
    $script:manifest = New-TestManifest '0.6.0'
    $script:manifest.schemaVersion = 2
    $script:manifest.assets[1] = $multipart
    Check ((Resolve-CSweetOfficeRelease 'x64').Bundle.parts.Count -eq 3) 'Schema 2 multipart discovery must remain automatic.'
    $script:manifest.schemaVersion = 1
    Reject { Resolve-CSweetOfficeRelease 'x64' } 'Schema 1 must not admit multipart metadata.'
    $script:manifest.schemaVersion = 2
    foreach ($change in @('missing','reordered','duplicate','foreign-url','wrong-sum','oversized','too-many','fractional','mixed-url','bad-digest','unequal')) {
        $bad = $multipartJson | ConvertFrom-Json
        switch ($change) {
            'missing' { $bad.parts = @($bad.parts[0], $bad.parts[2]) }
            'reordered' { $bad.parts = @($bad.parts[1], $bad.parts[0], $bad.parts[2]) }
            'duplicate' { $bad.parts[1] = $bad.parts[0] }
            'foreign-url' { $bad.parts[0].url = 'https://example.org/part001' }
            'wrong-sum' { $bad.size++ }
            'oversized' { $bad.parts[0].size = 1GB + 1L }
            'too-many' { $bad.parts = @($bad.parts) * 6 }
            'fractional' { $bad.parts[0].size = 1.5 }
            'mixed-url' { $bad | Add-Member -NotePropertyName url -NotePropertyValue $asset.url }
            'bad-digest' { $bad.parts[0].sha256 = 'invalid' }
            'unequal' { $bad.parts[0].size--; $bad.parts[1].size++ }
        }
        $script:manifest.assets[1] = $bad
        Reject { Resolve-CSweetOfficeRelease 'x64' } "Reject malformed multipart discovery: $change"
    }
    $script:manifest.assets[1] = $multipart
    $script:requestedParts = @()
    $script:downloadFault = ''
    function Invoke-CSweetReleaseDownload {
        param($Uri, $OutputPath, $MaximumBytes, $TimeoutSeconds)
        $script:requestedParts += [string]$Uri
        if ($script:downloadFault -eq 'missing' -or ($script:downloadFault -eq 'missing-last' -and ([string]$Uri).EndsWith('part003'))) { throw [Net.Http.HttpRequestException]::new('Missing release part') }
        if ($script:downloadFault -eq 'timeout-last' -and ([string]$Uri).EndsWith('part003')) { throw [OperationCanceledException]::new('Download timeout') }
        $source = if ($script:downloadFault -eq 'swapped' -and ([string]$Uri).EndsWith('part001')) { $script:partFiles[$parts[1].url] } else { $script:partFiles[[string]$Uri] }
        Copy-Item -LiteralPath $source -Destination $OutputPath
        if ($script:downloadFault -eq 'truncated') {
            $stream = [IO.File]::OpenWrite($OutputPath)
            try { $stream.SetLength($stream.Length - 1) } finally { $stream.Dispose() }
        }
        if ($script:downloadFault -eq 'corrupt') {
            $chunk = [IO.File]::ReadAllBytes($OutputPath); $chunk[0] = $chunk[0] -bxor 255; [IO.File]::WriteAllBytes($OutputPath, $chunk)
        }
    }
    Receive-CSweetOfficeAsset $multipart "$testRoot/multipart" | Out-Null
    Check ((Get-Content "$testRoot/multipart/guest/image.vhdx") -eq 'fixture') 'Verified multipart downloads reconstruct and extract the original archive.'
    Check (($script:requestedParts -join ',') -ceq (($multipart.parts | ForEach-Object url) -join ',')) 'Parts download exactly once in manifest order.'
    Check (-not (Test-Path "$testRoot/multipart.zip")) 'Successful reconstruction removes its temporary archive.'
    foreach ($fault in @('missing','missing-last','timeout-last','truncated','corrupt','swapped','whole-digest')) {
        $script:downloadFault = $fault
        $candidate = $multipartJson | ConvertFrom-Json
        if ($fault -eq 'whole-digest') { $candidate.sha256 = '0' * 64 }
        $destination = "$testRoot/failure-$fault"
        Reject { Receive-CSweetOfficeAsset $candidate $destination } "Reject multipart download failure: $fault"
        Check (-not (Test-Path $destination)) 'Never extract before all integrity checks pass.'
        Check (-not (Test-Path "$destination.zip")) 'Failed reconstruction removes its partial archive.'
        Check (@(Get-ChildItem -LiteralPath $testRoot -Filter "failure-$fault.part-*").Count -eq 0) 'Failed reconstruction removes its temporary part.'
    }
    $script:downloadFault = ''
    [IO.File]::WriteAllText("$testRoot/existing.zip", 'preserve')
    Reject { Receive-CSweetOfficeAsset $multipart "$testRoot/existing" } 'Do not overwrite another archive.'
    Check ((Get-Content "$testRoot/existing.zip") -eq 'preserve') 'Cleanup must preserve files this invocation did not create.'
    Write-Host "Passed $script:checks Office release checks."
} finally {
    # This exact GUID directory was created above and contains only test fixtures.
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
