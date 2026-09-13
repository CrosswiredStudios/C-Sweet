function Write-ComputeSetupProgress {
    param([string] $Path, [string] $Message)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $json = @{ message = $Message; updatedAt = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
    $temporary = $Path + '.tmp'
    [IO.File]::WriteAllText($temporary, $json)
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
