# 快速开始

本页给出最小可用配置流程。

## 1. 复制模板

从：

```text
Custom/CoordinateTriggerEvents/Template_CN.json
```

复制为：

```text
Custom/CoordinateTriggerEvents/L10_CTE.json
```

## 2. 启用配置

把：

```json
"Enabled": false
```

改为：

```json
"Enabled": true
```

## 3. 限定关卡

数字 ID：

```json
"MainLevelLayoutIDs": 65527
```

或者 MTFO PartialData 字符串 ID：

```json
"MainLevelLayoutIDs": "Level_10_L1"
```

## 4. 添加一个进入范围触发器

```json
"PositionTriggers": [
  {
    "ID": "enter_ambush_area",
    "TriggerAreaMode": "Radius",
    "TriggerMode": "AnyPlayerEnter",
    "Enabled": true,
    "Position": { "x": 100.0, "y": 2.0, "z": -50.0 },
    "Radius": 5.0,
    "Cooldown": 1.0,
    "RequireAlivePlayers": true,
    "DebugVisible": false,
    "Events": [
      {
        "Type": 0
      }
    ],
    "UsePlayerCountEvents": false,
    "PlayerCountEvents": { "1": [], "2": [], "3": [], "4": [] },
    "UseTriggerCycleEvents": false,
    "TriggerCycleCount": 0,
    "TriggerCycleEvents": []
  }
]
```

`AnyPlayerEnter` 是进入边沿触发：玩家从范围外进入范围内时触发一次。玩家一直站在范围内不会按 `Cooldown` 重复触发。

## 5. 测试时开启 Debug Marker

调试时可以开启：

```json
"Debug": {
  "Enabled": true,
  "ShowScanMarkers": true,
  "ShowNames": true
}
```

正式发布配置建议关闭 Debug，避免额外可视化对象和日志开销。
