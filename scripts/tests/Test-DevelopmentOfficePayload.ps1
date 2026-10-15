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
foreach ($suite in @('csweet-hardware-vm-smoke-v15', 'csweet-hardware-vm-smoke-v15-ipv4')) {
    Assert-DevelopmentOfficePayload ([pscustomobject]@{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = $suite })
}
foreach ($payload in @(
    [pscustomobject]@{ officeVersion = '0.9.0'; brokerProtocolVersion = '1.0'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v14' },
    [pscustomobject]@{ brokerProtocolVersion = '1.0'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v15' },
    [pscustomobject]@{ brokerProtocolVersion = '1.1'; certificationSuiteVersion = 'csweet-hardware-vm-smoke-v14' },
    [pscustomobject]@{}
)) {
    $rejected = $false
    try { Assert-DevelopmentOfficePayload $payload } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid development payload was admitted.' }
}
Write-Output 'Installer syntax and six development payload cases passed.'
