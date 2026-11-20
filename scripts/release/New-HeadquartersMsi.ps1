#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DistributionRoot,
    [Parameter(Mandatory)] [string] $OutputPath,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $ProductVersion,
    [string] $Wix = 'wix'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $DistributionRoot).FullName
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "The output already exists: $output" }
$services = @('CSweet.Api', 'CSweet.AgentHost', 'CSweet.WorkerHost', 'CSweet.ExecutionGateway',
    'CSweet.GitHost', 'CSweet.SourceControlProvisionerHost')
$expected = $services + 'CSweet.Migrator'
$metadata = Get-Content -Raw -LiteralPath (Join-Path $root 'build-info.json') | ConvertFrom-Json
if ($metadata.runtime -ne 'win-x64' -or (Compare-Object $expected $metadata.services)) {
    throw 'The input must be a complete win-x64 Headquarters distribution.'
}
foreach ($service in $expected) {
    if (-not (Test-Path -PathType Leaf -LiteralPath (Join-Path $root "$service/$service.exe"))) {
        throw "The distribution is missing $service."
    }
}
if (-not (Test-Path -PathType Leaf -LiteralPath (Join-Path $root 'web/index.html'))) { throw 'Missing web application.' }

$wixNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
$xml = [xml]"<Wix xmlns='$wixNamespace'><Fragment><DirectoryRef Id='HeadquartersFolder'/><ComponentGroup Id='HeadquartersPayload'/></Fragment></Wix>"
$group = $xml.Wix.Fragment.ComponentGroup
function Add-WixElement($Parent, [string] $Name, [hashtable] $Attributes) {
    $element = $xml.CreateElement($Name, $wixNamespace)
    foreach ($attribute in $Attributes.GetEnumerator()) { $element.SetAttribute($attribute.Key, $attribute.Value) }
    [void]$Parent.AppendChild($element)
    return $element
}
function Get-StableId([string] $relative) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 32)
}
function Add-Directory($parent, [IO.DirectoryInfo] $directory) {
    foreach ($entry in $directory.EnumerateFileSystemInfos() | Sort-Object Name) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected link: $($entry.FullName)" }
        $relative = [IO.Path]::GetRelativePath($root, $entry.FullName)
        $id = Get-StableId $relative
        if ($entry -is [IO.DirectoryInfo]) {
            $child = Add-WixElement $parent 'Directory' @{ Id = "dir_$id"; Name = $entry.Name }
            Add-Directory $child $entry
            continue
        }
        $component = Add-WixElement $parent 'Component' @{ Id = "cmp_$id"; Guid = '*' }
        [void](Add-WixElement $component 'File' @{ Id = "file_$id"; Name = $entry.Name; KeyPath = 'yes'; Source = $entry.FullName })
        [void](Add-WixElement $group 'ComponentRef' @{ Id = "cmp_$id" })
        $service = $directory.Name
        if ($service -in $expected -and $relative -eq "$service\$service.exe") {
            $startType = if ($service -eq 'CSweet.Migrator') { 'demand' } else { 'auto' }
            [void](Add-WixElement $component 'ServiceInstall' @{
                Id = "install_$id"; Name = $service; DisplayName = "C-Sweet Headquarters ($service)"
                Type = 'ownProcess'; Start = $startType; ErrorControl = 'normal'; Account = "NT SERVICE\$service"; Vital = 'yes'
            })
            $control = @{ Id = "control_$id"; Name = $service; Stop = 'both'; Remove = 'uninstall'; Wait = 'yes' }
            if ($startType -eq 'auto') { $control.Start = 'install' }
            [void](Add-WixElement $component 'ServiceControl' $control)
        }
    }
}
Add-Directory $xml.Wix.Fragment.DirectoryRef (Get-Item -LiteralPath $root)
$working = [IO.Directory]::CreateTempSubdirectory('csweet-hq-msi-').FullName
try {
    $payload = Join-Path $working 'Payload.wxs'
    $xml.Save($payload)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    & $Wix build -arch x64 -d "ProductVersion=$ProductVersion" -d "ScriptRoot=$PSScriptRoot\windows" -intermediateFolder $working -o $output (Join-Path $PSScriptRoot 'windows/Headquarters.wxs') $payload
    if ($LASTEXITCODE -ne 0) { throw 'WiX could not build the Headquarters MSI.' }
} finally {
    if ([IO.Path]::GetDirectoryName($working) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($working).StartsWith('csweet-hq-msi-')) { Remove-Item -LiteralPath $working -Recurse -Force }
}
