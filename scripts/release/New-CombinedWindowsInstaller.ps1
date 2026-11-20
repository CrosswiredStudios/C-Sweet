#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $HeadquartersMsi,
    [Parameter(Mandatory)] [string] $OfficeMsi,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $ProductVersion,
    [Parameter(Mandatory)] [ValidatePattern('^[a-fA-F0-9]{40}$')] [string] $CertificateThumbprint,
    [ValidatePattern('^[a-fA-F0-9]{40}$')] [string] $OfficeCertificateThumbprint = $CertificateThumbprint,
    [string] $Wix = 'wix',
    [string] $BalExtension = 'WixToolset.Bal.wixext',
    [string] $SignTool = 'signtool',
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $LocalTestSignatureVerifier
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$hq = (Get-Item -LiteralPath $HeadquartersMsi).FullName
$office = (Get-Item -LiteralPath $OfficeMsi).FullName
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory for the complete installer layout.' }
$work = [IO.Directory]::CreateTempSubdirectory('csweet-bundle-').FullName

function Test-SignedFile([string] $Path, [string] $Signer) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.SignerCertificate.Thumbprint -ine $Signer) { throw "Unexpected signer on $Path" }
    if ($LocalTestSignatureVerifier) {
        $cert = $signature.SignerCertificate
        if ($cert.Subject -ne $cert.Issuer) { throw 'Use a self-signed certificate for a local test bundle.' }
        $pem = Join-Path $work "$Signer.pem"
        [IO.File]::WriteAllText($pem, $cert.ExportCertificatePem())
        & $LocalTestSignatureVerifier verify -CAfile $pem -require-leaf-hash "sha256:$($cert.GetCertHashString('SHA256'))" -in $Path
    } else { & $SignTool verify /pa /all $Path }
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $Path" }
}
function Sign-BundleFile([string] $Path) {
    $signArgs = @('sign', '/s', 'My', '/sha1', $CertificateThumbprint, '/fd', 'SHA256')
    if (-not $LocalTestSignatureVerifier) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    & $SignTool @signArgs $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
    Test-SignedFile $Path $CertificateThumbprint
}
try {
    Test-SignedFile $hq $CertificateThumbprint
    Test-SignedFile $office $OfficeCertificateThumbprint
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($office, 0)
    $media = $database.OpenView('SELECT `Cabinet` FROM `Media`')
    $media.Execute()
    while ($record = $media.Fetch()) {
        $cab = $record.StringData(1)
        if (-not $cab -or $cab.StartsWith('#')) { continue }
        if ($cab -cnotmatch '^[A-Za-z0-9_.-]+\.cab$') { throw 'Invalid external cabinet name in the Office MSI.' }
        Test-SignedFile (Join-Path ([IO.Path]::GetDirectoryName($office)) $cab) $OfficeCertificateThumbprint
    }
    $media.Close()
    $name = 'C-Sweet Headquarters and Office'
    $upgrade = '09e76a7b-e89c-4349-9a9b-2bf86c3cee79'
    if ($LocalTestSignatureVerifier) {
        $name += ' (local signing test)'
        $upgrade = '898b8337-8ab4-473f-8b01-1051e85c7182'
    }
    $layout = Join-Path $output 'C-Sweet-Setup.exe'
    $engine = Join-Path $work 'engine.exe'
    $signed = Join-Path $work 'signed.exe'
    & $Wix build -arch x64 -ext $BalExtension -d "ProductVersion=$ProductVersion" -d "BundleName=$name" -d "UpgradeCode=$upgrade" `
        -d "HeadquartersMsi=$hq" -d "OfficeMsi=$office" -intermediateFolder $work -o $layout (Join-Path $PSScriptRoot 'windows/Combined.wxs')
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the combined installer.' }
    & $Wix burn detach $layout -engine $engine
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the Burn engine.' }
    Sign-BundleFile $engine
    & $Wix burn reattach $layout -engine $engine -o $signed
    if ($LASTEXITCODE -ne 0) { throw 'Failed to reattach the signed Burn engine.' }
    Sign-BundleFile $signed
    Copy-Item -LiteralPath $signed -Destination $layout -Force
    Write-Host "Combined installer layout: $output"
} finally {
    if ([IO.Path]::GetDirectoryName($work) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($work).StartsWith('csweet-bundle-')) { Remove-Item -LiteralPath $work -Recurse -Force }
}
