$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

foreach ($pattern in @(
    'TryGetSampledCourseNodeArea\(agent\.Position,\s*trigger,\s*out object sampledArea\)',
    'return TryAreaInstanceIdMatchesCachedArea\(sampledArea,\s*areas\);',
    'TryGetStrictAreaBoundsContains\(item\.Area,\s*agent\.Position\)',
    'AIG_GeomorphNodeVolume\.TryGetCourseNode',
    'AIG_CourseNode\.TryGetCourseNode'
)) {
    if ($positionText -notmatch $pattern) { $errors += "Missing sampled OverrideArea pattern: $pattern" }
}

if ($positionText -match 'IsPositionInsideAreaByReflection\(item\.Area,\s*agent\.Position,\s*allowExpandedBounds:\s*true\)') {
    $errors += 'OverrideArea runtime hot path must not use expanded bounds.'
}

if ($positionText -match 'TryGetPlayerCourseNode\(agent,\s*out object playerNode\)\s*&&\s*!PlayerCourseNodeMatchesCachedArea\(playerNode,\s*areas\)\)\s*\{\s*return false;\s*\}') {
    $errors += 'OverrideArea must not reject entry only because the cached player CourseNode does not match the cached Area.'
}

if ($positionText -match 'OrderByDescending\(BoundsXZArea\)') {
    $errors += 'OverrideArea must not reintroduce largest-candidate bounds selection.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position OverrideArea sampled gate checks passed.'





