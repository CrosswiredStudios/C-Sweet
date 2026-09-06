param([Parameter(Mandatory = $true)][string]$StorageParent)
$ErrorActionPreference = "Stop"
if (![IO.Path]::IsPathFullyQualified($StorageParent) -or !(Test-Path -LiteralPath $StorageParent -PathType Container)) {
    throw "Supply an existing absolute local directory, mounted NAS directory, or UNC share directory."
}
$testParent = (Resolve-Path -LiteralPath $StorageParent).ProviderPath
$previous = [Environment]::GetEnvironmentVariable("CSWEET_TEST_STORAGE_PARENT", "Process")
Push-Location ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..")))
try {
    $env:CSWEET_TEST_STORAGE_PARENT = $testParent
    & dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --no-restore --artifacts-path .artifacts/internal-git/verified --filter "FullyQualifiedName~InternalGitFilesystemIntegrationTests" --logger "console;verbosity=normal" --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Filesystem source-control integration test failed. Inspect the test output before using this storage." }
}
finally {
    [Environment]::SetEnvironmentVariable("CSWEET_TEST_STORAGE_PARENT", $previous, "Process")
    Pop-Location
}
