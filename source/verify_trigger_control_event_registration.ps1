$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$triggerControl = Get-Content -LiteralPath (Join-Path $root 'WardenEvents\TriggerControlEvent.cs') -Raw
$plugin = Get-Content -LiteralPath (Join-Path $root 'Core\Plugin.cs') -Raw

$errors = New-Object System.Collections.Generic.List[string]

if ($triggerControl -notmatch 'TriggerControlEventType\s*=\s*700') {
    $errors.Add('Trigger control event type must remain 700.')
}

if ($triggerControl -notmatch 'SetupTriggerControlWardenEvent') {
    $errors.Add('Type 700 must register itself as an EOS WardenEvent definition.')
}

if ($plugin -notmatch 'SetupTriggerControlWardenEvent\(\)') {
    $errors.Add('Plugin.Load must call Runtime.SetupTriggerControlWardenEvent().')
}

if ($triggerControl -notmatch 'EOSWardenEventManager' -or $triggerControl -notmatch 'AddEventDefinition') {
    $errors.Add('Type 700 registration must follow the EOS/AWO AddEventDefinition pattern.')
}

if ($triggerControl -notmatch 'HandleTriggerControlWardenEvent\s*\(\s*WardenObjectiveEventData\s+\w+\s*\)') {
    $errors.Add('Registered Type 700 handler must accept WardenObjectiveEventData.')
}

if ($triggerControl -notmatch 'TryHandleWardenIntelDirect\s*\(\s*element\s*,\s*ownerLabel\s*\)') {
    $errors.Add('CTE-local Type 700 handling must keep WardenIntel compatible instead of swallowing it.')
}

if ($triggerControl -notmatch 'WorldEventObjectFilter') {
    $errors.Add('Terminal/native Type 700 handling must support a WardenObjectiveEventData-native target field.')
}

if ($errors.Count -gt 0) {
    foreach ($err in $errors) { Write-Error $err }
    exit 1
}

Write-Output 'Type 700 EOS registration checks passed.'


