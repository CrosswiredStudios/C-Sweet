$ErrorActionPreference = 'Stop'
# Hermetic integration: local authored tarballs populate the same npm content cache
# used by managed compute restore. Every npm operation is explicitly offline.
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskRoot = Join-Path $taskRepo ('.artifacts/node-cache-regression/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$taskNpm = (Get-Command npm.cmd -ErrorAction Stop).Source
$taskConfig = Join-Path $taskRoot 'empty.npmrc'
[IO.File]::WriteAllText($taskConfig, '')
$taskGlobalConfig = Join-Path $taskRoot 'global.npmrc'
[IO.File]::WriteAllText($taskGlobalConfig, '')
$taskPackages = @()
$taskLockPackages = @{}
$taskDependencies = @{}
foreach ($taskName in @('restore-proof-a', 'restore-proof-b')) {
    $taskPackage = Join-Path $taskRoot $taskName
    [IO.Directory]::CreateDirectory($taskPackage) | Out-Null
    @{name=$taskName;version='1.0.0';main='index.js'} | ConvertTo-Json | Set-Content (Join-Path $taskPackage 'package.json')
    [IO.File]::WriteAllText((Join-Path $taskPackage 'index.js'), "module.exports = '$taskName';")
    Push-Location $taskPackage
    try {
        $taskPacked = & $taskNpm pack --offline --ignore-scripts --json --userconfig $taskConfig --globalconfig $taskGlobalConfig --cache (Join-Path $taskRoot 'pack-cache') | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Fixture packaging failed.' }
    } finally { Pop-Location }
    $taskPackages += Join-Path $taskPackage $taskPacked.filename
    $taskDependencies[$taskName] = '1.0.0'
    $taskLockPackages["node_modules/$taskName"] = @{version='1.0.0';resolved="https://registry.npmjs.org/$taskName/-/$taskName-1.0.0.tgz";integrity=$taskPacked.integrity}
}
$taskApplication = Join-Path $taskRoot 'app'
[IO.Directory]::CreateDirectory($taskApplication) | Out-Null
@{name='restore-regression';version='1.0.0';dependencies=$taskDependencies;scripts=@{test='node test.cjs'}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskApplication 'package.json')
$taskLockPackages[''] = @{name='restore-regression';version='1.0.0';dependencies=$taskDependencies}
@{name='restore-regression';version='1.0.0';lockfileVersion=3;requires=$true;packages=$taskLockPackages} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $taskApplication 'package-lock.json')
[IO.File]::WriteAllText((Join-Path $taskApplication 'test.cjs'), "const a = require('node:assert/strict'); a.equal(require('restore-proof-a'), 'restore-proof-a'); a.equal(require('restore-proof-b'), 'restore-proof-b'); console.log('RESTORE_REGRESSION_OK');")
$taskCache = Join-Path $taskRoot 'restore-cache'
Push-Location $taskApplication
try {
    $taskEmpty = & $taskNpm ci --offline --ignore-scripts --no-audit --no-fund --userconfig $taskConfig --globalconfig $taskGlobalConfig --cache $taskCache 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $taskEmpty -notmatch 'ENOTCACHED') { throw 'Empty cache must fail offline with ENOTCACHED.' }
    & $taskNpm cache add --offline --ignore-scripts --no-audit --no-fund --userconfig $taskConfig --globalconfig $taskGlobalConfig --cache $taskCache @taskPackages
    if ($LASTEXITCODE -ne 0) { throw 'Managed tarball cache population failed.' }
    & $taskNpm ci --offline --ignore-scripts --no-audit --no-fund --userconfig $taskConfig --globalconfig $taskGlobalConfig --cache $taskCache
    if ($LASTEXITCODE -ne 0) { throw 'Pinned offline installation failed.' }
    $taskResult = & (Get-Command node.exe).Source (Join-Path $taskApplication 'test.cjs') | Out-String
    if ($LASTEXITCODE -ne 0 -or $taskResult -notmatch 'RESTORE_REGRESSION_OK') { throw 'Installed packages did not pass the real Node regression.' }
    Write-Output 'PASS: empty offline cache rejected; two verified local tarballs cached; pinned npm ci and real Node tests passed without network.'
} finally { Pop-Location }
