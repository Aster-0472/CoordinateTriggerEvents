$csproj = Join-Path $PSScriptRoot 'CoordinateTriggerEvents.csproj'
$rsp = Join-Path $PSScriptRoot 'csc.offline.rsp'

$sourceFiles = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Filter '*.cs' | Sort-Object FullName
$sourceText = ($sourceFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$projectText = Get-Content -LiteralPath $csproj -Raw
$rspText = Get-Content -LiteralPath $rsp -Raw

$errors = @()

if (Test-Path (Join-Path $PSScriptRoot 'Plugin.cs')) { $errors += 'Root Plugin.cs should be split into feature folders.' }
if ($sourceText -notmatch 'public const string VERSION = "1\.7\.0"') { $errors += 'PluginInfo.VERSION must be 1.7.0.' }
foreach ($pattern in @('<Version>1.7.0</Version>', '<AssemblyVersion>1.7.0.0</AssemblyVersion>', '<FileVersion>1.7.0.0</FileVersion>', '<InformationalVersion>1.7.0</InformationalVersion>')) {
    if ($projectText -notlike "*$pattern*") { $errors += "Missing csproj version pattern: $pattern" }
}
if (-not $rspText.Contains($PSScriptRoot)) { $errors += 'csc.offline.rsp must use this source workspace path.' }
if ($rspText -notlike '*CoordinateTriggerEvents.dll*') { $errors += 'csc.offline.rsp must output CoordinateTriggerEvents.dll.' }
foreach ($relative in @(
    'Core\ConfigManager.cs',
    'Core\Plugin.cs',
    'Core\Runtime.cs',
    'Debug\DebugMarkerManager.cs',
    'InteractTriggers\InteractTriggerManager.cs',
    'InteractTriggers\SpoIndexResolver.cs',
    'Models\ConfigModels.cs',
    'Models\NetworkPackets.cs',
    'PositionTriggers\PositionTriggerManager.cs',
    'ScanTriggers\ScanTriggerManager.cs',
    'HudInteractTriggers\HudInteractTriggerManager.cs',
    'WardenEvents\TriggerControlEvent.cs'
)) {
    $full = Join-Path $PSScriptRoot $relative
    if (-not (Test-Path $full)) { $errors += "Missing split source file: $relative" }
    if ($rspText -notlike "*$full*") { $errors += "csc.offline.rsp must compile split source file: $relative" }
}
$removedTerminalTriggerPatterns = @(
    ('Cte' + 'TerminalTriggerPacket'),
    ('terminal' + '_trigger_event.v1'),
    ('OnReceive' + 'TerminalTriggerSync'),
    ('OnSynced' + 'TerminalTrigger'),
    ('Broadcast' + 'TerminalTrigger'),
    ('On' + 'TerminalUse'),
    ('On' + 'TerminalUnused'),
    ('On' + 'TerminalUsing'),
    ('On' + 'TerminalExited'),
    ('Interact_' + 'ComputerTerminal'),
    ('Interact_' + 'Terminal'),
    ('LG_' + 'ComputerTerminal'),
    ('Terminal' + 'Selector'),
    ('Terminal' + 'Serial')
)
foreach ($pattern in $removedTerminalTriggerPatterns) {
    if ($sourceText -match [regex]::Escape($pattern)) { $errors += "Forbidden terminal trigger remnant found: $pattern" }
}
foreach ($pattern in @(
    'PositionTriggers',
    'ScanTriggers',
    'InteractTriggers", "BigPickup',
    'HudInteractTriggers',
    'CreateChinesePositionTemplateJson',
    'CreateChineseScanTemplateJson',
    'CreateChineseBigPickupTemplateJson',
    'CreateChineseHudInteractTemplateJson'
)) {
    if ($sourceText -notmatch [regex]::Escape($pattern)) { $errors += "Missing split template generation pattern: $pattern" }
}
if ($sourceText -match 'RegisterFreeSizedEvent|InvokeFreeSizedEvent|hud_sync|client_hud_sync') { $errors += 'Forbidden legacy free-sized HUD sync remnants found.' }
if ($sourceText -match 'return ExecuteClientDisplayEventGroup\(') { $errors += 'Clients must not enter old local ClientDisplayReplay group path.' }
if ($sourceText -match 'return TryExecuteSyncedClientDisplayEvent\(') { $errors += 'Clients must not use old local synced display fallback entry.' }
if ($sourceText -notmatch 'TriggerControlEventType\s*=\s*700') { $errors += 'Trigger control custom event type must be 700.' }
if ($sourceText -match 'settriggerenabled|seteventtriggerenabled|settriggerstate|enabletrigger|disabletrigger') { $errors += 'Legacy string trigger-control event names must be removed.' }
if ($sourceText -notmatch '"Type"\s*:\s*700') { $errors += 'Generated templates must include Type 700 trigger-control examples.' }
if ($sourceText -notmatch 'HudInteractTriggerRule') { $errors += 'HudInteractTriggerRule model is required.' }
if ($sourceText -notmatch 'HudInteractTriggerManager\.Update') { $errors += 'Runtime must update HudInteractTriggerManager.' }
if ($sourceText -notmatch 'HudInteractTriggerRequestPacket') { $errors += 'HUD interact NetworkAPI request packet is required.' }
if ($sourceText -notmatch 'HudInteractTriggerConfirmPacket') { $errors += 'HUD interact NetworkAPI confirm packet is required.' }
if ($sourceText -notmatch 'Interact_Timed') { $errors += 'HUD interact must use Interact_Timed.' }
if ($sourceText -notmatch 'OnInteractionTriggered') { $errors += 'HUD interact must trigger from timed interaction completion.' }
if ($sourceText -notmatch 'TimerUpdated') { $errors += 'HUD interact progress events must use native Interact_Timed.TimerUpdated callback.' }
if ($sourceText -notmatch 'OnInteractorStateChanged') { $errors += 'HUD interact must use native interactor state changes to detect E release cancel.' }
if ($sourceText -notmatch 'OnInteractorCompleted') { $errors += 'HUD interact must use native interactor completion as a reliable completion fallback.' }
if ($sourceText -notmatch '_completionRequested') { $errors += 'HUD interact must guard completion fallback against duplicate Events.' }
if ($sourceText -match 'HostInteractionReachMargin') { $errors += 'HUD interact host validation must not use Radius or reach margin; native interaction result is authoritative.' }
if ($sourceText -match 'Vector3\.Distance\(player\.Position,\s*trigger\.Position\.ToVector3\(\)\)') { $errors += 'HUD interact host validation must not reject completed native interactions by player distance.' }
if ($sourceText -notmatch 'PendingCancelDelaySeconds') { $errors += 'HUD interact cancel must be deferred briefly so normal completion does not also fire CancelEvents.' }
if ($sourceText -notmatch 'NormalizeProgressThreshold') { $errors += 'HUD interact ProgressEvents must normalize 0-1 and 0-100 progress thresholds.' }
$hudInteractText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'HudInteractTriggers\HudInteractTriggerManager.cs') -Raw
if ($sourceText -match 'CustomInteractAPI') { $errors += 'HUD interact must not depend on CustomInteractAPI.' }
if ($hudInteractText -notmatch 'Interact_Timed interact = go\.AddComponent<Interact_Timed>\(\)') { $errors += 'HUD interact must use the game-native Interact_Timed component.' }
if ($hudInteractText -notmatch 'm_colliderToOwn\s*=\s*collider') { $errors += 'HUD interact must bind the native owned collider.' }
if ($hudInteractText -notmatch 'LayerMask\.NameToLayer\("Interaction"\)' -or $hudInteractText -notmatch '\?\s*layer\s*:\s*14') { $errors += 'HUD interact must use the native Interaction layer with numeric fallback 14.' }
if ($hudInteractText -match 'OnAdditionalInteractionText|HoldText') { $errors += 'HUD interact manager must not depend on HoldText; HudText is the single prompt text.' }
if ($hudInteractText -notmatch 'ResolveHudText' -or $hudInteractText -notmatch 'Runtime\.TryResolvePartialDataPersistentId' -or $hudInteractText -notmatch 'Text\.Get') { $errors += 'HUD interact HudText must resolve numeric and MTFO PartialData text IDs before assigning InteractionMessage.' }
if ($sourceText -notmatch 'DebugVisible') { $errors += 'HudInteractTriggers must expose DebugVisible.' }
if ($sourceText -notmatch 'DebugColor') { $errors += 'HudInteractTriggers must expose DebugColor.' }
if ($sourceText -notmatch 'DebugLabel') { $errors += 'HudInteractTriggers must expose DebugLabel.' }
if ($hudInteractText -match 'GuiManager\.InteractionLayer|Input\.GetKey|SetMessageTimer|MessageTimerVisible|InteractPromptVisible\s*=\s*false') {
    $errors += 'HUD interact manager must not directly drive or hide the global InteractionLayer.'
}
if ($sourceText -notmatch '"UseTriggerArea"\s*:\s*true') { $errors += 'BigPickup templates must expose UseTriggerArea.' }
if ($sourceText -match '"TriggerArea"\s*:\s*\{[^}]*"TriggerAreaMode"') { $errors += 'BigPickup TriggerArea template must not include TriggerAreaMode.' }
if ($sourceText -notmatch '"Count"\s*:\s*-1') { $errors += 'BigPickup templates must document Count=-1 big-zone behavior.' }
$jsoncWhitespace = '(?:(?:\s*//[^\r\n]*\r?\n)|\s)*'
if ($sourceText -notmatch ('"ProgressEvents"\s*:\s*\{' + $jsoncWhitespace + '"Progress"\s*:\s*-1,' + $jsoncWhitespace + '"Events"\s*:\s*\[\s*\]' + $jsoncWhitespace + '\}')) { $errors += 'HudInteract template must use SPO-style ProgressEvents object.' }
if ($sourceText -notmatch 'HudText.*<color=#00BFFF>') { $errors += 'HudInteract template must document HudText color tags.' }
if ($sourceText -notmatch '//') { $errors += 'Generated default config templates must include comments.' }
if ($sourceText -notmatch 'JsonCommentHandling\.Skip') { $errors += 'Template rewrite logic must validate JSONC with comments enabled.' }
if ($sourceText -notmatch 'Replacing invalid or outdated template config') { $errors += 'Existing invalid or outdated template files must be regenerated.' }
$scanText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ScanTriggers\ScanTriggerManager.cs') -Raw
if ($scanText -match 'currentPlayers\s*!=\s*0') { $errors += 'OnPlayerExitScan must fire when any player leaves; it must not require currentPlayers == 0.' }
if ($scanText -notmatch 'currentPlayers\s*>=\s*previousPlayers') { $errors += 'OnPlayerExitScan must ignore equal/increased counts and fire on any decrease.' }

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'CTE 1.7.0 static checks passed.'





















