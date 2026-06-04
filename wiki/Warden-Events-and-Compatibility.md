# WardenEvent 与 AWO/EOS/Legacy 兼容

CTE 的核心目标是把触发条件转换为 WardenEvent。

## 普通事件列表

```json
"Events": [
  {
    "Type": 0
  }
]
```

`Events` 内部字段应尽量遵循 GTFO 原版 `WardenObjectiveEventData`。

## WardenEvents 别名

部分旧配置或兼容配置可能使用：

```json
"WardenEvents": []
```

CTE 会尽量兼容这类写法，但推荐统一使用模板中的 `Events`。

## AWO / WEE 兼容

AWO 使用扩展 WardenEvent 数据结构，并通过 WEE 事件类型扩展原版 WardenEvent。CTE 在触发事件时会尽量保留或识别 AWO 常见字段，尤其是：

```text
WardenIntel
Delay
Duration
SubObjective
Fog
SoundID
DialogueID
DimensionIndex / Layer / LocalIndex
```

如果你的事件依赖 AWO 扩展字段，建议按 AWO 文档格式书写，并在 BepInEx 日志中确认 AWO 已加载。

## EOS / Legacy 兼容

CTE 不替代 EOS 的 Objective / Extended Event 系统，但应尽量兼容 EOS/Legacy 生态中的旧字段别名和事件包装方式。

常见原则：

- 不只按 vanilla 字段解析事件。
- 保留 `Events` / `WardenEvents` 兼容入口。
- 字符串 ID 尽量兼容 MTFO PartialData 的 `_persistentID.json`。
- 终端、扫描点、大物品绑定不要只依赖运行时顺序，尽量通过日志确认 TSL、Index、Serial。

## Delay 与重事件

如果事件列表中包含刷怪、灯光、门状态、终端状态、Objective 推进等重事件，建议合理使用 Delay 或触发器 Cooldown，避免同一帧执行大量重事件。

CTE 终端路径已经做了延迟队列和每 Tick 限流，但 Position/Scan/BigPickup 持续触发器仍应通过 `Cooldown` 控制频率。
