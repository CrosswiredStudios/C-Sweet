# Tagged development bundles are HTTPS/checksum verified, then certified and development-signed on this host.
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:OfficeReleaseRepository = 'CrosswiredStudios/CSweet.Office'

function Invoke-CSweetReleaseDownload {
    param([uri] $Uri, [string] $OutputPath, [long] $MaximumBytes, [int] $TimeoutSeconds = 30)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('CSweet-Office-Setup/1.0')
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $response = $null
    try {
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($Uri.Scheme -cne 'https' -or $Uri.UserInfo -or $Uri.Port -ne 443 -or
                ($Uri.Host -notin @('github.com', 'api.github.com') -and -not $Uri.Host.EndsWith('.githubusercontent.com'))) {
                throw 'An Office release request targeted an untrusted origin.'
            }
            $response = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            if ($status -in @(301,302,303,307,308)) {
                $Uri = [uri]::new($Uri, $response.Headers.Location)
                $response.Dispose(); $response = $null
                continue
            }
            if ($status -ne 200) { throw [Net.Http.HttpRequestException]::new("Office release server returned HTTP $status.") }
            if ($response.Content.Headers.ContentLength -gt $MaximumBytes) { throw 'Office release exceeds the download limit.' }
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = if ($OutputPath) {
                [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            } else { [IO.MemoryStream]::new() }
            try {
                $buffer = [byte[]]::new(65536); $total = 0L
                while (($count = $input.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $count
                    if ($total -gt $MaximumBytes) { throw 'Office release exceeds the download limit.' }
                    $output.Write($buffer, 0, $count)
                }
                if (-not $OutputPath) { return [Text.Encoding]::UTF8.GetString($output.ToArray()) }
            } finally { $output.Dispose(); $input.Dispose() }
            return
        }
        throw 'Too many Office release redirects.'
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose(); $timeout.Dispose()
    }
}

function Get-CSweetReleaseJson([string] $Url) {
    try {
        # Keep metadata in bounded memory; do not trust a temporary file writable by another process.
        $json = Invoke-CSweetReleaseDownload -Uri $Url -MaximumBytes 1MB -TimeoutSeconds 15
    } catch {
        # Offline, no release, rate limit, or transient server failure allows the local source fallback.
        $cause = $_.Exception
        while ($null -ne $cause) {
            if ($cause -is [Net.Http.HttpRequestException] -or $cause -is [OperationCanceledException]) { return $null }
            $cause = $cause.InnerException
        }
        throw
    }
    return $json | ConvertFrom-Json
}

function Assert-CSweetOfficeAsset([object] $Asset, [string] $Version, [string] $Kind, [bool] $AllowMultipart) {
    $suffix = if ($Kind -eq 'linux-x64') { 'tar.gz' } else { 'zip' }
    $name = "csweet-office-$Version-$Kind.$suffix"
    $base = "https://github.com/$script:OfficeReleaseRepository/releases/download/v$Version"
    if ($null -eq $Asset -or $Asset.name -cne $name -or
        $Asset.sha256 -isnot [string] -or $Asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        ($Asset.size -isnot [int] -and $Asset.size -isnot [long]) -or $Asset.size -le 0) {
        throw 'Office release asset metadata is invalid.'
    }
    $multipart = $null -ne $Asset.PSObject.Properties['parts']
    $allowed = if ($multipart) { @('name','size','sha256','parts') } else { @('name','size','sha256','url') }
    if (@($Asset.PSObject.Properties.Name | Where-Object { $_ -notin $allowed }).Count -gt 0) { throw 'Office release asset has unexpected metadata.' }
    if (-not $multipart) {
        if ($Asset.url -cne "$base/$name" -or $Asset.size -ge 2GB -or ($Kind -eq 'windows-support' -and $Asset.size -gt 10MB)) {
            throw 'Office release asset metadata is invalid.'
        }
        return
    }
    if (-not $AllowMultipart -or $Kind -eq 'windows-support' -or $Asset.size -gt 16GB -or
        $Asset.parts -isnot [array] -or $Asset.parts.Count -lt 2 -or $Asset.parts.Count -gt 16) {
        throw 'Office multipart metadata is invalid or exceeds transport limits.'
    }
    $total = 0L
    for ($index = 0; $index -lt $Asset.parts.Count; $index++) {
        $part = $Asset.parts[$index]
        $partName = '{0}.part{1:D3}' -f $name, ($index + 1)
        if ($null -eq $part -or @($part.PSObject.Properties.Name | Where-Object { $_ -notin @('name','size','sha256','url') }).Count -gt 0 -or
            $part.name -cne $partName -or $part.url -cne "$base/$partName" -or
            $part.sha256 -isnot [string] -or $part.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            ($part.size -isnot [int] -and $part.size -isnot [long]) -or $part.size -le 0 -or $part.size -gt 1GB) {
            throw 'Office multipart part is missing, reordered, duplicated or invalid.'
        }
        if (($index -lt $Asset.parts.Count - 1 -and $part.size -ne $Asset.parts[0].size) -or $part.size -gt $Asset.parts[0].size) {
            throw 'Office multipart part sizes are inconsistent.'
        }
        $total += [long]$part.size
    }
    if ($total -ne $Asset.size) { throw 'Office multipart sizes do not match the complete archive.' }
}

function Resolve-CSweetOfficeRelease([string] $Architecture, [string] $TargetVersion) {
    if ($Architecture -cne 'x64') { return $null }
    if ($TargetVersion -and $TargetVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid target Office version.' }
    $releases = Get-CSweetReleaseJson "https://api.github.com/repos/$script:OfficeReleaseRepository/releases?per_page=20"
    if ($null -eq $releases) { return $null }
    $candidates = @($releases | Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -cmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' } |
        Sort-Object { [version]$_.tag_name.Substring(1) } -Descending)
    foreach ($release in $candidates) {
        if ($TargetVersion -and $release.tag_name -cne "v$TargetVersion") { continue }
        if (@($release.assets | Where-Object name -CEQ 'office-bootstrap.json').Count -ne 1) { continue }
        $version = $release.tag_name.Substring(1)
        $base = "https://github.com/$script:OfficeReleaseRepository/releases/download/v$version"
        $manifest = Get-CSweetReleaseJson "$base/office-bootstrap.json"
        if ($null -eq $manifest) { return $null }
        if ($manifest.schemaVersion -notin @(1,2) -or $manifest.officeVersion -cne $version -or
            $manifest.protocolVersion -cne '1.0' -or $manifest.signing -cne 'development' -or
            $manifest.requiresHostCertification -isnot [bool] -or $manifest.requiresHostCertification -ne $true) { throw 'Office release metadata is invalid or incompatible.' }
        if ($manifest.assets -isnot [array] -or $manifest.assets.Count -lt 2 -or $manifest.assets.Count -gt 3) { throw 'Office release assets are invalid.' }
        $selected = @{}
        foreach ($kind in @('windows-support', 'windows-x64')) {
            $name = "csweet-office-$version-$kind.zip"
            $assets = @($manifest.assets | Where-Object name -CEQ $name)
            if ($assets.Count -eq 0) { break }
            if ($assets.Count -ne 1) { throw 'Duplicate Office release asset.' }
            $asset = $assets[0]
            Assert-CSweetOfficeAsset $asset $version $kind ($manifest.schemaVersion -eq 2)
            $selected[$kind] = $asset
        }
        foreach ($asset in $manifest.assets) {
            if ($asset.name -cin @("csweet-office-$version-windows-support.zip", "csweet-office-$version-windows-x64.zip")) { continue }
            Assert-CSweetOfficeAsset $asset $version 'linux-x64' ($manifest.schemaVersion -eq 2)
        }
        # Older Office binary versions can carry an ordinary-only guest. Such a
        # release is unavailable for this setup profile, rather than an integrity
        # failure or a reason to download and repeatedly certify the wrong image.
        if ($null -eq $manifest.PSObject.Properties['environmentProfiles'] -or
            @($manifest.environmentProfiles) -cnotcontains 'software-development-polyglot-v1' -or
            $null -eq $manifest.PSObject.Properties['guestBrokerProtocolVersion'] -or
            $manifest.guestBrokerProtocolVersion -cne '1.1') { continue }
        if ($selected.Count -eq 2) { return [pscustomobject]@{ Version = $version; Support = $selected['windows-support']; Bundle = $selected['windows-x64'] } }
    }
    return $null
}

function Expand-CSweetOfficeArchive([string] $Archive, [string] $Destination) {
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (Test-Path -LiteralPath $Destination) { throw 'Office extraction requires a new directory.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $total = 0L
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        if ($zip.Entries.Count -gt 10000) { throw 'Office archive contains too many files.' }
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('\', '/')
            if ($relative.StartsWith('/') -or $relative.Contains(':') -or
                @($relative.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Office archive contains an unsafe entry.' }
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not $paths.Add($path)) { throw 'Office archive contains an unsafe or duplicate path.' }
            $total += $entry.Length
            if ($total -gt 16GB) { throw 'Office archive exceeds the extraction limit.' }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

function Test-CSweetOfficeFile([string] $Path, [object] $Expected) {
    return [IO.File]::Exists($Path) -and (Get-Item -LiteralPath $Path).Length -eq [long]$Expected.size -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Expected.sha256
}

# On a partial or damaged cache hit, validate each range against the current manifest.
# A terminated process may have written only a prefix of its last part.
function Test-CSweetOfficePart([IO.FileStream] $Archive, [long] $Offset, [object] $Part) {
    $remaining = [long]$Part.size
    if ($Offset + $remaining -gt $Archive.Length) { return $false }
    $Archive.Position = $Offset
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [byte[]]::new(1MB)
        while ($remaining -gt 0) {
            $count = $Archive.Read($buffer, 0, [int][Math]::Min($remaining, $buffer.Length))
            if ($count -eq 0) { return $false }
            $null = $hash.TransformBlock($buffer, 0, $count, $buffer, 0)
            $remaining -= $count
        }
        $null = $hash.TransformFinalBlock($buffer, 0, 0)
        return [BitConverter]::ToString($hash.Hash).Replace('-', '') -ieq $Part.sha256
    } finally { $hash.Dispose() }
}

function Write-CSweetOfficeArchive([IO.FileStream] $OutputStream, [object] $Asset, [string] $PartPath,
    [Diagnostics.Stopwatch] $Elapsed, [int] $TimeoutSeconds, [scriptblock] $OnDownload) {
    $parts = @(if ($null -ne $Asset.PSObject.Properties['parts']) { $Asset.parts } else { $Asset })
    $downloaded = $false
    $offset = 0L
    for ($index = 0; $index -lt $parts.Count; $index++) {
        $part = $parts[$index]
        if (-not (Test-CSweetOfficePart $OutputStream $offset $part)) {
            if ($OnDownload) { & $OnDownload ($index + 1) $parts.Count }
            $remainingSeconds = [int][Math]::Ceiling($TimeoutSeconds - $Elapsed.Elapsed.TotalSeconds)
            if ($remainingSeconds -le 0) { throw 'Office release download timed out.' }
            try {
                Invoke-CSweetReleaseDownload -Uri $part.url -OutputPath $PartPath -MaximumBytes ([long]$part.size) -TimeoutSeconds $remainingSeconds
                if (-not (Test-CSweetOfficeFile $PartPath $part)) { throw 'The Office release part failed its size or SHA-256 check. Retry the release download.' }
                $OutputStream.Position = $offset
                $input = [IO.File]::OpenRead($PartPath)
                try { $input.CopyTo($OutputStream, 1MB) } finally { $input.Dispose() }
                $downloaded = $true
            } finally { [IO.File]::Delete($PartPath) }
        }
        $offset += [long]$part.size
    }
    $OutputStream.SetLength($offset)
    return $downloaded
}

function Receive-CSweetOfficeAsset([object] $Asset, [string] $Destination, [int] $TimeoutSeconds = 1800,
    [string] $CacheRoot, [scriptblock] $OnDownload, [scriptblock] $OnVerified) {
    if ($Asset.name -cnotmatch '^csweet-office-([0-9]+\.[0-9]+\.[0-9]+)-(windows-support|windows-x64)\.zip$') { throw 'Office download asset name is invalid.' }
    $kind = $Matches[2]
    Assert-CSweetOfficeAsset $Asset $Matches[1] $kind $true
    if ($TimeoutSeconds -le 0 -or $TimeoutSeconds -gt 1800) { throw 'Office download timeout is invalid.' }
    if (Test-Path -LiteralPath $Destination) { throw 'Office extraction requires a new directory.' }
    $archive = "$Destination.zip"
    $partPath = "$Destination.part-$([guid]::NewGuid().ToString('N'))"
    $cacheLock = $null
    $temporaryArchive = $false
    $downloaded = $false
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    try {
        $mode = [IO.FileMode]::CreateNew
        if ($CacheRoot) {
            # Keep one archive per asset kind across releases. Serialize repair and extraction
            # so another elevated setup cannot change the verified bytes underneath us.
            $cache = [IO.Directory]::CreateDirectory((Join-Path $CacheRoot $kind)).FullName
            $lockPath = Join-Path $cache 'writer.lock'
            while ($null -eq $cacheLock) {
                try { $cacheLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
                catch [IO.IOException] {
                    if (($_.Exception.HResult -band 0xffff) -notin @(11, 32, 33)) { throw }
                    if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Timed out waiting for the Office download cache.' }
                    Start-Sleep -Milliseconds 250
                }
            }
            $mode = [IO.FileMode]::OpenOrCreate
            $archive = Join-Path $cache 'package.zip'
            $partPath = Join-Path $cache 'download.tmp'
            [IO.File]::Delete($partPath)
        }
        if (-not $CacheRoot -or -not (Test-CSweetOfficeFile $archive $Asset)) {
            $output = [IO.File]::Open($archive, $mode, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $temporaryArchive = -not $CacheRoot
            try {
                $downloaded = Write-CSweetOfficeArchive $output $Asset $partPath $elapsed $TimeoutSeconds $OnDownload
            } finally { $output.Dispose() }
            if (-not (Test-CSweetOfficeFile $archive $Asset)) {
                throw 'The Office release download failed its size or SHA-256 check. Retry the release download.'
            }
        }
        if ($OnVerified) { & $OnVerified $downloaded }
        Expand-CSweetOfficeArchive -Archive $archive -Destination $Destination
        return [IO.Path]::GetFullPath($Destination)
    } finally {
        if ($null -ne $cacheLock) { $cacheLock.Dispose() }
        if ($temporaryArchive) { [IO.File]::Delete($archive) }
    }
}
