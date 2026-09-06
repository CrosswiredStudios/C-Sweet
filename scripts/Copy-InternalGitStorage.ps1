param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$DestinationRoot,
    [Parameter(Mandatory = $true)][switch]$WritersStopped
)
$ErrorActionPreference = 'Stop'
if (!$WritersStopped) { throw 'Stop GitHost and all source-control writers before copying storage.' }

function Resolve-StorageDirectory([string]$Value) {
    if (![IO.Path]::IsPathFullyQualified($Value)) { throw 'Storage paths must be absolute.' }
    $resolved = [IO.Path]::GetFullPath($Value).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved -eq [IO.Path]::GetPathRoot($Value).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Use a dedicated storage directory, not a filesystem or share root.' }
    for ($ancestor = [IO.DirectoryInfo]::new($resolved); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Storage paths cannot traverse symbolic links or junctions.' }
    }
    return $resolved
}
function Get-StorageFiles([string]$Root, [switch]$Directories) {
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($name in @('repositories', 'lfs', 'backups')) {
        $directory = Join-Path $Root $name
        if (Test-Path -LiteralPath $directory -PathType Container) { $queue.Enqueue($directory) }
    }
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Storage contains a symbolic link or junction.' }
        if ($Directories) { Get-Item -LiteralPath $directory -Force }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Storage contains a symbolic link or junction.' }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) }
            elseif (!$Directories) { $item }
        }
    }
}
function Get-Digest([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$source = Resolve-StorageDirectory $SourceRoot
$destination = Resolve-StorageDirectory $DestinationRoot
$comparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$separator = [IO.Path]::DirectorySeparatorChar
if ($source.Equals($destination, $comparison) -or $source.StartsWith($destination + $separator, $comparison) -or $destination.StartsWith($source + $separator, $comparison)) {
    throw 'Source and destination storage directories must be separate and cannot contain each other.'
}
if (!(Test-Path -LiteralPath (Join-Path $source 'repositories/.csweet-git-store') -PathType Leaf)) { throw 'Source repository storage has no C-Sweet identity marker.' }
$journalPath = Join-Path $destination '.csweet-relocation.json'
if (Test-Path -LiteralPath $destination) {
    if (!(Test-Path -LiteralPath $journalPath -PathType Leaf)) {
        if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) { throw 'Destination must be empty or belong to this exact resumable copy.' }
    }
    else {
        if ((Get-Item -LiteralPath $journalPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Relocation journal cannot be a link.' }
        $previous = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
        if ($previous.version -ne 1 -or $previous.source -cne $source -or $previous.destination -cne $destination) { throw 'Destination belongs to a different storage copy.' }
    }
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
# Validate the complete source and destination trees before writing or replacing files.
$sourceFiles = @(Get-StorageFiles $source | Sort-Object FullName)
$destinationFiles = @(Get-StorageFiles $destination)
# Remove only abandoned incoming copies belonging to a current source file in this owned destination.
foreach ($file in $destinationFiles) {
    $relative = [IO.Path]::GetRelativePath($destination, $file.FullName)
    if ($relative -match '^(.+)\.csweet-incoming-[0-9a-f]{32}$' -and
        !(Test-Path -LiteralPath (Join-Path $source $relative)) -and (Test-Path -LiteralPath (Join-Path $source $Matches[1]) -PathType Leaf)) {
        if (!$file.FullName.StartsWith($destination + $separator, $comparison)) { throw 'Incoming file escapes the destination.' }
        Remove-Item -LiteralPath $file.FullName
    }
}
$state = @{ version = 1; source = $source; destination = $destination; status = 'Copying'; verifiedFiles = 0 }
function Save-CopyState {
    $pendingJournal = $journalPath + '.next'
    if ((Test-Path -LiteralPath $pendingJournal) -and ((Get-Item -LiteralPath $pendingJournal -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Pending journal cannot be a link.' }
    [IO.File]::WriteAllText($pendingJournal, ($state | ConvertTo-Json))
    [IO.File]::Move($pendingJournal, $journalPath, $true)
}
Save-CopyState
foreach ($directory in Get-StorageFiles $source -Directories) {
    $relative = [IO.Path]::GetRelativePath($source, $directory.FullName)
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destination + $separator, $comparison)) { throw 'Copy directory escapes the destination.' }
    [IO.Directory]::CreateDirectory($target) | Out-Null
}
$inventory = @{}
foreach ($file in $sourceFiles) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destination + $separator, $comparison)) { throw 'Copy target escapes the destination.' }
    $digest = Get-Digest $file.FullName
    $inventory[$relative] = $digest
    if (!(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Digest $target) -ne $digest) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $incoming = $target + '.csweet-incoming-' + [Guid]::NewGuid().ToString('N')
        try {
            $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $outputStream = [IO.File]::Open($incoming, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            if ((Get-Digest $incoming) -ne $digest -or (Get-Digest $file.FullName) -ne $digest) { throw 'Source changed during copying. Keep writers stopped and retry.' }
            [IO.File]::Move($incoming, $target, $true)
        }
        finally { if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming } }
    }
    $state.verifiedFiles++
}
$finalSource = @(Get-StorageFiles $source)
$finalDestination = @(Get-StorageFiles $destination)
if ($finalSource.Count -ne $inventory.Count -or $finalDestination.Count -ne $inventory.Count) { throw 'Storage inventory changed or destination contains extra files; copy is not complete.' }
foreach ($file in $finalSource) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    if (!$inventory.ContainsKey($relative) -or (Get-Digest $file.FullName) -ne $inventory[$relative] -or
        (Get-Digest (Join-Path $destination $relative)) -ne $inventory[$relative]) { throw 'Storage verification failed; copy is not complete.' }
}
$repositories = Join-Path $destination 'repositories'
foreach ($head in $finalDestination | Where-Object { $_.Name -eq 'HEAD' -and $_.DirectoryName.StartsWith($repositories + $separator, $comparison) -and $_.DirectoryName.EndsWith('.git', $comparison) }) {
    Push-Location $head.DirectoryName
    try {
        & git -c core.hooksPath= -c protocol.allow=never --git-dir . fsck --full --no-reflogs
        if ($LASTEXITCODE -ne 0) { throw 'Copied Git object integrity verification failed.' }
    } finally { Pop-Location }
}
$state.status = 'Verified'
Save-CopyState
Write-Output "Verified $($state.verifiedFiles) files at $destination. Source storage was retained. Configure repository/LFS/backup paths and matching markers before restarting writers. Back up the application database separately."
