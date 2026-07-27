param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryUrl,
    [string]$BaseUrl = "http://localhost:8080",
    [string]$BusinessId = "e2e-agent-runtime",
    [int]$TickFrequencySeconds = 300,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd('/')

function Invoke-CSweetJson {
    param([string]$Method, [string]$Path, $Body = $null)
    $parameters = @{ Method = $Method; Uri = "$base$Path" }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 12
    }
    Invoke-RestMethod @parameters
}

Write-Host "[1/8] Checking API and runtime settings"
Invoke-CSweetJson GET "/api/health" | Out-Null
$settings = Invoke-CSweetJson GET "/api/agent-runtime/settings"
if (-not $settings.enableImportedAgents) { throw "Imported agents are disabled in global runtime settings." }

Write-Host "[2/8] Previewing immutable GitHub import"
$preview = Invoke-CSweetJson POST "/api/agents/imports/preview" @{ repositoryUrl = $RepositoryUrl }
if (-not $preview.importId) { throw "Import preview did not return an import ID." }

Write-Host "[3/8] Approving manifest-bounded grants and schedule"
$install = Invoke-CSweetJson POST "/api/agents/imports/$($preview.importId)/install" @{
    businessId = $BusinessId
    activationMode = "Manual"
    tickFrequencySeconds = [Math]::Max($TickFrequencySeconds, $settings.minimumTickFrequencySeconds)
    overlapPolicy = "Skip"
    grantedCapabilities = @($preview.capabilities)
    grantedSubscriptions = @($preview.requestedSubscriptions)
    grantedPublications = @($preview.requestedPublications)
    grantedPermissions = @($preview.requestedPermissions)
    grantedNetworkAccess = @($preview.requestedNetworkAccess)
    maxRuntimeSeconds = $settings.defaultMaxRuntimeSeconds
    memoryMb = $settings.defaultContainerMemoryMb
    cpuPercent = $settings.defaultContainerCpuPercent
}

Write-Host "[4/8] Waiting for isolated build"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 5
    $installation = Invoke-CSweetJson GET "/api/agents/installations/$($install.id)"
    $buildStatus = $installation.build.status
    Write-Host "  build: $buildStatus"
    if ($buildStatus -in @("Failed", "Cancelled")) {
        $log = Invoke-CSweetJson GET "/api/agents/installations/$($install.id)/build-log"
        throw "Build failed: $($log.content)"
    }
} until ($buildStatus -eq "Succeeded" -or (Get-Date) -ge $deadline)
if ($buildStatus -ne "Succeeded") { throw "Build did not complete before timeout." }

Write-Host "[5/8] Requesting an immediate schedule tick"
Invoke-CSweetJson POST "/api/agents/installations/$($install.id)/run-now" | Out-Null

Write-Host "[6/8] Waiting for runtime registration and completion"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$terminal = @("Completed", "StartFailed", "McpSessionTimedOut", "RuntimeTimedOut", "ExitedWithoutCompletion", "Failed", "Cancelled", "PolicyDenied")
do {
    Start-Sleep -Seconds 5
    $runs = @(Invoke-CSweetJson GET "/api/agents/installations/$($install.id)/runs")
    $latest = $runs | Select-Object -First 1
    $runtimeStatus = $latest.status
    Write-Host "  runtime: $runtimeStatus"
} until (($runtimeStatus -in $terminal) -or (Get-Date) -ge $deadline)
if ($runtimeStatus -ne "Completed") {
    throw "Runtime ended as '$runtimeStatus': $($latest.reason)`n$($latest.logExcerpt)"
}

Write-Host "[7/8] Verifying next-run and retained runtime history"
$installation = Invoke-CSweetJson GET "/api/agents/installations/$($install.id)"
if (-not $installation.latestRuntime -or $installation.latestRuntime.status -ne "Completed") {
    throw "Completed runtime was not exposed by installation management API."
}

Write-Host "[8/8] Disabling the QA installation"
$disabled = Invoke-CSweetJson POST "/api/agents/installations/$($install.id)/disable"
if ($disabled.isEnabled) { throw "QA installation did not disable successfully." }

Write-Host "Agent runtime end-to-end QA passed for $($preview.agentId) at commit $($preview.commitSha)." -ForegroundColor Green
