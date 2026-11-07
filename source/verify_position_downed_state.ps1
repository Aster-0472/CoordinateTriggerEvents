$ErrorActionPreference = 'Stop'

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$positionPath = Join-Path $sourceRoot 'PositionTriggers\PositionTriggerManager.cs'
$text = Get-Content -LiteralPath $positionPath -Raw -Encoding UTF8

$errors = @()

if ($text -notmatch 'EnumeratePlayersForAreaState') {
    $errors += 'PositionTriggers must have an area-state player enumerator that does not filter downed players.'
}

if ($text -notmatch 'EnumeratePlayersForEventEligibility') {
    $errors += 'PositionTriggers must keep a separate event-eligibility player enumerator for RequireAlivePlayers.'
}

if ($text -notmatch 'foreach\s*\(\s*PlayerAgent\s+agent\s+in\s+EnumeratePlayersForAreaState\(trigger\)\s*\)') {
    $errors += 'CountPlayersInsideTriggerArea must use EnumeratePlayersForAreaState(trigger).'
}

$areaStateMethod = [regex]::Match(
    $text,
    'private\s+static\s+IEnumerable<PlayerAgent>\s+EnumeratePlayersForAreaState\s*\([^)]*\)\s*\{(?<body>.*?)\n\s*\}',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)

if ($areaStateMethod.Success -and $areaStateMethod.Groups['body'].Value -match 'RequireAlivePlayers|!\s*agent\.Alive') {
    $errors += 'EnumeratePlayersForAreaState must not check RequireAlivePlayers or agent.Alive.'
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'Position trigger downed-player area-state checks passed.'









