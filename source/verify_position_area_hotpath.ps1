$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

if ($positionText -match 'IsPositionInsideAreaByReflection\(item\.Area,\s*agent\.Position,\s*allowExpandedBounds:\s*true\)') {
    $errors += 'OverrideArea runtime hot path must not use expanded bounds; it can scan Renderer/Collider hierarchies and cause stutter.'
}

if ($positionText -notmatch 'TryGetSampledCourseNodeArea\(agent\.Position,\s*trigger,\s*out object sampledArea\)') {
    $errors += 'OverrideArea should sample the game CourseNode from the player position and compare sampled node.m_area.'
}

if ($positionText -notmatch 'TryGetStrictAreaBoundsContains\(item\.Area,\s*agent\.Position\)') {
    $errors += 'OverrideArea should keep only a strict bounds fallback after CourseNode sampling fails.'
}

if ($positionText -match 'allowTransformFallback:\s*true\)\s*&&\s*BoundsContainsXZ') {
    $errors += 'Runtime area containment must not use transform/hierarchy fallback bounds.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position OverrideArea hot-path checks passed.'





