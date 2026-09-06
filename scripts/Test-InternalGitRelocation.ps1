param([string]$TestParent = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($TestParent) -or !(Test-Path -LiteralPath $TestParent -PathType Container)) { throw 'Use an existing absolute test parent.' }
$testParent = [IO.Path]::GetFullPath($TestParent)
if (![IO.Path]::EndsInDirectorySeparator($testParent)) { $testParent += [IO.Path]::DirectorySeparatorChar }
$root = Join-Path $testParent ('csweet-relocation-test-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$destination = Join-Path $root 'destination'
[IO.Directory]::CreateDirectory((Join-Path $source 'repositories')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $source 'lfs')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $source 'backups')) | Out-Null
$marker = Join-Path $root '.csweet-test-owner'
[IO.File]::WriteAllText($marker, $root)
try {
    [IO.File]::WriteAllText((Join-Path $source 'repositories/.csweet-git-store'), 'test-store')
    $asset = Join-Path $source 'lfs/asset.bin'
    [IO.File]::WriteAllBytes($asset, [byte[]]@(0, 1, 255))
    [IO.File]::WriteAllText((Join-Path $source 'backups/backup.zip'), 'test archive bytes')
    & git init --bare --template= --initial-branch=main (Join-Path $source 'repositories/test.git')
    if ($LASTEXITCODE -ne 0) { throw 'Native Git fixture initialization failed.' }
    $bare = Join-Path $source 'repositories/test.git'
    $tree = ($null | & git --git-dir $bare mktree).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Native Git tree creation failed.' }
    $commit = (& git -c user.name=Tests -c user.email=test@example.invalid --git-dir $bare commit-tree $tree -m 'Relocation test').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Native Git commit creation failed.' }
    & git --git-dir $bare update-ref refs/heads/main $commit
    if ($LASTEXITCODE -ne 0) { throw 'Native Git ref creation failed.' }
    & (Join-Path $PSScriptRoot 'Copy-InternalGitStorage.ps1') -SourceRoot $source -DestinationRoot $destination -WritersStopped
    $copy = Join-Path $destination 'lfs/asset.bin'
    $expected = (Get-FileHash -LiteralPath $asset).Hash
    if ((Get-FileHash -LiteralPath $copy).Hash -ne $expected) { throw 'Initial copied content differs.' }
    [IO.File]::WriteAllBytes($copy, [byte[]]@(5, 5, 5))
    [IO.File]::WriteAllText(($copy + '.csweet-incoming-' + [Guid]::NewGuid().ToString('N')), 'interrupted copy')
    & (Join-Path $PSScriptRoot 'Copy-InternalGitStorage.ps1') -SourceRoot $source -DestinationRoot $destination -WritersStopped
    if ((Get-FileHash -LiteralPath $copy).Hash -ne $expected -or (Get-FileHash -LiteralPath $asset).Hash -ne $expected) { throw 'Resume did not preserve source and recover destination.' }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Copy-InternalGitStorage.ps1') -SourceRoot $source -DestinationRoot (Join-Path $source 'nested') -WritersStopped } catch { $rejected = $true }
    if (!$rejected) { throw 'Nested copy was not rejected.' }
    $state = Get-Content -LiteralPath (Join-Path $destination '.csweet-relocation.json') -Raw | ConvertFrom-Json
    if ($state.status -ne 'Verified') { throw 'Copy was not marked verified.' }
    $copiedCommit = (& git --git-dir (Join-Path $destination 'repositories/test.git') rev-parse refs/heads/main).Trim()
    if ($LASTEXITCODE -ne 0 -or $copiedCommit -ne $commit) { throw 'Copied commit differs from the source.' }
    Write-Output 'Relocation copy, content recovery, abandoned-copy cleanup, native Git integrity, source retention, and nested-target rejection passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (!$resolved.StartsWith($testParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Content -LiteralPath $marker -Raw) -ne $root) { throw 'Test ownership changed; refusing cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
