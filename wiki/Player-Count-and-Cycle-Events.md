# 玩家人数事件与循环事件

CTE 支持两类额外事件组织方式：玩家人数事件和触发循环事件。

## PlayerCountEvents

开启后，触发器不再执行普通 `Events`，而是按当前符合条件的玩家人数执行对应事件组。

```json
"UsePlayerCountEvents": true,
"PlayerCountEvents": {
  "1": [],
  "2": [],
  "3": [],
  "4": []
}
```

适合：

- 1 人进入时播放提示。
- 2 人进入时开启小规模敌潮。
- 4 人全部到位后推进 Objective。

如果 `RequireAlivePlayers=true`，倒地玩家不计入人数。

## TriggerCycleEvents

每次触发成功后累计一次 Count。当累计次数达到 `TriggerCycleCount` 时，额外执行 `TriggerCycleEvents`。

```json
"UseTriggerCycleEvents": true,
"TriggerCycleCount": 3,
"TriggerCycleEvents": []
```

适合：

- 玩家多次进出区域后触发惩罚。
- 每第 N 次扫描点状态变化时触发额外事件。
- 大物品多次拾取/放下后推进隐藏流程。

## PickupDropCycleEvents

大物品拾取/放下路径可使用：

```json
"UsePickupDropCycleEvents": true,
"PickupDropCycleCount": 2,
"PickupDropCycleEvents": []
```

适合：玩家反复移动关键大物品时触发额外逻辑。

## 性能注意

循环事件本身不一定昂贵，真正昂贵的是事件组里执行的内容。例如刷怪、灯光复制、终端状态、门状态、复杂 Objective 推进等。建议：

- 循环事件使用较大的 `Cooldown`。
- 避免每秒重复执行重事件。
- 在日志中确认触发次数是否符合预期。
