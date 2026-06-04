# 配置示例

## 进入区域触发一次

```json
{
  "ID": "enter_zone_warning",
  "TriggerAreaMode": "Radius",
  "TriggerMode": "AnyPlayerEnter",
  "Enabled": true,
  "Position": { "x": 120.5, "y": 2.0, "z": -84.0 },
  "Radius": 5.0,
  "Cooldown": 1.0,
  "RequireAlivePlayers": true,
  "DebugVisible": false,
  "Events": [ { "Type": 0 } ],
  "UsePlayerCountEvents": false,
  "PlayerCountEvents": { "1": [], "2": [], "3": [], "4": [] },
  "UseTriggerCycleEvents": false,
  "TriggerCycleCount": 0,
  "TriggerCycleEvents": []
}
```

## 玩家在区域内持续触发

```json
{
  "ID": "inside_alarm_repeat",
  "TriggerAreaMode": "Radius",
  "TriggerMode": "AnyPlayerInside",
  "Enabled": true,
  "Position": { "x": 120.5, "y": 2.0, "z": -84.0 },
  "Radius": 5.0,
  "Cooldown": 5.0,
  "RequireAlivePlayers": true,
  "DebugVisible": false,
  "Events": [ { "Type": 0 } ],
  "UsePlayerCountEvents": false,
  "PlayerCountEvents": { "1": [], "2": [], "3": [], "4": [] },
  "UseTriggerCycleEvents": false,
  "TriggerCycleCount": 0,
  "TriggerCycleEvents": []
}
```

## 扫描点激活触发

```json
{
  "ID": "scan_activated",
  "TriggerMode": "OnScanActivated",
  "Enabled": true,
  "Index": 1,
  "Cooldown": 1.0,
  "RequireAlivePlayers": true,
  "Events": [ { "Type": 0 } ],
  "UsePlayerCountEvents": false,
  "PlayerCountEvents": { "1": [], "2": [], "3": [], "4": [] },
  "UseTriggerCycleEvents": false,
  "TriggerCycleCount": 0,
  "TriggerCycleEvents": []
}
```

## 终端使用触发

```json
{
  "ID": "terminal_use_event",
  "TargetType": "Terminal",
  "TriggerMode": "OnTerminalUse",
  "Enabled": true,
  "TerminalSelector": "[TERMINAL_0_0_0_0]",
  "Cooldown": 1.0,
  "Events": [ { "Type": 0 } ]
}
```

## 大物品持有期间重复触发

```json
{
  "ID": "bigpickup_held_repeat",
  "TargetType": "BigPickup",
  "TriggerMode": "OnBigPickupHeld",
  "Enabled": true,
  "Index": 1,
  "Cooldown": 5.0,
  "Events": [ { "Type": 0 } ]
}
```
