$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$configPath = Join-Path $sourceRoot 'Core\ConfigManager.cs'
$modelPath = Join-Path $sourceRoot 'Models\ConfigModels.cs'

$positionText = Get-Content -LiteralPath $positionPath -Raw
$configText = Get-Content -LiteralPath $configPath -Raw
$modelText = Get-Content -LiteralPath $modelPath -Raw

$errors = @()

foreach ($pattern in @(
    '"outside"\s*=>\s*"anyplayeroutside"',
    '"anyplayeroutside"\s*=>\s*"anyplayeroutside"',
    '"alloutside"\s*=>\s*"allplayersoutside"',
    '"allplayersoutside"\s*=>\s*"allplayersoutside"'
)) {
    if ($positionText -notmatch $pattern) { $errors += "Missing Position TriggerMode normalization pattern: $pattern" }
}

foreach ($pattern in @(
    'case\s+"anyplayeroutside"',
    'case\s+"allplayersoutside"',
    'HasEverHadPlayersInside',
    'if \(!state\.Initialized\)',
    'state\.Initialized\s*&&\s*state\.HasEverHadPlayersInside'
)) {
    if ($positionText -notmatch $pattern -and $modelText -notmatch $pattern) { $errors += "Missing Position outside state/evaluation pattern: $pattern" }
}

if ($positionText -match 'state\.HasEverHadPlayersInside\s*=\s*state\.HasEverHadPlayersInside\s*\|\|\s*eligibleInsidePlayerCount\s*>\s*0') {
    $errors += 'Outside modes must not arm HasEverHadPlayersInside before the initial no-fire sample gate.'
}

if ($configText -notmatch 'anyplayeroutside"\s*\|\|\s*mode\s*==\s*"allplayersoutside"') {
    $errors += 'Position outside modes must be treated as continuous modes for cooldown validation.'
}

foreach ($pattern in @(
    'AnyPlayerOutside',
    'AllPlayersOutside',
    'continues while outside'
)) {
    if ($configText -notmatch [regex]::Escape($pattern)) { $errors += "Missing template documentation pattern: $pattern" }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position outside TriggerMode checks passed.'





