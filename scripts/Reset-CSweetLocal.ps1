#Requires -Version 5.1
<#
.SYNOPSIS
Previews or applies a destructive reset of the default Windows development installation.
.DESCRIPTION
Close AppHost, C-Sweet, builds and setup first. Run in elevated Windows PowerShell 5.1 or later as the
development user. Preview is the default; -Apply is required for any mutation.
This resets ALL local C-Sweet businesses, including ones still present in the DB.
Custom storage, remote offices, Compose, certificates and shared SDK caches are excluded.
See docs/implementation/local-reset.md for scope and recovery instructions.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Apply,
    [switch] $IncludeBuildOutputs,
    [switch] $IncludeUserData,
    [switch] $ResetDatabase,
    [switch] $All,
    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-CSweetResetAbsolutePath([string] $Path) {
    # .NET Framework lacks Path.IsPathFullyQualified. IsPathRooted alone would
    # incorrectly accept C:relative and \current-drive-relative paths.
    # Accept ordinary drive/UNC paths; refuse device namespaces for deletion.
    $normalized = $Path.Replace('/', '\')
    return $normalized -match '^[A-Za-z]:\\' -or
        $normalized -match '^\\\\(?![?.]\\)[^\\]+\\[^\\]+(?:\\|$)'
}

function Test-CSweetResetWithin([string] $Path, [string] $Root) {
    if (-not (Test-CSweetResetAbsolutePath $Path) -or -not (Test-CSweetResetAbsolutePath $Root)) { return $false }
    try {
        $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
        $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    } catch { return $false }
    return $candidate.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CSweetResetPath([string] $Path, [string] $Boundary) {
    if (-not (Test-CSweetResetWithin $Path $Boundary) -or
        [IO.Path]::GetFullPath($Path).TrimEnd('\') -eq [IO.Path]::GetPathRoot($Path).TrimEnd('\')) {
        throw "Unsafe reset path: $Path (boundary: $Boundary)"
    }
    # Check ancestors too: a safe-looking child of a junction is not a safe target.
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reset refuses linked paths: $current"
            }
        }
        $current = Split-Path -Parent $current
    }
}

function Get-CSweetResetTree([string] $Path) {
    # Never traverse junctions, including links below a deletion target.
    $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    $pending.Push((Get-Item -LiteralPath $Path -Force))
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reset refuses linked paths: $($item.FullName)"
        }
        $item
        if ($item -is [IO.DirectoryInfo]) {
            foreach ($child in $item.EnumerateFileSystemInfos()) { $pending.Push($child) }
        }
    }
}

function Get-CSweetResetBuildTargets([string] $Repository) {
    # Only conventional .NET project outputs and the repository's generated artifacts.
    # Never select arbitrary directories named bin/obj in user workspaces or node_modules.
    $artifacts = Join-Path $Repository 'artifacts'
    if (Test-Path -LiteralPath $artifacts) { $artifacts }
    # Git's index/ignore rules avoid walking all historical build output just to find projects.
    $projects = @(& git -C $Repository -c core.quotePath=false ls-files --cached --others --exclude-standard -- '*.csproj')
    if ($LASTEXITCODE -ne 0) { throw "Cannot inventory projects in $Repository" }
    foreach ($project in $projects | Select-Object -Unique) {
        if ($project -notmatch '^(src|tests|tools)/' -or $project -match '(^|/)(bin|obj|artifacts|node_modules)/') { continue }
        $directory = Split-Path -Parent (Join-Path $Repository $project)
        Assert-CSweetResetPath $directory $Repository
        foreach ($name in @('bin', 'obj', 'TestResults')) {
            $target = Join-Path $directory $name
            if (Test-Path -LiteralPath $target) { $target }
        }
    }
    $results = Join-Path $Repository 'TestResults'
    if (Test-Path -LiteralPath $results) { $results }
}

function Get-CSweetResetImageBuildTargets(
    [object[]] $Machines, [string[]] $Roots, [string] $ImageArtifactRoot,
    [string] $TemporaryRoot = ([IO.Path]::GetTempPath()),
    [string[]] $LegacyOfficeArtifactRoots = @()
) {
    # New-CSweetLinuxHyperVImage supplies csw-<12 hex> and image-<guid>/cidata.iso
    # to Packer. Packer keeps the live VM/disk in Temp/hyperv<digits>, not in its
    # eventual export directory. Require the selected C-Sweet seed as ownership
    # evidence, then add only that VM directory and its exact sibling disk.
    $imageSelected = @($Roots | Where-Object { Test-CSweetResetWithin $ImageArtifactRoot $_ }).Count -gt 0
    $selectedOfficeRoots = @($LegacyOfficeArtifactRoots | Where-Object {
        $officeRoot = $_; @($Roots | Where-Object { Test-CSweetResetWithin $officeRoot $_ }).Count -gt 0
    })
    if (-not $imageSelected -and $selectedOfficeRoots.Count -eq 0) { return }
    $temporary = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd('\', '/')
    $imageRoot = [IO.Path]::GetFullPath($ImageArtifactRoot).TrimEnd('\', '/')
    foreach ($vm in $Machines) {
        if (-not (Test-CSweetResetWithin $vm.Path $temporary)) { continue }
        $seedRoots = @()
        $seedPattern = ''
        if ($imageSelected -and $vm.Name -match '^csw-[0-9a-f]{12}$') {
            $seedRoots = @($imageRoot)
            $seedPattern = '^image-[0-9a-f]{32}\\cidata\.iso$'
        } elseif ($imageSelected -and $vm.Name -match '^CSweet-Linux-Image-Build-(image-[0-9a-f]{32})$') {
            $seedRoots = @($imageRoot)
            # Legacy names encode the run ID; a different run's seed is not evidence.
            $seedPattern = '^' + [regex]::Escape($Matches[1]) + '\\cidata\.iso$'
        } elseif ($selectedOfficeRoots.Count -gt 0 -and $vm.Name -match '^CSweet-Agent-Guest-Image-Build-([0-9]{8}-[0-9]{6}-[0-9a-f]{8})$') {
            $seedRoots = $selectedOfficeRoots
            $seedPattern = '^' + [regex]::Escape($Matches[1]) + '\\cidata\.iso$'
        } else { continue }
        $vmPath = [IO.Path]::GetFullPath($vm.Path).TrimEnd('\', '/')
        $buildDirectory = Split-Path -Parent $vmPath
        if ((Split-Path -Leaf $vmPath) -cne $vm.Name -or
            (Split-Path -Leaf $buildDirectory) -notmatch '^hyperv[0-9]+$' -or
            (Split-Path -Parent $buildDirectory) -ine $temporary) { continue }
        $seed = @($vm | Get-VMDvdDrive | Where-Object {
            $path = [string]$_.Path
            foreach ($root in $seedRoots) {
                if (-not (Test-CSweetResetWithin $path $root)) { continue }
                $relative = [IO.Path]::GetFullPath($path).Substring([IO.Path]::GetFullPath($root).TrimEnd('\').Length).TrimStart('\')
                if ($relative -match $seedPattern -and (Test-Path -LiteralPath $path -PathType Leaf)) { return $true }
            }
            return $false
        })
        if ($seed.Count -ne 1) { continue }
        $disks = @($vm | Get-VMHardDiskDrive)
        $diskPath = Join-Path $buildDirectory ($vm.Name + '.vhdx')
        if ($disks.Count -ne 1 -or -not (Test-CSweetResetAbsolutePath $disks[0].Path) -or
            [IO.Path]::GetFullPath($disks[0].Path) -ine $diskPath -or @($vm | Get-VMSnapshot).Count -ne 0) { continue }
        foreach ($path in @($vmPath, $diskPath)) {
            [pscustomobject]@{ Path = $path; Boundary = $buildDirectory; Kind = 'Temporary C-Sweet image build' }
        }
    }
}

function Get-CSweetResetVmPlan([object[]] $Machines, [string[]] $Roots) {
    $plan = [Collections.Generic.List[object]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($vm in $Machines) {
        try {
            $paths = @($vm.Path, $vm.ConfigurationLocation, $vm.SnapshotFileLocation, $vm.SmartPagingFilePath) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
            $media = @($vm | Get-VMHardDiskDrive | Select-Object -ExpandProperty Path) +
                @($vm | Get-VMDvdDrive | Select-Object -ExpandProperty Path)
            foreach ($snapshot in @($vm | Get-VMSnapshot)) {
                $media += @($snapshot | Get-VMHardDiskDrive | Select-Object -ExpandProperty Path)
                $media += @($snapshot | Get-VMDvdDrive | Select-Object -ExpandProperty Path)
            }
            $parents = @()
            # Parent VHDs may live under a selected image cache even if the child is elsewhere.
            foreach ($disk in @($media | Where-Object { $_ -match '\.(a?vhdx?)$' } | Select-Object -Unique)) {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                $parent = $disk
                while ($parent) {
                    if (-not $seen.Add($parent)) { throw "Cyclic VHD chain: $disk" }
                    $parent = (Get-VHD -Path $parent).ParentPath
                    if ($parent) { $parents += $parent }
                }
            }
            $allPaths = @($paths) + @($media | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $inside = @($allPaths | Where-Object {
                $path = $_; @($Roots | Where-Object { Test-CSweetResetWithin $path $_ }).Count -gt 0
            })
            $sharedParents = @($parents | Where-Object {
                $path = $_; @($Roots | Where-Object { Test-CSweetResetWithin $path $_ }).Count -gt 0
            })
            if ($inside.Count -eq 0 -and $sharedParents.Count -eq 0) { continue }
            # A shared base disk alone never establishes ownership of a VM.
            $ownedLocation = @($Roots | Where-Object { Test-CSweetResetWithin $vm.Path $_ }).Count -gt 0
            if (-not $ownedLocation -or $inside.Count -ne $allPaths.Count) {
                $external = @($allPaths | Where-Object {
                    $path = $_; @($Roots | Where-Object { Test-CSweetResetWithin $path $_ }).Count -eq 0
                } | Select-Object -Unique)
                throw "VM '$($vm.Name)' has shared/external paths. Resolve its storage before resetting. External paths: $($external -join '; '). Selected storage references: $(($inside + $sharedParents | Select-Object -Unique) -join '; ')"
            }
            $plan.Add([pscustomobject]@{ Name = $vm.Name; Id = $vm.Id; Path = $vm.Path; State = [string]$vm.State })
        } catch { $failures.Add("$($vm.Name): $($_.Exception.Message)") }
    }
    if ($failures.Count -gt 0) { throw ("VM inventory has $($failures.Count) blocker(s):`n" + ($failures -join "`n")) }
    $plan
}

function Test-CSweetResetAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}

function Test-CSweetResetHyperVAvailable { return $null -ne (Get-Module -ListAvailable Hyper-V) }

function Get-CSweetResetTrackedFiles([string] $Repository, [string] $Path) {
    $tracked = @(& git -C $Repository ls-files -- $Path)
    if ($LASTEXITCODE -ne 0) { throw "Failed Git inventory: $Path" }
    $tracked
}

function Assert-CSweetResetBuildUntracked([string] $Repository, [string] $Path) {
    if (@(Get-CSweetResetTrackedFiles $Repository $Path).Count -gt 0) { throw "Refusing build cleanup with tracked files: $Path" }
}

function Invoke-CSweetLocalReset {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [switch] $Apply, [switch] $IncludeBuildOutputs, [switch] $IncludeUserData,
        [switch] $ResetDatabase, [switch] $All,
        [string] $RepositoryRoot
    )
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This reset supports Windows development installations only.' }
    # Resolve after parameter binding: Windows PowerShell 5.1 can expose an empty
    # PSScriptRoot while evaluating a parameter default through -File.
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
    if ($All) { $IncludeBuildOutputs = $true; $IncludeUserData = $true; $ResetDatabase = $true }
    Write-Host 'Discovering default C-Sweet reset targets (read-only)...'
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'src\CSweet.AppHost\CSweet.AppHost.csproj'))) {
        throw 'RepositoryRoot must be the C-Sweet headquarters checkout.'
    }
    $parent = Split-Path -Parent $RepositoryRoot
    $machineRoot = Join-Path $env:ProgramData 'CSweet'
    $installRoot = Join-Path $env:ProgramFiles 'CSweet'
    $localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CSweet'
    $targets = [Collections.Generic.List[object]]::new()
    $blockers = [Collections.Generic.List[string]]::new()
    $services = @(); $vms = @(); $containers = @(); $volumePresent = $false
    function Add-Target([string] $Path, [string] $Boundary, [string] $Kind, [string] $Repo = '') {
        if (Test-Path -LiteralPath $Path) {
            $targets.Add([pscustomobject]@{ Path = [IO.Path]::GetFullPath($Path); Boundary = $Boundary; Kind = $Kind; Repository = $Repo; GiB = $null })
        }
    }
    foreach ($name in @('AgentRuntime', 'Compute', 'ComputeBusinesses', 'Diagnostics', 'Setup', 'Office', 'SatelliteOffice', 'ExecutionNode')) {
        Add-Target (Join-Path $machineRoot $name) $machineRoot 'Runtime'
    }
    foreach ($name in @('Office', 'SatelliteOffice', 'ExecutionNode')) {
        Add-Target (Join-Path $installRoot $name) $installRoot 'Installed office'
    }
    if ($IncludeUserData) {
        Add-Target $localRoot $localRoot 'User data INCLUDING repositories, media and secrets'
        Add-Target (Join-Path $RepositoryRoot '.csweet') $RepositoryRoot 'Fallback user data'
        Add-Target (Join-Path ([IO.Path]::GetTempPath()) 'csweet-git') ([IO.Path]::GetTempPath()) 'Temporary Git workspaces'
    }
    else {
        foreach ($name in @('agent-build-logs', 'agent-source-archives', 'ComputeSetup', 'crashes', 'Setup', 'workspace-snapshots', 'ExecutionNode')) {
            Add-Target (Join-Path $localRoot $name) $localRoot 'Cache / runtime state'
        }
    }
    if ($IncludeBuildOutputs) {
        $repos = @($RepositoryRoot) + @(Get-ChildItem -LiteralPath $parent -Directory | Where-Object {
            $_.Name -like 'CSweet.*' -or $_.Name -eq 'CSweetAgentSdk'
        } | Select-Object -ExpandProperty FullName)
        foreach ($repo in $repos | Select-Object -Unique) {
            try {
                Assert-CSweetResetPath $repo $parent
                if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) { continue }
                foreach ($target in Get-CSweetResetBuildTargets $repo) { Add-Target $target $repo 'Generated build output' $repo }
            } catch { $blockers.Add($_.Exception.Message) }
        }
    }
    $hyperVAvailable = $false
    $hyperVInventoryComplete = $false
    $machines = @()
    try {
        $hyperVAvailable = Test-CSweetResetHyperVAvailable
        if ($hyperVAvailable) {
            Import-Module Hyper-V
            $machines = @(Get-VM)
            if ($IncludeBuildOutputs) {
                $imageArtifactRoot = Join-Path $parent 'CSweet.Isolation\artifacts\linux-images'
                $legacyOfficeRoots = @('CSweet.Office', 'CSweet.SatelliteOffice') | ForEach-Object { Join-Path $parent ($_ + '\artifacts\windows-test') }
                foreach ($target in Get-CSweetResetImageBuildTargets $machines @($targets | Select-Object -ExpandProperty Path) $imageArtifactRoot -LegacyOfficeArtifactRoots $legacyOfficeRoots) {
                    Add-Target $target.Path $target.Boundary $target.Kind
                }
            }
        }
        $hyperVInventoryComplete = $true
    } catch { $blockers.Add('Hyper-V discovery failed: ' + $_.Exception.Message) }
    $disks = [Collections.Generic.List[string]]::new()
    $preserved = [Collections.Generic.List[object]]::new()
    Write-Host "Measuring $($targets.Count) folders; large build trees can take several minutes..."
    $targetIndex = 0
    foreach ($target in $targets) {
        Write-Progress -Activity 'Measuring C-Sweet reset targets' -Status $target.Path -PercentComplete (100 * $targetIndex++ / $targets.Count)
        try {
            Assert-CSweetResetPath $target.Path $target.Boundary
            if ($target.Repository) {
                if (@(Get-CSweetResetTrackedFiles $target.Repository $target.Path).Count -gt 0) {
                    $preserved.Add($target)
                    continue
                }
            }
            [long]$bytes = 0
            Get-CSweetResetTree $target.Path | ForEach-Object {
                if ($_ -is [IO.FileInfo]) {
                    $bytes += $_.Length
                    if ($_.Extension -match '^\.(a?vhdx?)$') { $disks.Add($_.FullName) }
                }
            }
            $target.GiB = [math]::Round($bytes / 1GB, 2)
        } catch { $blockers.Add($_.Exception.Message) }
    }
    Write-Progress -Activity 'Measuring C-Sweet reset targets' -Completed
    foreach ($target in $preserved) { $null = $targets.Remove($target) }
    try {
        $allServices = @(Get-CimInstance Win32_Service -Filter "Name LIKE 'CSweet%'")
        $services = @($allServices | Where-Object {
            $_.Name -match '^CSweet\.(Compute\.HyperV(\.[0-9a-f]{32})?|Office\.(Node|RuntimeHost|Maintenance)|SatelliteOffice\.(Node|RuntimeHost)|ExecutionNode(\..+)?)$'
        })
        if ($services.Count -ne $allServices.Count) { throw 'Unrecognized C-Sweet services exist; inspect them before reset.' }
        foreach ($service in $services) {
            $match = [regex]::Match($service.PathName, '^"([^"\r\n]+\.exe)"(?:\s|$)|^(\S+\.exe)(?:\s|$)')
            $exe = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            if (-not $match.Success -or @($targets | Where-Object { Test-CSweetResetWithin $exe $_.Path }).Count -eq 0) {
                throw "Service $($service.Name) uses an external/unrecognized executable; inspect it before reset."
            }
        }
        # AppHost and setup must be closed by the operator, not killed by broad process names.
        $active = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ProcessId -ne $PID -and ($_.Name -match '^CSweet\.' -or
                ($IncludeBuildOutputs -and $_.Name -match '^packer(?:-plugin-hyperv.*)?\.exe$') -or
                $_.CommandLine -match '(?i)(dotnet.+CSweet|Start-CSweet|Initialize-CSweet|Install-Compute|packer.+csweet)') -and
            $_.ProcessId -notin @($services | Select-Object -ExpandProperty ProcessId)
        })
        if ($active.Count -gt 0) { $blockers.Add('Close C-Sweet / build / setup processes first: ' + (($active | ForEach-Object { "$($_.Name) PID=$($_.ProcessId)" }) -join ', ')) }
    } catch { $blockers.Add('Service/process inventory failed: ' + $_.Exception.Message) }
    try {
        if ($hyperVAvailable -and $hyperVInventoryComplete) {
            $vms = @(Get-CSweetResetVmPlan $machines @($targets | Select-Object -ExpandProperty Path))
            foreach ($disk in $disks) {
                if ((Get-VHD -Path $disk).Attached) {
                    # Running VMs are stopped during apply. Host-mounted disks must be handled explicitly.
                    $attachedToVm = @(Get-VM | Get-VMHardDiskDrive | Where-Object { $_.Path -eq $disk }).Count -gt 0
                    if (-not $attachedToVm) { throw "Host-mounted or indirectly attached VHD requires explicit dismount/shutdown first: $disk" }
                }
            }
        } elseif (-not $hyperVAvailable -and ($disks.Count -gt 0 -or $services.Count -gt 0)) { throw 'Hyper-V module unavailable; cannot verify VM/disk ownership.' }
    } catch { $blockers.Add('Hyper-V inventory failed: ' + $_.Exception.Message) }
    # The existing Office uninstaller owns registry / service privilege cleanup.
    $officeUninstaller = Join-Path $parent 'CSweet.Office\scripts\windows\Uninstall-CSweetOffice.ps1'
    $hasOffice = @($targets | Where-Object { $_.Kind -eq 'Installed office' -or $_.Path -in @((Join-Path $machineRoot 'Office'), (Join-Path $machineRoot 'SatelliteOffice')) }).Count -gt 0 -or
        @($services | Where-Object { $_.Name -match '^CSweet\.(Office|SatelliteOffice)\.' }).Count -gt 0
    if ($hasOffice -and -not (Test-Path -LiteralPath $officeUninstaller)) { $blockers.Add('The sibling CSweet.Office uninstall script is required.') }
    $officeProduct = Get-ItemProperty -LiteralPath 'HKLM:\Software\CSweet\Office' -Name ProductCode -ErrorAction SilentlyContinue
    if ($officeProduct) {
        $blockers.Add('MSI-installed Office detected. Uninstall it through Windows Installed apps / C-Sweet Office removal, then preview again.')
    }
    if ($ResetDatabase) {
        try {
            # Never point a reset at a remote daemon or inherit a context/host override.
            if ($env:DOCKER_HOST -or $env:DOCKER_CONTEXT) { throw 'Clear DOCKER_HOST / DOCKER_CONTEXT before resetting the local database.' }
            $context = @(& docker context inspect | ConvertFrom-Json)
            if ($LASTEXITCODE -ne 0 -or $context.Count -ne 1 -or $context[0].Endpoints.docker.Host -notlike 'npipe://*') {
                throw 'Database reset requires a local Windows named-pipe Docker context.'
            }
            $volumes = @(& docker volume ls --format '{{.Name}}')
            if ($LASTEXITCODE -ne 0) { throw 'Docker volume inventory failed.' }
            $volumePresent = 'csweet-aspire-postgres' -in $volumes
            if ($volumePresent) {
                $containers = @(& docker ps -a --filter 'volume=csweet-aspire-postgres' --format '{{.ID}}')
                if ($LASTEXITCODE -ne 0) { throw 'Database container inventory failed.' }
                foreach ($id in $containers) {
                    $info = @(& docker inspect $id | ConvertFrom-Json)
                    if ($LASTEXITCODE -ne 0 -or $info[0].Config.Image -notmatch '^(docker.io/)?(library/)?postgres[:@]') {
                        throw "Non-Postgres container uses the database volume: $id"
                    }
                }
            }
        } catch { $blockers.Add('Database inventory failed: ' + $_.Exception.Message) }
    }
    Write-Host "Local reset for ALL C-Sweet businesses. User root: $localRoot"
    $targets | Select-Object Kind, GiB, Path | Format-Table -AutoSize -Wrap | Out-Host
    $services | Select-Object Name, State | Format-Table -AutoSize -Wrap | Out-Host
    $vms | Format-Table -AutoSize -Wrap | Out-Host
    foreach ($target in $preserved) { Write-Host "PRESERVED (contains tracked files): $($target.Path)" }
    if ($ResetDatabase) { Write-Host "Aspire database volume present: $volumePresent; Postgres containers: $($containers -join ', ')" }
    [double]$measuredGiB = 0
    foreach ($target in $targets) { if ($null -ne $target.GiB) { $measuredGiB += $target.GiB } }
    Write-Host ('Measured files: {0:N2} GiB (logical sizes; unknown folders and Docker are excluded).' -f $measuredGiB)
    foreach ($blocker in $blockers) { Write-Warning $blocker }
    if (-not $Apply) { Write-Host 'PREVIEW ONLY. No resources changed. Add -Apply only after reviewing the targets and resolving blockers.'; return }
    if ($blockers.Count -gt 0) { throw 'Reset blocked by incomplete/unsafe inventory. No resources changed.' }
    if (-not (Test-CSweetResetAdministrator)) { throw 'Run Windows PowerShell 5.1 or later as Administrator under the same development user.' }
    if (-not $PSCmdlet.ShouldProcess('ALL listed local C-Sweet resources', 'Permanently reset; discard workloads and require fresh enrollment')) { return }

    # Quiesce services before VMs and delete data only after unregistering its owners.
    foreach ($service in $services) {
        Set-Service -Name $service.Name -StartupType Disabled
        Stop-Service -Name $service.Name -Force
        (Get-Service -Name $service.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    $freshVms = @()
    if ($hyperVAvailable) { $freshVms = @(Get-CSweetResetVmPlan @(Get-VM) @($targets | Select-Object -ExpandProperty Path)) }
    if (@(Compare-Object @($vms | ForEach-Object { "$($_.Id)|$($_.Path)" } | Sort-Object) @($freshVms | ForEach-Object { "$($_.Id)|$($_.Path)" } | Sort-Object)).Count -gt 0) {
        throw 'VM inventory changed. Services remain stopped; preview again.'
    }
    foreach ($vm in $freshVms) {
        $current = Get-VM -Id $vm.Id
        if ([string]$current.State -ne 'Off') { Stop-VM -VM $current -TurnOff -Force -Confirm:$false }
        Remove-VM -VM $current -Force -Confirm:$false
    }
    if ($hasOffice) { & $officeUninstaller -Force -Elevated }
    foreach ($service in $services) {
        if (Get-Service -Name $service.Name -ErrorAction SilentlyContinue) {
            & "$env:SystemRoot\System32\sc.exe" delete $service.Name | Out-Host
            if ($LASTEXITCODE -notin @(0, 1060)) { throw "Failed to remove service $($service.Name); data retained." }
        }
    }
    if ($ResetDatabase -and $volumePresent) {
        foreach ($id in $containers) {
            & docker rm -f $id | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Failed to remove the database container.' }
        }
        & docker volume rm csweet-aspire-postgres | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Failed to remove the database volume.' }
    }
    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target.Path)) { continue }
        Assert-CSweetResetPath $target.Path $target.Boundary
        if ($target.Repository) {
            Assert-CSweetResetBuildUntracked $target.Repository $target.Path
        }
        # Revalidate the entire tree just before recursive deletion.
        Get-CSweetResetTree $target.Path | ForEach-Object {
            if ($_ -is [IO.FileInfo] -and $_.Extension -match '^\.(a?vhdx?)$' -and (Get-VHD -Path $_.FullName).Attached) {
                throw "Disk remains attached; data retained: $($_.FullName)"
            }
        }
        Remove-Item -LiteralPath $target.Path -Recurse -Force
        if (Test-Path -LiteralPath $target.Path) { throw "Reset did not completely remove $($target.Path)" }
    }
    Write-Host 'Reset complete. Rebuild/relaunch C-Sweet and enroll fresh local execution services. See local-reset.md for excluded stores.'
}

# Dot-sourcing exposes the functions for isolated fixture tests without running a reset.
if ($MyInvocation.InvocationName -ne '.') { Invoke-CSweetLocalReset @PSBoundParameters }
