$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

if ($positionText -match 'TryGetPlayerCourseNode\(agent,\s*out object playerNode\)\s*&&\s*!PlayerCourseNodeMatchesCachedArea\(playerNode,\s*areas\)\)\s*\{\s*return false;\s*\}') {
    $errors += 'OverrideArea must not reject entry only because the cached player CourseNode does not match the cached Area.'
}

if ($positionText -notmatch 'TryGetSampledCourseNodeArea\(agent\.Position,\s*trigger,\s*out object sampledArea\)') {
    $errors += 'OverrideArea should sample the game CourseNode from the current player position.'
}

if ($positionText -notmatch 'return TryAreaInstanceIdMatchesCachedArea\(sampledArea,\s*areas\);') {
    $errors += 'OverrideArea should compare sampled node.m_area to the configured Area by gameObject InstanceID.'
}

if ($positionText -notmatch 'TryGetStrictAreaBoundsContains\(item\.Area,\s*agent\.Position\)') {
    $errors += 'OverrideArea still needs a strict position fallback when CourseNode sampling fails.'
}

if ($positionText -match 'IsPositionInsideAreaByReflection\(item\.Area,\s*agent\.Position,\s*allowExpandedBounds:\s*true\)') {
    $errors += 'OverrideArea must not use expanded bounds for entry detection.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position OverrideArea entry gate checks passed.'





