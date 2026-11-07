$sourceRoot = $PSScriptRoot
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$positionText = Get-Content -LiteralPath $positionPath -Raw
$errors = @()

if ($positionText -notmatch 'TryGetSampledCourseNodeArea\(agent\.Position,\s*trigger,\s*out object sampledArea\)') {
    $errors += 'OverrideArea player checks must sample CourseNode from the player position.'
}

if ($positionText -notmatch 'TryGetStrictAreaBoundsContains\(item\.Area,\s*agent\.Position\)') {
    $errors += 'OverrideArea should keep strict area bounds only as a fallback.'
}

if ($positionText -notmatch 'TryGetAreaBounds\(area,\s*out Bounds strictBounds,\s*allowTransformFallback:\s*false\)') {
    $errors += 'OverrideArea fallback hit checks must request strict area bounds with allowTransformFallback=false.'
}

if ($positionText -match 'IsPositionInsideAreaByReflection\(item\.Area,\s*agent\.Position,\s*allowExpandedBounds:\s*true\)') {
    $errors += 'OverrideArea precision check must not use expanded bounds.'
}

if ($positionText -match 'BoundsContainsXZ\(bounds,\s*position,\s*0\.10f\)') {
    $errors += 'OverrideArea precision check must not expand boundaries with the old 0.10f margin.'
}

if ($positionText -match 'OrderByDescending\(BoundsXZArea\)') {
    $errors += 'OverrideArea must not choose the largest candidate bounds; that can overlap neighboring areas at doors.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position OverrideArea precision checks passed.'





