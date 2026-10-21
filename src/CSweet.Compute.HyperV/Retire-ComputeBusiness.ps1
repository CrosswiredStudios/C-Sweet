# Installer/service-owned retry task. Never accepts a caller-selected deletion path.
# The receipt capability only acknowledges this exact retirement; it grants no other API access.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$requestPath = Join-Path $PSScriptRoot 'request.json'
$request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
$business = ([guid]$request.businessId).ToString('N')
$node = ([guid]$request.nodeId).ToString('N')
if ($business -eq [guid]::Empty.ToString('N') -or $node -eq [guid]::Empty.ToString('N')) { throw 'Invalid retirement identity.' }
$machine = Join-Path $env:ProgramData 'CSweet'
$root = [IO.Path]::GetFullPath((Join-Path $machine ('ComputeBusinesses\' + $business)))
$taskRoot = [IO.Path]::GetFullPath((Join-Path $machine ('ComputeRetirement\' + $business)))
if ([IO.Path]::GetFullPath($PSScriptRoot) -ine $taskRoot) { throw 'Invalid task location.' }
$serviceName = 'CSweet.Compute.HyperV.' + $business

function Assert-NoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked cleanup path.' }
        }
        $cursor = Split-Path -Parent $cursor
    }
}
function Assert-Tree([string]$Path) {
    Assert-NoLinks $Path
    if (-not (Test-Path -LiteralPath $Path)) { return }
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked cleanup entry.' }
        if ($entry.PSIsContainer) { Assert-Tree $entry.FullName }
    }
}
function Test-InRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    return $full -ieq $root -or $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
}
Assert-NoLinks $taskRoot
$origin = [uri]$request.coreOrigin
if ($origin.Scheme -cne 'https' -or $origin.UserInfo -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment) { throw 'Invalid Core origin.' }
if ($request.coreCertificateSha256) {
    $pin = [string]$request.coreCertificateSha256
    if ($pin -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid Core TLS pin.' }
    # TLS callbacks run on framework threads; avoid a PowerShell script-block callback requiring a runspace.
    if (-not ('CSweetRetirementTls' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
public static class CSweetRetirementTls {
    public static string Pin;
    public static readonly RemoteCertificateValidationCallback Callback = new RemoteCertificateValidationCallback(Validate);
    public static bool Validate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) {
        if (cert == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        using (var sha = SHA256.Create()) {
            var digest = BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
            var identity = new X509Certificate2(cert);
            return string.Equals(digest, Pin, StringComparison.OrdinalIgnoreCase) &&
                identity.NotBefore.ToUniversalTime() <= DateTime.UtcNow && identity.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        }
    }
}
'@
    }
    [CSweetRetirementTls]::Pin = $pin
    [Net.ServicePointManager]::ServerCertificateValidationCallback = [CSweetRetirementTls]::Callback
}
$body = @{cleanupId=$request.cleanupId; receiptToken=$request.receiptToken} | ConvertTo-Json -Compress
$authorization = Invoke-WebRequest -UseBasicParsing -Method Post -Uri ([uri]::new($origin,'api/compute/provider/retirement-authorize')) -ContentType 'application/json' -Body $body -MaximumRedirection 0 -TimeoutSec 30
if ($authorization.StatusCode -ne 204) { throw 'Current Core state does not authorize retirement.' }
Assert-Tree $root
if (Test-Path -LiteralPath $root) {
    $configurationPath = Join-Path $root 'provider.json'
    if (-not (Test-Path -LiteralPath $configurationPath)) { $configurationPath = Join-Path $root 'compute-owner.json' }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if (([guid]$configuration.enrollment.organizationId).ToString('N') -ne $business -or
        ([guid]$configuration.enrollment.nodeId).ToString('N') -ne $node) { throw 'Provider ownership changed.' }
    if ($configuration.PSObject.Properties['certificateThumbprint'] -and $configuration.certificateThumbprint) {
        $request.certificateThumbprint = $configuration.certificateThumbprint
        $incoming = Join-Path $taskRoot 'request.incoming'
        $request | ConvertTo-Json -Compress | Set-Content -LiteralPath $incoming -Encoding UTF8
        Move-Item -LiteralPath $incoming -Destination $requestPath -Force
    }
    Import-Module Hyper-V -ErrorAction Stop
    # A remaining VM, checkpoint or external child of a business base image blocks retirement.
    foreach ($vm in Get-VM -ErrorAction Stop) {
        $media = @($vm | Get-VMHardDiskDrive | Select-Object -ExpandProperty Path) + @($vm | Get-VMDvdDrive | Select-Object -ExpandProperty Path)
        foreach ($snapshot in @($vm | Get-VMSnapshot)) {
            $media += @($snapshot | Get-VMHardDiskDrive | Select-Object -ExpandProperty Path)
            $media += @($snapshot | Get-VMDvdDrive | Select-Object -ExpandProperty Path)
        }
        foreach ($location in @($vm.Path,$vm.ConfigurationLocation,$vm.SnapshotFileLocation,$vm.SmartPagingFilePath) + $media) {
            if (Test-InRoot $location) { throw 'A VM still references business storage.' }
        }
        foreach ($disk in @($media | Where-Object { $_ -match '\.(a?vhdx?)$' })) {
            $seen = @{}; $parent = $disk
            while ($parent) {
                if ($seen.ContainsKey($parent)) { throw 'Cyclic disk chain.' }; $seen[$parent] = $true
                $parent = (Get-VHD -Path $parent -ErrorAction Stop).ParentPath
                if ($parent -and (Test-InRoot $parent)) { throw 'A VM uses a business base image.' }
            }
        }
    }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($service) {
        $expected = '"' + (Join-Path $root 'runtime\CSweet.Compute.HyperV.exe') + '" service ' + $business
        if ($service.PathName -ine $expected) { throw 'Service executable ownership changed.' }
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
    }
    Assert-Tree $root
    # The fully resolved, fixed business root was checked above, including all descendants.
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
}
if (Test-Path -LiteralPath $root) { throw 'Business storage remains.' }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'Business service remains.' }

# Remove only this installer's dedicated signing certificate/key. Shared credentials are retained.
$thumbprint = [string]$request.certificateThumbprint
if ($thumbprint -and $thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid certificate identity.' }
if (-not $thumbprint -and -not $request.preserveSharedProvider) {
    # Configure may have created its key/certificate before writing provider.json.
    foreach ($candidate in Get-ChildItem -LiteralPath 'Cert:\LocalMachine\My') {
        if ($candidate.Subject -ne 'CN=C-Sweet Local Compute') { continue }
        $key = $null
        try {
            $key = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($candidate)
            if ($key -and $key.Key.KeyName -ceq ('CSweet.Compute.' + $node)) { $thumbprint = $candidate.Thumbprint; break }
        } finally { if ($key) { $key.Dispose() } }
    }
}
$certificate = if ($thumbprint) { Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $thumbprint) -ErrorAction SilentlyContinue } else { $null }
if ($certificate -and -not $request.preserveSharedProvider) {
    $private = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate)
    try {
        if ($private.Key.KeyName -cne ('CSweet.Compute.' + $node)) { throw 'Signing key ownership changed.' }
        $private.Key.Delete()
    } finally { if ($private) { $private.Dispose() } }
    Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $thumbprint) -Force
}
if (-not $request.preserveSharedProvider) {
    $provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $options = [Security.Cryptography.CngKeyOpenOptions]::MachineKey
    if ([Security.Cryptography.CngKey]::Exists(('CSweet.Compute.' + $node), $provider, $options)) {
        $orphanKey = [Security.Cryptography.CngKey]::Open(('CSweet.Compute.' + $node), $provider, $options)
        try { $orphanKey.Delete() } finally { $orphanKey.Dispose() }
    }
}
$response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri ([uri]::new($origin,'api/compute/provider/retirement-receipt')) -ContentType 'application/json' -Body $body -MaximumRedirection 0 -TimeoutSec 30
if ($response.StatusCode -ne 204) { throw 'Core has not acknowledged retirement.' }
Assert-Tree $taskRoot
Remove-Item -LiteralPath $taskRoot -Recurse -Force
Unregister-ScheduledTask -TaskName ('CSweet.Compute.Retire.' + $business) -Confirm:$false
