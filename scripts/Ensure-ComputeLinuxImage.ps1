# Internal application setup step. Called by the already-elevated local setup workflow.
[CmdletBinding()]
param(
    [string] $RepositoryRoot = "$PSScriptRoot\..",
    [string] $ProgressPath,
    [string] $SetupProgressPath,
    [guid] $ProgressJobId = [guid]::Empty,
    [string] $ProgressHelperPath = "$PSScriptRoot\..\..\CSweet.Office\scripts\windows\CSweet.WindowsSetupProgress.ps1"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$isolationRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\CSweet.Isolation'))
$imageRoot = Join-Path $repositoryRoot 'artifacts\compute\mvp\images'
$receiptPath = Join-Path $imageRoot 'current-image.json'
$progressState = @{ Heartbeat = $null }
$ownerProcessId = $PID
if ($ProgressPath -and $ProgressJobId -ne [guid]::Empty) { . $ProgressHelperPath }
$report = {
    param($phase, $message)
    Write-Host $message
    if ($SetupProgressPath) {
        . (Join-Path $repositoryRoot 'scripts\Write-ComputeSetupProgress.ps1')
        Write-ComputeSetupProgress -Path $SetupProgressPath -Message $message
    }
    if (-not $ProgressPath -or $ProgressJobId -eq [guid]::Empty) { return }
    . $ProgressHelperPath
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey "compute-$phase" -PhaseDisplayName 'Preparing Linux test environments' `
        -Message $message -PercentComplete 88 -EstimatedRemainingMinimumSeconds 60 -EstimatedRemainingMaximumSeconds 2400
    if ($phase -eq 'build-guest') {
        $progressState.Heartbeat = Start-Job -ScriptBlock {
            param($helper, $path, $jobId, $owner)
            . $helper
            while ($true) {
                Start-Sleep -Seconds 30
                Write-CSweetSetupProgress -Path $path -JobId $jobId -Workflow 'developer-bootstrap' `
                    -State running -PhaseKey 'compute-build-guest' -PhaseDisplayName 'Preparing Linux test environments' `
                    -Message 'C-Sweet is preparing Linux for application testing. This may take several minutes.' `
                    -PercentComplete 88 -EstimatedRemainingMinimumSeconds 60 -EstimatedRemainingMaximumSeconds 2400 -OwnerProcessId $owner
            }
        } -ArgumentList $ProgressHelperPath, $ProgressPath, $ProgressJobId, $ownerProcessId
    }
}.GetNewClosure()
try {
    & $report 'checking' 'Checking the Linux test environment.'
    $roots = @(
        (Join-Path $repositoryRoot 'src\CSweet.Compute.Guest'),
        (Join-Path $repositoryRoot 'src\CSweet.Compute.Contracts'),
        (Join-Path $repositoryRoot 'src\CSweet.Domain\Compute'),
        (Join-Path $repositoryRoot 'build\compute-linux'),
        (Join-Path $isolationRoot 'tools\LinuxImage')
    )
    $files = @($roots | ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Recurse } |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    foreach ($relative in @('Directory.Build.props','Directory.Packages.props','global.json',
        'scripts\New-ComputeLinuxImage.ps1','scripts\Install-ComputeGuestLinux.sh')) {
        $files += Get-Item -LiteralPath (Join-Path $repositoryRoot $relative)
    }
    $records = $files | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($records -join "`n"))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
    New-Item -ItemType Directory -Path $imageRoot -Force | Out-Null
    # Serialize cache reads, builds and receipt publication across setup/retry requests.
    $cacheLock = $null
    $waitDeadline = [DateTime]::UtcNow.AddMinutes(110)
    while ($null -eq $cacheLock) {
        try { $cacheLock = [IO.File]::Open((Join-Path $imageRoot 'preparation.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] {
            # Office/bootstrap may already own this preparation. Reuse its verified receipt when it finishes.
            if (($_.Exception.HResult -band 0xffff) -notin @(32, 33) -or [DateTime]::UtcNow -ge $waitDeadline) { throw }
            & $report 'waiting' 'C-Sweet is already preparing Linux. This request will continue automatically.'
            Start-Sleep -Seconds 15
        }
    }
    try {
        $cached = $null
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            try { $cached = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } catch { }
        }
        $cacheMatches = $false
        try {
            $cacheMatches = $null -ne $cached -and $cached.Fingerprint -ceq $fingerprint -and
                [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($cached.ImagePath)) -ieq $imageRoot -and
                (Test-Path -LiteralPath $cached.ImagePath -PathType Leaf) -and
                (Get-FileHash -LiteralPath $cached.ImagePath -Algorithm SHA256).Hash -ieq $cached.Sha256
        } catch { $cacheMatches = $false }
        if ($cacheMatches) {
            & $report 'cached' 'The Linux test image is already prepared.'
            Write-Output $cached.ImagePath
            return
        }
        $imagePath = Join-Path $imageRoot ("ubuntu-compute-$fingerprint-$([guid]::NewGuid().ToString('N')).vhdx")
        & (Join-Path $repositoryRoot 'scripts\New-ComputeLinuxImage.ps1') -OutputPath $imagePath -ReportProgress $report | Out-Host
        $build = Get-Content -LiteralPath ($imagePath + '.build.json') -Raw | ConvertFrom-Json
        if ($build.ImagePath -cne $imagePath -or
            (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash -ine $build.Sha256) { throw 'The Linux image could not be verified.' }
        $receipt = [pscustomobject]@{ Fingerprint = $fingerprint; ImagePath = $imagePath; Sha256 = $build.Sha256 }
        $temporary = $receiptPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $receiptPath) { [IO.File]::Replace($temporary, $receiptPath, ($receiptPath + '.previous')) }
        else { [IO.File]::Move($temporary, $receiptPath) }
        Write-Output $imagePath
    } finally { $cacheLock.Dispose() }
} finally {
    if ($null -ne $progressState.Heartbeat) {
        Stop-Job $progressState.Heartbeat -ErrorAction SilentlyContinue
        Remove-Job $progressState.Heartbeat -Force -ErrorAction SilentlyContinue
    }
}
