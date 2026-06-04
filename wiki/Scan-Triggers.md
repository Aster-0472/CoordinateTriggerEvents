# ScanTriggers 扫描点触发器

`ScanTriggers` 用于把安全扫描点状态转换为 WardenEvent。

## Index

```json
"Index": 0
```

`Index` 建议使用 ScanPositionOverride / ECC / CTE 日志输出的扫描点索引。模板注释中说明索引从 1 开始；实际配置前请以日志为准。

## TriggerMode

### OnScanActivated

玩家激活扫描点时触发事件。

```json
"TriggerMode": "OnScanActivated"
```

### OnPlayerExitScan

玩家退出扫描点时触发事件。

```json
"TriggerMode": "OnPlayerExitScan"
```

### OnAllPlayersEnterScan

全员进入扫描点时触发一次。

```json
"TriggerMode": "OnAllPlayersEnterScan"
```

### OnAllPlayersInsideScan

全员持续在扫描点内时按 `Cooldown` 重复触发。

```json
"TriggerMode": "OnAllPlayersInsideScan",
"Cooldown": 5.0
```

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

### OnAllPlayersExitScan

全员曾进入扫描点后，全部退出时触发一次。

```json
"TriggerMode": "OnAllPlayersExitScan"
```

### OnAllPlayersExitedScan

全员曾进入扫描点并全部退出后，按 `Cooldown` 重复触发；初始无人不会触发。

```json
"TriggerMode": "OnAllPlayersExitedScan",
"Cooldown": 5.0
```

这是持续触发器，`Cooldown` 必填，最低 1.0 秒。

## 删除持续触发器是否还会生效

如果删除整个 `OnAllPlayersInsideScan` 或 `OnAllPlayersExitedScan` 触发器配置，它不会继续生效。

如果只删除 `Cooldown`，触发器会被视为配置错误并跳过或按运行时保护处理；持续触发器必须显式写 `Cooldown`。

## 性能建议

- 持续扫描触发器不要把 `Cooldown` 写得太低。
- 正式发布时关闭 Debug Marker。
- 如果只需要全员进入一次，用 `OnAllPlayersEnterScan`，不要使用持续触发模式。
