$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

foreach ($pattern in @(
    'private sealed class CourseNodeDimensionMap',
    'AIG_NodeCluster\.AllNodeClusters',
    'TryRefreshCourseNodePositionMap',
    'ResolveCourseNodeFromPositionMap',
    'TryGetClosestNodeInCluster',
    'TryAreaInstanceIdMatchesCachedArea',
    'GetAreaInstanceId',
    'gameObject\.GetInstanceID\(\)'
)) {
    if ($positionText -notmatch $pattern) { $errors += "Missing ARA/LEGACY-style area lookup pattern: $pattern" }
}

if ($positionText -match 'return AreaMatchesCachedArea\(sampledArea,\s*areas\);') {
    $errors += 'OverrideArea must not rely only on ReferenceEquals/Equals for sampled LG_Area comparison.'
}

if ($positionText -match 'IsPositionInsideAreaByReflection\(item\.Area,\s*agent\.Position,\s*allowExpandedBounds:\s*true\)') {
    $errors += 'OverrideArea must not reintroduce expanded bounds in the runtime hot path.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position OverrideArea CourseNode map checks passed.'




