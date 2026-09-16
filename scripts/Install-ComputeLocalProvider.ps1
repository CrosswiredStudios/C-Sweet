# Internal application installer. C-Sweet supplies the protected handoff and triggers UAC.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $HandoffPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$setup = Get-Content -LiteralPath $HandoffPath -Raw | ConvertFrom-Json
$repositoryRoot = [IO.Path]::GetFullPath($setup.repositoryRoot)
$logRoot = Split-Path -Parent $HandoffPath
$logPath = Join-Path $logRoot 'installation.log'
Start-Transcript -LiteralPath $logPath -Force | Out-Null
. (Join-Path $repositoryRoot 'scripts\Write-ComputeSetupProgress.ps1')
$setupProgress = Join-Path $logRoot 'progress.json'
. (Join-Path $repositoryRoot 'scripts\Write-ComputeInstallerReceipt.ps1')
$hasher = [Security.Cryptography.SHA256]::Create()
try { $attemptHash = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($setup.secret))).Replace('-','').ToLowerInvariant() } finally { $hasher.Dispose() }
$installerState = 'Failed'
Write-ComputeInstallerReceipt -Directory $logRoot -AttemptHash $attemptHash -State 'Running'
$providerExecutable = $null
$backupReady = $false
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval was not received.' }
    $existingService = Get-Service -Name 'CSweet.Compute.HyperV' -ErrorAction SilentlyContinue
    if ($null -ne $existingService) {
        $existingExecutable = Join-Path $env:ProgramData 'CSweet\Compute\runtime\CSweet.Compute.HyperV.exe'
        $registered = Get-CimInstance Win32_Service -Filter "Name='CSweet.Compute.HyperV'"
        if ($registered.PathName -cne ('"' + $existingExecutable + '" service')) { throw 'The existing compute service has an unexpected executable.' }
        & $existingExecutable validate-service
        if ($LASTEXITCODE -ne 0) { throw 'The existing compute service did not pass validation.' }
        $installedCatalog = Get-Content -LiteralPath (Join-Path $env:ProgramData 'CSweet\Compute\catalog.json') -Raw | ConvertFrom-Json
        if (@($installedCatalog.templates | Where-Object { $_.template.features -contains 'docker-apps-v1' }).Count -gt 0) {
            & (Join-Path $repositoryRoot 'scripts\Set-ComputeServiceRecovery.ps1')
            if ($existingService.Status -ne 'Running') { Start-Service -Name 'CSweet.Compute.HyperV' }
            (Get-Service -Name 'CSweet.Compute.HyperV').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
            # A retained provider can outlive a recreated C-Sweet database. Restore only its
            # existing public identity and signed catalog before reporting completion.
            & $existingExecutable reenroll-local $HandoffPath
            if ($LASTEXITCODE -ne 0) { throw 'The existing compute service could not restore its enrollment.' }
            & $existingExecutable complete-local $HandoffPath
            if ($LASTEXITCODE -ne 0) { throw 'The existing compute service could not complete this setup.' }
            $installerState = 'Completed'
            return
        }
    }
    # The Office image service owns prerequisites and immutable Linux image preparation.
    $imagePath = & (Join-Path $repositoryRoot 'scripts\Ensure-ComputeLinuxImage.ps1') -SetupProgressPath $setupProgress
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw 'The Linux test image was not prepared.' }
    $publishRoot = Join-Path $logRoot 'provider'
    $acceptanceRoot = Join-Path $logRoot 'acceptance'
    Write-ComputeSetupProgress -Path $setupProgress -Message 'Checking the compute service.'
    dotnet test (Join-Path $repositoryRoot 'tests\CSweet.UnitTests\CSweet.UnitTests.csproj') -c Release `
        --disable-build-servers -m:1 -p:UseSharedCompilation=false -nr:false `
        --filter 'FullyQualifiedName~Compute&FullyQualifiedName!~Postgres' `
        --logger 'trx;LogFileName=compute.trx' --results-directory $acceptanceRoot --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The compute component checks did not pass.' }
    Write-ComputeSetupProgress -Path $setupProgress -Message 'Installing the compute service.'
    dotnet publish (Join-Path $repositoryRoot 'src\CSweet.Compute.HyperV\CSweet.Compute.HyperV.csproj') `
        -c Release -r win-x64 --self-contained true --disable-build-servers -m:1 -p:UseSharedCompilation=false -nr:false -o $publishRoot --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The compute provider could not be prepared.' }
    $protectedRoot = Join-Path $env:ProgramData 'CSweet\Compute'
    if (Test-Path -LiteralPath $protectedRoot) {
        $entry = Get-Item -LiteralPath $protectedRoot -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The compute installation path redirects elsewhere.' }
    }
    # Check existing descendants before any elevated copy or recursive ownership update.
    if (Test-Path -LiteralPath $protectedRoot) {
        $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($protectedRoot)
        while ($pending.Count -gt 0) {
            foreach ($child in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The compute installation contains a redirected path.' }
                if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
            }
        }
    }
    New-Item -ItemType Directory -Path $protectedRoot -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl.SetOwner($admins)
    foreach ($sid in @($admins, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $protectedRoot -AclObject $acl
    $runtimeRoot = Join-Path $protectedRoot 'runtime'
    $imagesRoot = Join-Path $protectedRoot 'images'
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $imagesRoot -Force | Out-Null
    $service = Get-Service -Name 'CSweet.Compute.HyperV' -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        # The application schedules image upgrades only after all resource teardown is confirmed.
        # Keep the enrollment, signing key and replay journal; only replace the service payload.
        $backupRoot = Join-Path $protectedRoot ('upgrade-backup-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backupRoot | Out-Null
        foreach ($name in @('provider.json','provisioning.json','catalog.json')) {
            $existingPath = Join-Path $protectedRoot $name
            if (Test-Path -LiteralPath $existingPath) { Copy-Item -LiteralPath $existingPath -Destination $backupRoot }
        }
        Copy-Item -LiteralPath $runtimeRoot -Destination (Join-Path $backupRoot 'runtime') -Recurse
        $backupReady = $true
        Stop-Service -Name 'CSweet.Compute.HyperV' -ErrorAction Stop
        (Get-Service -Name 'CSweet.Compute.HyperV').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    # Remove stale payload files after backup. The resolved target is confined to this
    # protected installation and descendants were checked for redirected paths above.
    $resolvedRuntime = [IO.Path]::GetFullPath($runtimeRoot)
    if ($resolvedRuntime -ne [IO.Path]::GetFullPath((Join-Path $protectedRoot 'runtime'))) { throw 'Unexpected runtime path.' }
    Get-ChildItem -LiteralPath $resolvedRuntime -Force | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $publishRoot -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeRoot -Force }
    $installedImage = Join-Path $imagesRoot (Split-Path -Leaf $imagePath)
    Copy-Item -LiteralPath $imagePath -Destination $installedImage -Force
    # Elevation can otherwise preserve the user's owner SID on new files. Set trusted ownership explicitly.
    & "$env:SystemRoot\System32\icacls.exe" $protectedRoot '/setowner' '*S-1-5-32-544' '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Compute payload ownership could not be protected.' }
    $providerExecutable = Join-Path $runtimeRoot 'CSweet.Compute.HyperV.exe'
    & $providerExecutable configure-local $HandoffPath $installedImage (Join-Path $acceptanceRoot 'compute.trx')
    if ($LASTEXITCODE -ne 0) { throw 'The compute provider could not be enrolled.' }
    & (Join-Path $repositoryRoot 'scripts\Register-ComputeGuestService.ps1') | Out-Host
    & $providerExecutable validate-service
    if ($LASTEXITCODE -ne 0) { throw 'Compute installation validation failed.' }
    if ($null -eq $service) {
        New-Service -Name 'CSweet.Compute.HyperV' -DisplayName 'C-Sweet Compute' -BinaryPathName ('"' + $providerExecutable + '" service') -StartupType Automatic | Out-Null
    }
    & (Join-Path $repositoryRoot 'scripts\Set-ComputeServiceRecovery.ps1')
    Start-Service -Name 'CSweet.Compute.HyperV'
    (Get-Service -Name 'CSweet.Compute.HyperV').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    & $providerExecutable complete-local $HandoffPath
    if ($LASTEXITCODE -ne 0) { throw 'Compute readiness could not be reported.' }
    $installerState = 'Completed'
} catch {
    if ($backupReady) {
        try {
            # Restore the last working service payload without touching enrollment keys,
            # workload disks or journals if the identity-preserving upgrade did not finish.
            Stop-Service -Name 'CSweet.Compute.HyperV' -ErrorAction SilentlyContinue
            (Get-Service -Name 'CSweet.Compute.HyperV').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            foreach ($name in @('provider.json','provisioning.json','catalog.json')) {
                $savedPath = Join-Path $backupRoot $name
                if (Test-Path -LiteralPath $savedPath) { Copy-Item -LiteralPath $savedPath -Destination $protectedRoot -Force }
            }
            $restoreRuntime = [IO.Path]::GetFullPath($runtimeRoot)
            if ($restoreRuntime -ne [IO.Path]::GetFullPath((Join-Path $protectedRoot 'runtime'))) { throw 'Unexpected rollback path.' }
            Get-ChildItem -LiteralPath $restoreRuntime -Force | Remove-Item -Recurse -Force
            Get-ChildItem -LiteralPath (Join-Path $backupRoot 'runtime') -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $runtimeRoot -Recurse -Force
            }
            Start-Service -Name 'CSweet.Compute.HyperV' -ErrorAction Stop
        } catch { Write-Warning 'The previous compute service could not be restarted automatically.' }
    }
    # The application worker reports failure from the process exit. Never print the handoff or secret.
    Write-Warning $_.Exception.Message
    Write-Error 'C-Sweet could not finish preparing local Linux compute.'
    exit 1
} finally {
    try { Write-ComputeInstallerReceipt -Directory $logRoot -AttemptHash $attemptHash -State $installerState }
    finally { Stop-Transcript | Out-Null }
}
