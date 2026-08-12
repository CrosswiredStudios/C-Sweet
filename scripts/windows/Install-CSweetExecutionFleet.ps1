[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PayloadRoot,
    [Parameter(Mandatory = $true)] [string] $ControlPlaneUrl,
    [switch] $Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'Administrator approval is required.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        ('"' + $PSCommandPath + '"'), '-PayloadRoot', ('"' + $PayloadRoot + '"'),
        '-ControlPlaneUrl', ('"' + $ControlPlaneUrl + '"'), '-Elevated')
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet installation failed with exit code $($process.ExitCode)." }
    return
}

$secureToken = Read-Host 'Paste the one-use execution-node enrollment token' -AsSecureString
$credential = [PSCredential]::new('token', $secureToken)
$token = $credential.GetNetworkCredential().Password
if ($token.Length -lt 32 -or $token.Length -gt 256) { throw 'The enrollment token is invalid.' }
$inputPath = Join-Path $env:TEMP "csweet-enrollment-$([guid]::NewGuid().ToString('N')).secret"
try {
    [IO.File]::WriteAllText($inputPath, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    & (Join-Path $PSScriptRoot 'Install-CSweetRuntimeHost.ps1') -PayloadRoot $PayloadRoot `
        -ControlPlaneUrl $ControlPlaneUrl -EnrollmentTokenInputPath $inputPath
    if ($LASTEXITCODE -ne 0) { throw 'The execution fleet installer failed.' }
} finally {
    if (Test-Path -LiteralPath $inputPath) { Remove-Item -LiteralPath $inputPath -Force }
}
