# PositionTriggers 坐标/区域触发器

`PositionTriggers` 用于把玩家位置转换成 WardenEvent。

## TriggerAreaMode

### Radius

按世界坐标点和半径创建触发范围。

核心字段：

```json
"TriggerAreaMode": "Radius",
"Position": { "x": 0, "y": 0, "z": 0 },
"Radius": 5.0
```

适合：伏击房间入口、禁区、路线拐点、Boss 区域。

### OverrideBigZone

生成覆盖指定 Zone 的触发范围。

核心字段：

```json
"TriggerAreaMode": "OverrideBigZone",
"DimensionIndex": 0,
"Layer": 0,
"LocalIndex": 0
```

适合：整区进入检测、整区离开检测。

### OverrideArea

生成覆盖指定 Zone Area 的触发范围。

核心字段：

```json
"TriggerAreaMode": "OverrideArea",
"DimensionIndex": 0,
"Layer": 0,
"LocalIndex": 0,
"Count": 0
```

`Count` 表示 Area 索引。

## TriggerMode

### AnyPlayerEnter

任意符合条件的玩家从范围外进入范围内时触发一次。

`Cooldown` 只限制快速离开再进入的重复触发，不会让玩家站在范围内持续触发。

### AnyPlayerInside

范围内存在任意符合条件的玩家时，按 `Cooldown` 重复触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

### AllPlayersEnter

所有符合条件的玩家进入范围后触发一次。

### AllPlayersInside

所有符合条件的玩家都在范围内时，按 `Cooldown` 重复触发。

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

### AnyPlayerExit

任意玩家从范围内离开时触发。

### AllPlayersExit

所有玩家都从范围内离开时触发。

## RequireAlivePlayers

```json
"RequireAlivePlayers": true
```

为 `true` 时，倒地玩家不计算为人数。适合全员进入、全员持续在范围内、人数事件等场景。

## PlayerCountEvents

启用后按玩家人数执行对应事件组。

```json
"UsePlayerCountEvents": true,
"PlayerCountEvents": {
  "1": [],
  "2": [],
  "3": [],
  "4": []
}
```

`UsePlayerCountEvents=false` 时执行普通 `Events`。

## TriggerCycleEvents

每次触发成功后累计一次 Count，累计到 `TriggerCycleCount` 后额外触发 `TriggerCycleEvents`。

```json
"UseTriggerCycleEvents": true,
"TriggerCycleCount": 3,
"TriggerCycleEvents": []
```

适合：进入三次后刷怪、循环提示、阶段递进。
