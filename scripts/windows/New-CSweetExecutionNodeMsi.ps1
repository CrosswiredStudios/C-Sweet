[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PayloadRoot,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [guid] $UpgradeCode = '8917027b-53f1-4c36-946f-1a704349352e'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use MAJOR.MINOR.PATCH.' }
if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40,128}$') { throw 'CertificateThumbprint must be hexadecimal.' }
$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) { throw 'PayloadRoot does not exist.' }
if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists: $OutputPath" }
foreach ($required in @('runtime-manifest.json', 'runtime\CSweet.RuntimeHost.exe',
        'node\CSweet.ExecutionNode.exe', 'helper\CSweet.AgentRuntime.HyperV.Helper.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot $required) -PathType Leaf)) {
        throw "Payload is missing $required."
    }
}
$reparsePoint = Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Force |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
    Select-Object -First 1
if ($null -ne $reparsePoint) { throw "Payload may not contain reparse points: $($reparsePoint.FullName)" }

$scriptFiles = @('Install-CSweetExecutionFleet.ps1', 'Install-CSweetRuntimeHost.ps1',
    'CSweet.WindowsSetupProgress.ps1', 'Uninstall-CSweetExecutionFleet.ps1')
foreach ($file in $scriptFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) {
        throw "Installer source is missing $file."
    }
}

$wixCommand = Get-Command wix.exe, wix -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $wixCommand) { throw 'WiX Toolset v4 (wix) is required.' }
$signTool = Get-Command signtool.exe, signtool -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $signTool) { throw 'Windows SDK signtool is required.' }

$buildRoot = Join-Path ([IO.Path]::GetTempPath()) "csweet-msi-$([guid]::NewGuid().ToString('N'))"
$stageRoot = Join-Path $buildRoot 'stage'
$wxsPath = Join-Path $buildRoot 'csweet-execution-node.wxs'
try {
    New-Item -ItemType Directory -Path $stageRoot, (Split-Path -Parent $OutputPath) -Force | Out-Null
    foreach ($file in $scriptFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stageRoot $file)
    }
    Copy-Item -LiteralPath $PayloadRoot -Destination (Join-Path $stageRoot 'payload') -Recurse

    function XmlEscape([string] $Value) {
        return [Security.SecurityElement]::Escape($Value)
    }
    function StableId([string] $Prefix, [string] $Value) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose() }
        return "${Prefix}_$($hash.Substring(0, 24))"
    }

    $componentIds = [Collections.Generic.List[string]]::new()
    $xml = [Text.StringBuilder]::new()
    function AddLine([int] $Indent, [string] $Value) {
        [void]$xml.Append(('  ' * $Indent)).AppendLine($Value)
    }
    function AddDirectoryContents([string] $DirectoryPath, [string] $RelativePath, [int] $Indent) {
        foreach ($file in Get-ChildItem -LiteralPath $DirectoryPath -File | Sort-Object Name) {
            $relativeFile = if ($RelativePath) { "$RelativePath\$($file.Name)" } else { $file.Name }
            $componentId = StableId 'cmp' $relativeFile
            $fileId = StableId 'fil' $relativeFile
            $componentIds.Add($componentId)
            AddLine $Indent "<Component Id=`"$componentId`" Guid=`"*`">"
            AddLine ($Indent + 1) "<File Id=`"$fileId`" Source=`"$(XmlEscape $file.FullName)`" Name=`"$(XmlEscape $file.Name)`" KeyPath=`"yes`" />"
            AddLine $Indent '</Component>'
        }
        foreach ($directory in Get-ChildItem -LiteralPath $DirectoryPath -Directory | Sort-Object Name) {
            $relativeDirectory = if ($RelativePath) { "$RelativePath\$($directory.Name)" } else { $directory.Name }
            $directoryId = StableId 'dir' $relativeDirectory
            AddLine $Indent "<Directory Id=`"$directoryId`" Name=`"$(XmlEscape $directory.Name)`">"
            AddDirectoryContents $directory.FullName $relativeDirectory ($Indent + 1)
            AddLine $Indent '</Directory>'
        }
    }

    AddLine 0 '<?xml version="1.0" encoding="utf-8"?>'
    AddLine 0 '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
    AddLine 1 "<Package Name=`"C-Sweet Execution Node`" Manufacturer=`"C-Sweet`" Version=`"$Version`" UpgradeCode=`"{$($UpgradeCode.ToString().ToUpperInvariant())}`" Scope=`"perMachine`" InstallerVersion=`"500`">"
    AddLine 2 '<MajorUpgrade DowngradeErrorMessage="A newer C-Sweet Execution Node package is already installed." />'
    AddLine 2 '<MediaTemplate EmbedCab="yes" />'
    AddLine 2 '<CustomAction Id="RemoveExecutionFleet" Directory="INSTALLFOLDER" Execute="deferred" Impersonate="no" Return="check" HideTarget="yes" ExeCommand="&quot;[SystemFolder]WindowsPowerShell\\v1.0\\powershell.exe&quot; -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File &quot;[INSTALLFOLDER]Uninstall-CSweetExecutionFleet.ps1&quot; -Elevated" />'
    AddLine 2 '<InstallExecuteSequence>'
    AddLine 3 '<Custom Action="RemoveExecutionFleet" Before="RemoveFiles" Condition="REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE" />'
    AddLine 2 '</InstallExecuteSequence>'
    AddLine 2 '<StandardDirectory Id="ProgramFiles64Folder">'
    AddLine 3 '<Directory Id="CSweetVendorFolder" Name="CSweet">'
    AddLine 4 '<Directory Id="INSTALLFOLDER" Name="ExecutionNodeInstaller">'
    AddDirectoryContents $stageRoot '' 5
    AddLine 4 '</Directory>'
    AddLine 3 '</Directory>'
    AddLine 2 '</StandardDirectory>'
    AddLine 2 '<Feature Id="MainFeature" Title="C-Sweet Execution Node" Level="1">'
    foreach ($componentId in $componentIds) { AddLine 3 "<ComponentRef Id=`"$componentId`" />" }
    AddLine 2 '</Feature>'
    AddLine 1 '</Package>'
    AddLine 0 '</Wix>'
    [IO.File]::WriteAllText($wxsPath, $xml.ToString(), [Text.UTF8Encoding]::new($false))

    & $wixCommand.Source build -arch x64 -o $OutputPath $wxsPath
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the MSI.' }
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $OutputPath
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed to sign the MSI.' }
    & $signTool.Source verify /pa /all $OutputPath
    if ($LASTEXITCODE -ne 0) { throw 'The MSI Authenticode signature could not be verified.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $OutputPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The MSI signature is not valid: $($signature.StatusMessage)"
    }
    Write-Host "Created signed Windows installer at $OutputPath"
} finally {
    if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
}
