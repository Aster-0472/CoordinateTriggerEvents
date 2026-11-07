$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$text = Get-Content (Join-Path $root 'PositionTriggers/PositionTriggerManager.cs') -Raw -Encoding UTF8
if (-not $text.Contains('Enumerable.Range(1, insidePlayerCount).SelectMany(playerCount => GetEventsForPlayerCount(trigger, playerCount))')) { throw 'Cumulative player slots missing' }
if (-not $text.Contains(': GetEventsForPlayerCount(trigger, insidePlayerCount)')) { throw 'Exact-count fallback missing' }
Write-Output 'Position continuous player-count checks passed.'
