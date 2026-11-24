$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../../src/CSweet.Api/Setup/Start-CSweetDevelopmentOfficeSetup.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Execute just the real guard and its read helper; never run enrollment or host setup.
foreach ($name in @('Get-OptionalObjectProperty', 'Assert-DevelopmentOfficePayload')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }.GetNewClosure(), $true)
    Invoke-Expression $definition.Extent.Text
}
foreach ($suite in @('csweet-hardware-vm-smoke-v16', 'csweet-hardware-vm-smoke-v16-ipv4')) {
    Assert-DevelopmentOfficePayload ([pscustomobject]@{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = $suite })
}
foreach ($payload in @(
    [pscustomobject]@{ officeVersion = '0.9.0'; brokerProtocolVersion = '1.0'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v14' },
    [pscustomobject]@{ brokerProtocolVersion = '1.0'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v16' },
    [pscustomobject]@{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v15' },
    [pscustomobject]@{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v14' },
    [pscustomobject]@{}
)) {
    $rejected = $false
    try { Assert-DevelopmentOfficePayload $payload } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid development payload was admitted.' }
}
Write-Output 'Installer syntax and seven development payload cases passed.'

# Exercise the launcher's actual compute-plan invocation with the two distinct gateway origins used by AppHost.
foreach ($name in @('Invoke-CSweetPinnedRestMethod', 'Request-CSweetOfficeComputePlan')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }.GetNewClosure(), $true)
    Invoke-Expression $definition.Extent.Text
}
$assignment = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$plan'
}, $true)
$origin = 'http://localhost:47083'
$originUri = [uri]$origin
$redemption = [pscustomobject]@{ controlPlaneUrl = 'https://localhost:47082' }
$controlPlaneCertificateSha256 = 'a' * 64
$computeRequest = '{"setupReceipt":"fixture"}'
$script:requests = 0
function Invoke-RestMethod {
    param($Method, [uri]$Uri, $ContentType, $Body, $TimeoutSec, [switch]$UseBasicParsing, $MaximumRedirection)
    $script:requests++
    if ($Uri.AbsoluteUri -cne 'https://localhost:47082/api/offices/local-sessions/compute-plan' -or
        $Method -ne 'Post' -or $Body -cne $computeRequest -or $MaximumRedirection -ne 0 -or
        $expectedCertificateSha256 -cne $controlPlaneCertificateSha256 -or
        $null -eq [Net.ServicePointManager]::ServerCertificateValidationCallback) { throw 'Unexpected compute reservation request.' }
    [pscustomobject]@{ enabled = $true }
}
Invoke-Expression $assignment.Extent.Text
if (-not $plan.enabled -or $script:requests -ne 1) { throw 'The authorized compute plan was lost.' }
$redemption.controlPlaneUrl = $origin
$rejected = $false
try { Invoke-Expression $assignment.Extent.Text } catch { $rejected = $true }
if (-not $rejected -or $script:requests -ne 1) { throw 'The launcher attempted an insecure compute reservation.' }
Write-Output 'HTTPS compute reservation and plaintext rejection passed.'
