param(
    [Parameter(Mandatory = $true)][string]$DotNetImage,
    [Parameter(Mandatory = $true)][string]$NodeImage,
    [Parameter(Mandatory = $true)][string]$PythonImage,
    [Parameter(Mandatory = $true)][string]$PowerShellImage,
    [Parameter(Mandatory = $true)][string]$UvImage,
    [string]$Tag = "csweet/software-development-polyglot-v1:local",
    [string]$OutputDirectory = "artifacts/software-development-polyglot-v1"
)

$ErrorActionPreference = "Stop"
$images = @($DotNetImage, $NodeImage, $PythonImage, $PowerShellImage, $UvImage)
foreach ($image in $images) {
    if ($image -notmatch '@sha256:[a-fA-F0-9]{64}$') {
        throw "Every base image must be pinned by a full sha256 digest: $image"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
docker build `
    --build-arg "DOTNET_IMAGE=$DotNetImage" `
    --build-arg "NODE_IMAGE=$NodeImage" `
    --build-arg "PYTHON_IMAGE=$PythonImage" `
    --build-arg "POWERSHELL_IMAGE=$PowerShellImage" `
    --build-arg "UV_IMAGE=$UvImage" `
    --tag $Tag `
    $PSScriptRoot
if ($LASTEXITCODE -ne 0) {
    throw "docker build failed with exit code $LASTEXITCODE."
}

$imageId = docker image inspect $Tag --format '{{.Id}}'
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
    throw "docker image inspect failed for '$Tag'."
}
$record = [ordered]@{
    profile = "software-development-polyglot-v1"
    tag = $Tag
    digest = $imageId
    builtAt = [DateTimeOffset]::UtcNow.ToString("O")
    baseImages = $images
}
$record | ConvertTo-Json -Depth 4 |
    Set-Content -Encoding utf8 (Join-Path $OutputDirectory "image-record.json")

if (Get-Command syft -ErrorAction SilentlyContinue) {
    syft $Tag -o "spdx-json=$(Join-Path $OutputDirectory 'sbom.spdx.json')"
} else {
    Write-Warning "syft was not found; the release pipeline must generate the required SBOM."
}
