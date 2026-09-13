[CmdletBinding()]
param(
    [string] $IsolationRoot = "$PSScriptRoot\..\..\CSweet.Isolation",
    [string] $OutputPath = "$PSScriptRoot\..\artifacts\compute\mvp\images\ubuntu-compute.vhdx",
    [string] $SwitchName = 'Default Switch',
    [string] $UbuntuVersion = '24.04.4',
    [string] $PackerVersion = '1.15.4',
    [scriptblock] $ReportProgress = { param($phase, $message) Write-Host $message }
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $IsolationRoot 'tools\LinuxImage\CSweet.LinuxImage.psd1') -Force
$prepare = {
    param($payload)
    $guest = Join-Path $payload 'guest'
    dotnet publish (Join-Path $repositoryRoot 'src\CSweet.Compute.Guest\CSweet.Compute.Guest.csproj') `
        -c Release -r linux-x64 --self-contained true -p:InvariantGlobalization=true -o $guest
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $guest 'CSweet.Compute.Guest'))) {
        throw 'The self-contained Linux compute guest publish failed.'
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\Install-ComputeGuestLinux.sh') -Destination $payload
}.GetNewClosure()
$result = New-CSweetLinuxHyperVImage -ProfileDirectory (Join-Path $repositoryRoot 'build\compute-linux') `
    -PreparePayload $prepare -GuestServiceName 'csweet-compute-guest.service' `
    -ArtifactDirectory (Join-Path $IsolationRoot 'artifacts\linux-images') -OutputPath $OutputPath `
    -SwitchName $SwitchName -UbuntuVersion $UbuntuVersion -PackerVersion $PackerVersion -ReportProgress $ReportProgress
$result | ConvertTo-Json | Set-Content -LiteralPath ($result.ImagePath + '.build.json') -Encoding UTF8
Write-Output $result.ImagePath
