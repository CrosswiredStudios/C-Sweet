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

function Resolve-CSweetOfficeRelease([string] $Architecture) {
    if ($Architecture -cne 'x64') { return $null }
    $releases = Get-CSweetReleaseJson "https://api.github.com/repos/$script:OfficeReleaseRepository/releases?per_page=20"
    if ($null -eq $releases) { return $null }
    $candidates = @($releases | Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -cmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' } |
        Sort-Object { [version]$_.tag_name.Substring(1) } -Descending)
    foreach ($release in $candidates) {
        if (@($release.assets | Where-Object name -CEQ 'office-bootstrap.json').Count -ne 1) { continue }
        $version = $release.tag_name.Substring(1)
        $base = "https://github.com/$script:OfficeReleaseRepository/releases/download/v$version"
        $manifest = Get-CSweetReleaseJson "$base/office-bootstrap.json"
        if ($null -eq $manifest) { return $null }
        if ($manifest.schemaVersion -ne 1 -or $manifest.officeVersion -cne $version -or
            $manifest.protocolVersion -cne '1.0' -or $manifest.signing -cne 'development' -or
            $manifest.requiresHostCertification -isnot [bool] -or $manifest.requiresHostCertification -ne $true) { throw 'Office release metadata is invalid or incompatible.' }
        $selected = @{}
        foreach ($kind in @('windows-support', 'windows-x64')) {
            $name = "csweet-office-$version-$kind.zip"
            $assets = @($manifest.assets | Where-Object name -CEQ $name)
            if ($assets.Count -eq 0) { break }
            if ($assets.Count -ne 1) { throw 'Duplicate Office release asset.' }
            $asset = $assets[0]
            if ($asset.url -cne "$base/$name" -or $asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                $asset.size -le 0 -or $asset.size -ge 2GB -or ($kind -eq 'windows-support' -and $asset.size -gt 10MB)) {
                throw 'Office release asset metadata is invalid.'
            }
            $selected[$kind] = $asset
        }
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

function Receive-CSweetOfficeAsset([object] $Asset, [string] $Destination, [int] $TimeoutSeconds = 1800) {
    $archive = "$Destination.zip"
    try {
        Invoke-CSweetReleaseDownload -Uri $Asset.url -OutputPath $archive -MaximumBytes ([long]$Asset.size) -TimeoutSeconds $TimeoutSeconds
        if ((Get-Item -LiteralPath $archive).Length -ne [long]$Asset.size -or
            (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Asset.sha256) {
            throw 'The Office release download failed its size or SHA-256 check. Retry the release download.'
        }
        Expand-CSweetOfficeArchive -Archive $archive -Destination $Destination
        return [IO.Path]::GetFullPath($Destination)
    } finally { if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force } }
}
