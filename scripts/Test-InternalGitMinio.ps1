param([string]$Image = "minio/minio:latest")
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$container = "csweet-minio-test-" + [Guid]::NewGuid().ToString("N")
$testSecret = [Guid]::NewGuid().ToString("N")
$names = @("CSWEET_TEST_MINIO_ENDPOINT", "CSWEET_TEST_MINIO_ACCESS", "CSWEET_TEST_MINIO_SECRET")
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
Push-Location $repositoryRoot
try {
    # No host mounts or existing buckets. Use only an already-cached image and expose S3 on loopback.
    & docker run --detach --rm --pull never --name $container -p "127.0.0.1::9000" -e "MINIO_ROOT_USER=csweet-test" -e "MINIO_ROOT_PASSWORD=$testSecret" $Image server /data | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to start the disposable MinIO container." }
    $address = (& docker port $container 9000/tcp).Trim()
    if ($LASTEXITCODE -ne 0 -or $address -notmatch '^127\.0\.0\.1:[0-9]+$') { throw "Unexpected MinIO port binding." }
    $env:CSWEET_TEST_MINIO_ENDPOINT = "http://$address"
    $env:CSWEET_TEST_MINIO_ACCESS = "csweet-test"
    $env:CSWEET_TEST_MINIO_SECRET = $testSecret
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $response = Invoke-WebRequest "$env:CSWEET_TEST_MINIO_ENDPOINT/minio/health/ready" -TimeoutSec 2; if ($response.StatusCode -eq 200) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw "MinIO did not become ready." }
    & dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --no-restore --artifacts-path .artifacts/internal-git/verified --filter "FullyQualifiedName~InternalGitMinioIntegrationTests" --logger "console;verbosity=normal" --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "MinIO source-control integration tests failed." }
}
finally {
    & docker rm --force $container 2>$null | Out-Null
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name], "Process") }
    Pop-Location
}
