$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$launcher = Join-Path $repositoryRoot 'src/CSweet.Api/Setup/Start-CSweetDevelopmentOfficeSetup.ps1'
$officeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../CSweet.Office'))
$packagedHelper = Join-Path $officeRoot 'scripts/windows/Start-CSweetOfficeAutomaticUpdate.ps1'
if ((Get-FileHash -LiteralPath $launcher).Hash -ne (Get-FileHash -LiteralPath $packagedHelper).Hash) {
    throw 'Headquarters and the installed Office automatic-update helper have drifted.'
}
$binary = Join-Path $officeRoot 'src/CSweet.Office.Node/bin/Release/net10.0/CSweet.Office.Node.dll'
if (-not (Test-Path -LiteralPath $binary)) { throw 'Build Office in Release before running the preparation harness.' }
$binaryVersion = ([version](Get-Item -LiteralPath $binary).VersionInfo.FileVersion).ToString(3)
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('Get-OptionalObjectProperty', 'Assert-DevelopmentOfficePayload', 'Remove-TransientFile')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }.GetNewClosure(), $true)
    Invoke-Expression $definition.Extent.Text
}
# Execute the actual preparation branch with controlled I/O, never the elevated installer,
# service operations, enrollment cutover, or a hypervisor. The version guard reads a real built binary.
$branch = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($automaticUpdate) {') -and $node.Extent.Text.Contains('/api/offices/local-sessions/update-prepare') }, $true)
if ($null -eq $branch) { throw 'The automatic-update preparation branch was not found.' }
$body = [scriptblock]::Create($branch.Extent.Text)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$harnessRoot = Join-Path $artifactsRoot ('office-update-harness-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $harnessRoot -Force | Out-Null
$checks = 0
try {
    foreach ($scenario in @('ready', 'bootstrap-failed', 'wrong-version', 'cancelled')) {
        $caseRoot = Join-Path $harnessRoot $scenario
        $setupRoot = Join-Path $caseRoot 'setup'
        $officeScriptRoot = Join-Path $caseRoot 'office/scripts/windows'
        $global:OfficeUpdateHarnessPayloadRoot = Join-Path $caseRoot 'office/artifacts/windows-test/payload'
        New-Item -ItemType Directory -Path $setupRoot,$officeScriptRoot,$global:OfficeUpdateHarnessPayloadRoot -Force | Out-Null
        Copy-Item -LiteralPath $binary -Destination (Join-Path $global:OfficeUpdateHarnessPayloadRoot 'office.dll')
        @{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v16'; officeExecutable = 'office.dll' } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $global:OfficeUpdateHarnessPayloadRoot 'runtime-manifest.json')
        $OfficeBootstrapScript = Join-Path $officeScriptRoot 'prepare.ps1'
        @'
param([switch]$DevelopmentTools,[switch]$PublicInternetIpv4Only,[string]$ControlPlaneUserSid,
    [string]$ControlPlaneUrl,[string]$ProgressPath,[guid]$ProgressJobId,[switch]$NoElevation,
    [switch]$SkipInstall,[string]$PayloadResultPath)
$global:OfficeUpdateHarnessEvents.Add('prepared')
[IO.File]::WriteAllText($PayloadResultPath,$global:OfficeUpdateHarnessPayloadRoot)
$global:LASTEXITCODE = $(if ($global:OfficeUpdateHarnessScenario -eq 'bootstrap-failed') { 1 } else { 0 })
'@ | Set-Content -LiteralPath $OfficeBootstrapScript
        $global:OfficeUpdateHarnessScenario = $scenario
        $global:OfficeUpdateHarnessEvents = New-Object 'Collections.Generic.List[string]'
        $script:readyCalls = 0
        $automaticUpdate = $true
        $targetVersion = if ($scenario -eq 'wrong-version') { '99.99.99' } else { $binaryVersion }
        $handoffSecret = 'one-use-test-handoff'
        $origin = 'https://example.test'
        $architecture = 'x64'
        $sessionId = [guid]::NewGuid()
        $identity = [pscustomobject]@{ User = [pscustomobject]@{ Value = 'test-user' } }
        $release = $null; $prebuiltRoot = $null
        $progressPath = Join-Path $setupRoot 'progress.json'
        function Invoke-CSweetPinnedRestMethod {
            param($Method,$Uri,$Body)
            if ($Uri.EndsWith('/update-prepare')) {
                $global:OfficeUpdateHarnessEvents.Add('begin')
                return [pscustomobject]@{ succeeded = $true; targetVersion = $targetVersion; receipt = 'test-receipt'; controlPlaneUrl = 'https://example.test' }
            }
            if ($Uri.EndsWith('/update-progress')) {
                $script:readyCalls++
                $global:OfficeUpdateHarnessEvents.Add('request-stop')
                $request = $Body | ConvertFrom-Json
                if (-not $request.packagePrepared) { throw 'Stop was requested without prepared-package evidence.' }
                if ($scenario -eq 'cancelled') { return [pscustomobject]@{ succeeded = $false; state = 'cancelled' } }
                return [pscustomobject]@{ succeeded = $true; state = $(if ($script:readyCalls -lt 2) { 'stopping' } else { 'installing' });
                    launchUri = 'csweet-office://enroll/v1#handoff=fresh-test-handoff' }
            }
            throw 'Unexpected update endpoint.'
        }
        function Write-CSweetSetupProgress { param($Path,$JobId,$Workflow,$State,$PhaseKey,$PhaseDisplayName,$Message,$PercentComplete) }
        function Start-Sleep { param($Seconds) $global:OfficeUpdateHarnessEvents.Add('wait-for-confirmation') }
        $failure = $null
        try { & $body } catch { $failure = $_ }
        if ($scenario -eq 'ready') {
            if ($failure) { throw $failure }
            if ($script:readyCalls -ne 2 -or $global:OfficeUpdateHarnessEvents.IndexOf('prepared') -gt $global:OfficeUpdateHarnessEvents.IndexOf('request-stop') -or
                -not $global:OfficeUpdateHarnessEvents.Contains('wait-for-confirmation')) { throw 'Preparation/stop ordering was violated.' }
            # Evaluate the actual request construction as well: the development-source
            # path must report the verified target rather than the legacy setup version.
            $existingInstallationState = 'clean'
            foreach ($requestName in @('$preflightRequest', '$request')) {
                $requestAssignment = $ast.Find({ param($node)
                    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left.Extent.Text -ceq $requestName -and $node.Extent.Text.Contains('officeVersion =')
                }.GetNewClosure(), $true)
                if ($null -eq $requestAssignment) { throw 'The installer request construction was not found.' }
                Invoke-Expression $requestAssignment.Extent.Text
                $constructed = if ($requestName -ceq '$request') { $request | ConvertFrom-Json } else { $preflightRequest | ConvertFrom-Json }
                if ($constructed.officeVersion -cne $targetVersion) { throw 'The installer request did not retain the verified target version.' }
            }
        } else {
            if ($null -eq $failure) { throw "Scenario $scenario should refuse installation." }
            if ($scenario -ne 'cancelled' -and $script:readyCalls -ne 0) { throw 'A failed or wrong-version package stopped jobs.' }
        }
        $checks++
    }
    Write-Output "Passed $checks real automatic-update preparation scenarios and packaged-helper parity."
}
finally {
    Remove-Variable -Name OfficeUpdateHarnessPayloadRoot,OfficeUpdateHarnessEvents,OfficeUpdateHarnessScenario -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($harnessRoot)
    if (-not $resolved.StartsWith($artifactsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('office-update-harness-')) { throw 'Unsafe harness cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
