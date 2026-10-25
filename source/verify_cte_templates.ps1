$ErrorActionPreference = 'Stop'

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$configManagerPath = Join-Path $sourceRoot 'Core\ConfigManager.cs'
$text = Get-Content -LiteralPath $configManagerPath -Raw -Encoding UTF8

$templateMatches = [regex]::Matches(
    $text,
    'private\s+static\s+string\s+(Create[^\s(]+TemplateJson)\s*\(\)\s*\{\s*return\s+"""(?<json>.*?)""";\s*\}',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)

if ($templateMatches.Count -lt 5) {
    throw "Expected at least 5 inline template JSON blocks, found $($templateMatches.Count)."
}

$failed = @()
foreach ($match in $templateMatches) {
    $name = $match.Groups[1].Value
    $json = $match.Groups['json'].Value.Trim()
    $jsonWithoutComments = (($json -split "`r?`n") | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    try {
        $null = $jsonWithoutComments | ConvertFrom-Json
    } catch {
        $failed += "${name}: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
}

if ($failed.Count -gt 0) {
    $failed | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "Validated $($templateMatches.Count) inline template JSON blocks."









