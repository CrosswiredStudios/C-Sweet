# Internal retained-service update. The caller stops the service and supplies its reviewed publish output.
function Install-ComputeProviderRuntime {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$ProtectedRoot,
        [Parameter(Mandatory=$true)][string]$PublishRoot,
        [Parameter(Mandatory=$true)][scriptblock]$Validate)
    $ErrorActionPreference = "Stop"
    $root = [IO.Path]::GetFullPath($ProtectedRoot)
    $published = [IO.Path]::GetFullPath($PublishRoot)
    function Assert-PlainTree([string]$Path) {
        $ancestor = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))
        while ($null -ne $ancestor) {
            if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Runtime update path redirects elsewhere.' }
            $ancestor = $ancestor.Parent
        }
        $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($Path)
        while ($pending.Count -gt 0) {
            foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Runtime update contains a redirected path.' }
                if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
            }
        }
    }
    function Child([string]$Name) {
        $path = [IO.Path]::GetFullPath((Join-Path $root $Name))
        if ([IO.Path]::GetDirectoryName($path) -cne $root) { throw 'Unexpected runtime update target.' }
        return $path
    }
    $runtime = Child 'runtime'
    Assert-PlainTree $runtime; Assert-PlainTree $published
    if (-not (Test-Path -LiteralPath (Join-Path $published 'CSweet.Compute.HyperV.exe') -PathType Leaf)) { throw 'Published provider entry point is missing.' }
    $stage = Child ('runtime-stage-' + [guid]::NewGuid().ToString('N'))
    $backup = Child ('runtime-backup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem -LiteralPath $published -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse }
    Assert-PlainTree $stage
    # Both resolved move targets are direct children of the exact protected installation.
    Move-Item -LiteralPath $runtime -Destination $backup
    try {
        Move-Item -LiteralPath $stage -Destination $runtime
        & $Validate (Join-Path $runtime 'CSweet.Compute.HyperV.exe')
    } catch {
        if (Test-Path -LiteralPath $runtime) { Move-Item -LiteralPath $runtime -Destination (Child ('runtime-failed-' + [guid]::NewGuid().ToString('N'))) }
        Move-Item -LiteralPath $backup -Destination $runtime
        throw
    }
    # Keep the previous payload recoverable. Enrollment, keys, catalog, images, workloads and journal are untouched.
    return $backup
}
