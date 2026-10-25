$sourceRoot = $PSScriptRoot
$modelPath = Join-Path $sourceRoot 'Models\ConfigModels.cs'
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$modelText = Get-Content -LiteralPath $modelPath -Raw
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

if ($modelText -notmatch 'public bool Initialized;') {
    $errors += 'TriggerState must track whether PositionTrigger state has completed its initial sample.'
}

if ($positionText -notmatch 'if \(!state\.Initialized\)') {
    $errors += 'EvaluateTrigger must sample initial PositionTrigger state without firing Events.'
}

if ($positionText -notmatch 'UpdatePositionTriggerState\(state,\s*inside,\s*insidePlayerCount,\s*eligibleInsidePlayerCount\);') {
    $errors += 'EvaluateTrigger should update initial state through a shared helper.'
}

if ($positionText -notmatch 'state\.Initialized\s*&&\s*state\.HasEverHadPlayersInside') {
    $errors += 'AnyPlayerOutside/AllPlayersOutside must require initialized state before firing.'
}

if ($positionText -match 'state\.HasEverHadPlayersInside\s*=\s*state\.HasEverHadPlayersInside\s*\|\|\s*eligibleInsidePlayerCount\s*>\s*0;') {
    $errors += 'HasEverHadPlayersInside must not be set before the initial no-fire sample gate.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position outside initialization checks passed.'





