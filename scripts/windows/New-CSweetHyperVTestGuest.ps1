[CmdletBinding()]
param(
    [string] $SwitchName = 'Default Switch',
    [string] $UbuntuVersion = '24.04.4',
    [string] $PackerVersion = '1.15.4',
    [string] $OutputPath = "$PSScriptRoot\..\..\artifacts\windows-runtime\source\csweet-agent-guest.vhdx",
    [string] $ProgressPath,
    [guid] $ProgressJobId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not [String]::IsNullOrWhiteSpace($ProgressPath) -and $ProgressJobId -ne [guid]::Empty) {
    . (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
}

function Report-GuestProgress {
    param([string] $Phase, [string] $Name, [string] $Message, [int] $Percent, [int] $Minimum, [int] $Maximum)
    if ([String]::IsNullOrWhiteSpace($ProgressPath) -or $ProgressJobId -eq [guid]::Empty) { return }
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey $Phase -PhaseDisplayName $Name -Message $Message -PercentComplete $Percent `
        -EstimatedRemainingMinimumSeconds $Minimum -EstimatedRemainingMaximumSeconds $Maximum
}

function Start-GuestBuildHeartbeat {
    if ([String]::IsNullOrWhiteSpace($ProgressPath) -or $ProgressJobId -eq [guid]::Empty) { return $null }
    $progressHelper = Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1'
    $ownerProcessId = $PID
    return Start-Job -ScriptBlock {
        param([string] $Helper, [string] $Path, [guid] $JobId, [int] $OwnerProcessId)
        . $Helper
        $started = [DateTimeOffset]::UtcNow
        while ($true) {
            Start-Sleep -Seconds 30
            $elapsedSeconds = [Math]::Max(0, [int]([DateTimeOffset]::UtcNow - $started).TotalSeconds)
            $elapsedMinutes = [Math]::Floor($elapsedSeconds / 60)
            $percent = [Math]::Min(49, 24 + [Math]::Floor($elapsedSeconds / 90))
            $minimum = [Math]::Max(0, 600 - $elapsedSeconds)
            $maximum = [Math]::Max(60, 2100 - $elapsedSeconds)
            Write-CSweetSetupProgress -Path $Path -JobId $JobId -Workflow 'developer-bootstrap' `
                -State running -PhaseKey build-guest -PhaseDisplayName 'Building the hardened guest image' `
                -Message "Ubuntu is still installing in the secure VM ($elapsedMinutes minutes elapsed). No action is needed." `
                -PercentComplete $percent -EstimatedRemainingMinimumSeconds $minimum `
                -EstimatedRemainingMaximumSeconds $maximum -OwnerProcessId $OwnerProcessId
        }
    } -ArgumentList $progressHelper, $ProgressPath, $ProgressJobId, $ownerProcessId
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this image builder from an Administrator PowerShell prompt, or use Initialize-CSweetWindowsIsolationTest.ps1.'
    }
}

function Get-Checksum([string] $Text, [string] $FileName) {
    $escaped = [Regex]::Escape($FileName)
    $match = [Regex]::Match($Text, "(?im)^([0-9a-f]{64})\s+\*?$escaped\s*$")
    if (-not $match.Success) { throw "The published checksum for $FileName was not found." }
    return $match.Groups[1].Value.ToLowerInvariant()
}

function Get-WebResponseText($Response) {
    if ($null -eq $Response -or $null -eq $Response.Content) {
        throw 'The checksum server returned an empty response.'
    }
    if ($Response.Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString([byte[]]$Response.Content)
    }
    return [string]$Response.Content
}

function Assert-SafeOutputPath([string] $Value) {
    $full = [IO.Path]::GetFullPath($Value)
    if ([IO.Path]::GetPathRoot($full) -eq $full -or [IO.Path]::GetExtension($full) -ine '.vhdx') {
        throw 'OutputPath must be a non-root .vhdx path.'
    }
    return $full
}

Assert-Administrator
Import-Module Hyper-V -ErrorAction Stop
$feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
if ($feature.State -ne 'Enabled') { throw 'Hyper-V is not enabled. Use the C-Sweet Agent Isolation onboarding action first, then restart Windows.' }
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
    $available = @(Get-VMSwitch | Select-Object -ExpandProperty Name)
    throw "Hyper-V switch '$SwitchName' does not exist. Available switches: $($available -join ', ')"
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Assert-SafeOutputPath $OutputPath
$artifactRoot = Join-Path $repositoryRoot 'artifacts\windows-test'
$toolRoot = Join-Path $artifactRoot 'tools'
$cacheRoot = Join-Path $artifactRoot 'cache'
$runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runRoot = Join-Path $artifactRoot $runName
$guestPublish = Join-Path $runRoot 'guest-publish'
$packerOutput = Join-Path $runRoot 'packer-output'
$packerBuildLog = Join-Path $runRoot 'packer-build.log'
$keyRoot = Join-Path $runRoot 'ssh'
$seedRoot = Join-Path $runRoot 'seed'
$seedIso = Join-Path $runRoot 'cidata.iso'
# Let Packer own output_directory so each build starts without stale export state.
New-Item -ItemType Directory -Path $toolRoot, $cacheRoot, $guestPublish, $keyRoot, $seedRoot -Force | Out-Null

$sshKeygen = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh-keygen.exe'
if (-not (Test-Path -LiteralPath $sshKeygen -PathType Leaf)) {
    Report-GuestProgress 'install-openssh' 'Installing a Windows build dependency' `
        'The Microsoft OpenSSH client is being installed for the temporary image-build connection.' 10 1200 2700
    Write-Host 'Installing the Microsoft OpenSSH Client capability required for the temporary Packer build key...'
    $capability = Add-WindowsCapability -Online -Name 'OpenSSH.Client~~~~0.0.1.0'
    if ($capability.RestartNeeded) { throw 'OpenSSH Client was installed, but Windows reports that a restart is required.' }
}
if (-not (Test-Path -LiteralPath $sshKeygen -PathType Leaf)) { throw 'Microsoft OpenSSH Client is unavailable after installation.' }

$packerRoot = Join-Path $toolRoot "packer-$PackerVersion"
$packer = Join-Path $packerRoot 'packer.exe'
if (-not (Test-Path -LiteralPath $packer -PathType Leaf)) {
    $archiveName = "packer_${PackerVersion}_windows_amd64.zip"
    $archivePath = Join-Path $cacheRoot $archiveName
    $baseUrl = "https://releases.hashicorp.com/packer/$PackerVersion"
    Write-Host "Downloading and verifying HashiCorp Packer $PackerVersion..."
    Report-GuestProgress 'download-packer' 'Downloading the image builder' `
        "HashiCorp Packer $PackerVersion is being downloaded and checksum-verified." 12 1200 2700
    $checksumsResponse = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/packer_${PackerVersion}_SHA256SUMS"
    $checksums = Get-WebResponseText $checksumsResponse
    $expected = Get-Checksum $checksums $archiveName
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$archiveName" -OutFile $archivePath
    }
    $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $expected) { throw 'The Packer archive failed its published SHA-256 verification.' }
    New-Item -ItemType Directory -Path $packerRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $packerRoot -Force
}

Write-Host 'Publishing the self-contained Linux guest broker...'
Report-GuestProgress 'publish-guest' 'Publishing the guest broker' `
    'C-Sweet is compiling the broker that runs inside the isolated guest.' 16 1080 2400
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.AgentRuntime.Guest\CSweet.AgentRuntime.Guest.csproj') `
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $guestPublish
if ($LASTEXITCODE -ne 0) { throw 'The Linux guest broker publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $guestPublish 'CSweet.AgentRuntime.Guest') -PathType Leaf)) {
    throw 'The published Linux guest executable was not produced.'
}

$privateKey = Join-Path $keyRoot 'packer_ed25519'
& $sshKeygen -q -t ed25519 -N '""' -f $privateKey
if ($LASTEXITCODE -ne 0) { throw 'The temporary Packer SSH key could not be generated.' }
$publicKey = (Get-Content -LiteralPath "$privateKey.pub" -Raw).Trim()
if ($publicKey -notmatch '^ssh-ed25519\s+') { throw 'The temporary Packer public key is invalid.' }

$userDataTemplate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build\windows-hyperv\user-data.pkrtpl') -Raw
$userData = $userDataTemplate.Replace('${ssh_public_key}', $publicKey)
if ($userData -eq $userDataTemplate) { throw 'The NoCloud user-data template does not contain the SSH key placeholder.' }
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $seedRoot 'user-data'), $userData, $utf8WithoutBom)
[IO.File]::WriteAllText((Join-Path $seedRoot 'meta-data'),
    "instance-id: csweet-agent-guest`nlocal-hostname: csweet-agent-guest`n", $utf8WithoutBom)
Write-Host 'Creating the local unattended-install seed disk...'
& (Join-Path $PSScriptRoot 'New-CSweetNoCloudSeedIso.ps1') -SourceDirectory $seedRoot -OutputPath $seedIso | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $seedIso -PathType Leaf)) {
    throw 'The local unattended-install seed disk could not be created.'
}

$ubuntuFile = "ubuntu-$UbuntuVersion-live-server-amd64.iso"
$ubuntuBase = "https://releases.ubuntu.com/$UbuntuVersion"
Write-Host "Resolving the published Ubuntu $UbuntuVersion image checksum..."
Report-GuestProgress 'resolve-ubuntu' 'Preparing the Ubuntu guest source' `
    "C-Sweet is verifying the official Ubuntu $UbuntuVersion image metadata." 19 1020 2280
$ubuntuChecksumsResponse = Invoke-WebRequest -UseBasicParsing -Uri "$ubuntuBase/SHA256SUMS"
$ubuntuChecksums = Get-WebResponseText $ubuntuChecksumsResponse
$ubuntuChecksum = Get-Checksum $ubuntuChecksums $ubuntuFile

$template = Join-Path $repositoryRoot 'build\windows-hyperv\csweet-agent-guest.pkr.hcl'
$env:PACKER_PLUGIN_PATH = Join-Path $toolRoot 'packer-plugins'
$env:PACKER_CACHE_DIR = Join-Path $cacheRoot 'packer'
New-Item -ItemType Directory -Path $env:PACKER_PLUGIN_PATH, $env:PACKER_CACHE_DIR -Force | Out-Null
Write-Host 'Installing the pinned Hyper-V Packer plugin...'
Report-GuestProgress 'prepare-packer' 'Preparing the Hyper-V image builder' `
    'The pinned Hyper-V build plugin is being installed and verified.' 21 960 2160
& $packer init $template
if ($LASTEXITCODE -ne 0) { throw 'Packer plugin initialization failed.' }
Write-Host 'Building the hardened Generation 2 Hyper-V guest.'
Write-Host "Packer will show 'Waiting for SSH to become available' while Ubuntu installs. That line normally remains unchanged for 10-30 minutes."
Report-GuestProgress 'build-guest' 'Building the hardened guest image' `
    'Ubuntu is installing from verified local media into a Secure Boot-enabled VM. The build console may remain on Waiting for SSH for 10-30 minutes.' 24 600 2100
$heartbeatJob = Start-GuestBuildHeartbeat
$packerExitCode = -1
try {
    & $packer build '-color=false' `
        "-var=iso_url=$ubuntuBase/$ubuntuFile" `
        "-var=iso_checksum=sha256:$ubuntuChecksum" `
        "-var=switch_name=$SwitchName" `
        "-var=ssh_private_key_file=$privateKey" `
        "-var=seed_iso_path=$seedIso" `
        "-var=guest_publish_directory=$guestPublish" `
        "-var=output_directory=$packerOutput" `
        "-var=vm_name=CSweet-Agent-Guest-Image-Build-$([IO.Path]::GetFileName($runRoot))" `
        $template 2>&1 | Tee-Object -FilePath $packerBuildLog
    $packerExitCode = $LASTEXITCODE
} finally {
    if ($null -ne $heartbeatJob) {
        Stop-Job -Job $heartbeatJob -ErrorAction SilentlyContinue
        Remove-Job -Job $heartbeatJob -Force -ErrorAction SilentlyContinue
    }
}
if ($packerExitCode -ne 0) {
    $detailLines = @(Get-Content -LiteralPath $packerBuildLog -Tail 12 -ErrorAction SilentlyContinue |
        ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [String]::IsNullOrWhiteSpace($_) })
    $detail = ($detailLines | Select-Object -Last 5) -join ' '
    if ($detail.Length -gt 700) { $detail = $detail.Substring($detail.Length - 700) }
    if ([String]::IsNullOrWhiteSpace($detail)) { $detail = "Packer exited with code $packerExitCode." }
    throw "The Hyper-V guest image build failed. $detail Diagnostic log: $packerBuildLog"
}

$disks = @(Get-ChildItem -LiteralPath $packerOutput -Filter '*.vhdx' -File -Recurse)
if ($disks.Count -ne 1) { throw "Expected one built VHDX, but found $($disks.Count)." }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
Copy-Item -LiteralPath $disks[0].FullName -Destination $output -Force
$digest = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Report-GuestProgress 'guest-complete' 'Secure guest image prepared' `
    'The isolated guest image was built and its SHA-256 digest was recorded.' 55 300 900
Write-Host "C-Sweet Hyper-V test guest created: $output" -ForegroundColor Green
Write-Host "Guest image digest: sha256:$digest"
Write-Output $output
