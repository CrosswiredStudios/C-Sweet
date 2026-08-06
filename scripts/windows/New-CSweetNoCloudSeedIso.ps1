[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourceDirectory,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')]
    [string] $VolumeLabel = 'CIDATA'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = [IO.Path]::GetFullPath($SourceDirectory)
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "The NoCloud seed source directory does not exist: $source"
}
foreach ($requiredName in @('meta-data', 'user-data')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $requiredName) -PathType Leaf)) {
        throw "The NoCloud seed is missing $requiredName."
    }
}
if ([IO.Path]::GetPathRoot($output) -eq $output -or [IO.Path]::GetExtension($output) -ine '.iso') {
    throw 'OutputPath must be a non-root .iso path.'
}

$outputDirectory = [IO.Path]::GetDirectoryName($output)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$isoStreamWriter = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CSweet.Windows
{
    public static class ComIsoStreamWriter
    {
        public static void Write(object source, string path)
        {
            if (source == null) throw new ArgumentNullException("source");
            var input = (IStream)source;
            var buffer = new byte[1024 * 1024];
            var bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        Marshal.WriteInt32(bytesReadPointer, 0);
                        input.Read(buffer, buffer.Length, bytesReadPointer);
                        var bytesRead = Marshal.ReadInt32(bytesReadPointer);
                        if (bytesRead <= 0) break;
                        destination.Write(buffer, 0, bytesRead);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(bytesReadPointer);
            }
        }
    }
}
'@
if (-not ('CSweet.Windows.ComIsoStreamWriter' -as [type])) {
    Add-Type -TypeDefinition $isoStreamWriter -Language CSharp
}

$fileSystemImage = $null
$resultImage = $null
$imageStream = $null
try {
    # IMAPI2 is built into supported Windows client editions. Using it here avoids
    # requiring the Windows ADK, xorriso, or another setup-time dependency.
    $fileSystemImage = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    # ISO9660 keeps the CIDATA label broadly discoverable; Joliet preserves the
    # required lowercase NoCloud file names instead of reducing them to 8.3 names.
    $fileSystemImage.FileSystemsToCreate = 3 # FsiFileSystemISO9660 | FsiFileSystemJoliet
    $fileSystemImage.VolumeName = $VolumeLabel.ToUpperInvariant()
    $fileSystemImage.Root.AddTree($source, $false)
    $resultImage = $fileSystemImage.CreateResultImage()
    $imageStream = $resultImage.ImageStream
    [CSweet.Windows.ComIsoStreamWriter]::Write($imageStream, $output)
} finally {
    foreach ($value in @($imageStream, $resultImage, $fileSystemImage)) {
        if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
        }
    }
}

if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or (Get-Item -LiteralPath $output).Length -eq 0) {
    throw 'Windows could not create the NoCloud seed ISO.'
}

Write-Output $output
