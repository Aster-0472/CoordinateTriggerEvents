$sourceRoot = $PSScriptRoot
$errors = @()

$sourceText = (Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.csproj', '.rsp' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

$scanPath = Join-Path $sourceRoot 'ScanTriggers\ScanTriggerManager.cs'
$runtimePath = Join-Path $sourceRoot 'Core\Runtime.cs'
$configPath = Join-Path $sourceRoot 'Core\ConfigManager.cs'
$modelPath = Join-Path $sourceRoot 'Models\ConfigModels.cs'
$scanText = Get-Content -LiteralPath $scanPath -Raw
$runtimeText = Get-Content -LiteralPath $runtimePath -Raw
$configText = Get-Content -LiteralPath $configPath -Raw
$modelText = Get-Content -LiteralPath $modelPath -Raw

foreach ($forbidden in @(
    'OnAllPlayersExitedScan',
    'onallplayersexitedscan',
    'AllPlayersExitedRepeatActive',
    'LastAllPlayersExitedCount',
    'TryArmAllPlayersExitedRepeat',
    'allplayersexited-repeat'
)) {
    if ($sourceText -match [regex]::Escape($forbidden)) {
        $errors += "Removed OnAllPlayersExitedScan feature still has source reference: $forbidden"
    }
}

foreach ($required in @(
    'OnScanActivated',
    'OnPlayerExitScan',
    'OnAllPlayersEnterScan',
    'OnAllPlayersInsideScan',
    'OnAllPlayersExitScan',
    'onallplayersexitscan',
    'onallplayersinsidescan',
    'AllPlayersEnteredThisCycle',
    'AllPlayersInsideNow'
)) {
    if ($sourceText -notmatch [regex]::Escape($required)) {
        $errors += "Existing scan trigger feature was accidentally removed: $required"
    }
}

if ($scanText -notmatch 'bool hasAllInsideRepeat = false') {
    $errors += 'OnAllPlayersInsideScan repeat detection must remain.'
}
if ($scanText -notmatch 'Runtime\.FireScanTrigger\(config,\s*trigger,\s*"OnAllPlayersExitScan"') {
    $errors += 'OnAllPlayersExitScan fire path must remain.'
}
if ($runtimeText -match 'alloutside|allexited|allplayersexited|allplayersoutside') {
    $errors += 'Runtime trigger aliases for removed OnAllPlayersExitedScan must be deleted.'
}
if ($configText -match 'OnAllPlayersExitedScan|Inside/Exited|Inside/Exited repeat') {
    $errors += 'Default config templates must not mention removed OnAllPlayersExitedScan.'
}
if ($modelText -match 'AllPlayersExitedRepeatActive|LastAllPlayersExitedCount') {
    $errors += 'Scan runtime state must not keep removed Exited repeat fields.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Scan OnAllPlayersExitedScan removal checks passed without deleting other scan trigger modes.'


