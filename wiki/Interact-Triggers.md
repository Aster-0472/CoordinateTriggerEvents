# InteractTriggers 大物品/终端触发器

`InteractTriggers` 用于把大物品和终端交互转换为 WardenEvent。

## TargetType: BigPickup

### OnBigPickupPickup

拾取大物品时触发。

```json
{
  "ID": "bigpickup_1_pickup_event",
  "TargetType": "BigPickup",
  "TriggerMode": "OnBigPickupPickup",
  "Index": 0,
  "Cooldown": 1.0,
  "Events": []
}
```

### OnBigPickupDrop

放下大物品时触发。

### OnBigPickupHeld

玩家持有该大物品期间按 `Cooldown` 重复触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

### OnBigPickupPlaced

大物品处于放置状态时按 `Cooldown` 重复触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

## BigPickup Index

大物品 Index 应以 CTE / ECC / 相关插件在 BepInEx 日志中的输出为准。修改 Layout、Zone 或生成物后，重新进图检查索引。

## TargetType: Terminal

### OnTerminalUse

使用终端时触发。

### OnTerminalUnused

退出终端时触发。

### OnTerminalUsing

玩家正在使用该终端期间按 `Cooldown` 重复触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

### OnTerminalExited

玩家至少使用并退出过一次该终端后，退出状态按 `Cooldown` 重复触发。初始未使用状态不会触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

## TerminalSelector

推荐使用 AWO TSL 风格：

```json
"TerminalSelector": "[TERMINAL_0_0_0_0]"
```

格式：

```text
[TERMINAL_DimensionIndex_LayerIndex_ZoneLocalIndex_TerminalIndexInZone]
```

CTE 也会尽量兼容 Terminal Serial、SerialText、TerminalSerial、TerminalTSL 等字段，但正式配置建议使用稳定的 `TerminalSelector`。

## 终端卡顿优化

从 1.0.7 起，终端使用/退出事件不在终端交互 Harmony 调用栈中同步执行，而是进入延迟队列并在后续 Tick 限流处理。

目的：避免终端 UI、EOSExt.SecurityDoorTerminal、TerminalQueryAPI、AWO 等插件同时运行时，终端使用/退出瞬间执行大量 WardenEvent 造成卡顿。
