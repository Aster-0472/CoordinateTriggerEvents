$ErrorActionPreference = 'Stop'

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$scanPath = Join-Path $sourceRoot 'ScanTriggers\ScanTriggerManager.cs'
$modelsPath = Join-Path $sourceRoot 'Models\ConfigModels.cs'
$scanText = Get-Content -LiteralPath $scanPath -Raw -Encoding UTF8
$modelText = Get-Content -LiteralPath $modelsPath -Raw -Encoding UTF8

$errors = @()

if ($modelText -notmatch 'ScanAreaStatePlayerSlots') {
    $errors += 'ScanRuntimeState must keep ScanAreaStatePlayerSlots so downed players can remain counted inside a scan.'
}

if ($scanText -notmatch 'UpdateScanAreaStatePlayers') {
    $errors += 'ScanTriggers must update an internal scan area-state player set.'
}

if ($scanText -notmatch 'OnScanPlayersChanged\s*\([^)]*HashSet<int>\?\s+currentSlots') {
    $errors += 'OnScanPlayersChanged must accept current player slots from the live scan callback.'
}

if ($scanText -notmatch 'GetPlayerSlotsOnScan') {
    $errors += 'ScanTriggers must read concrete PlayersOnScan slots instead of relying only on raw player count.'
}

if ($scanText -notmatch 'GetPlayerSlotsOnScan\s*\(\s*playersInScan\s*\)') {
    $errors += 'Harmony scan callbacks must pass live playersInScan slots into ScanTriggers.'
}

if ($scanText -notmatch 'OnScanPlayersChanged\s*\(\s*__instance\s*,\s*count\s*,\s*"Master_OnPlayerScanChangedCheckProgress"\s*,\s*slots\s*\)') {
    $errors += 'Master_OnPlayerScanChangedCheckProgress must pass live slots, not only count.'
}

if ($scanText -notmatch 'OnScanPlayersChanged\s*\(\s*__instance\s*,\s*count\s*,\s*\$"OnSyncStateChange:\{status\}"\s*,\s*slots\s*\)') {
    $errors += 'OnSyncStateChange must pass live slots, not only count.'
}

if ($scanText -notmatch 'agent\s*!=\s*null\s*&&\s*!\s*agent\.Alive') {
    $errors += 'ScanTriggers must preserve missing non-alive players in the scan area-state set.'
}

if ($scanText -notmatch 'state\.ScanAreaStatePlayerSlots\.Clear\(\)') {
    $errors += 'ResetScanCycle must clear ScanAreaStatePlayerSlots.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Scan trigger downed-player area-state checks passed.'








