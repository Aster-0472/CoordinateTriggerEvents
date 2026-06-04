# 故障排查

## 配置没有生效

检查：

1. JSON 是否放在当前 Rundown 的：

```text
Custom/CoordinateTriggerEvents/
```

2. 文件是否为 `.json`。
3. 顶层是否：

```json
"Enabled": true
```

4. `MainLevelLayoutIDs` 是否匹配当前关卡。
5. BepInEx 日志中是否有 JSON 解析错误。

## 修改配置后没有即时加载

检查：

- 文件是否保存到了 `Custom/CoordinateTriggerEvents/*.json`。
- 是否保存为临时文件后由编辑器替换；如果是，CTE 会监听 Created/Renamed，但极少数编辑器行为可能需要再保存一次。
- 日志中是否显示 JSON 解析失败。

## 持续触发器不运行

检查：

- TriggerMode 是否属于持续触发器。
- 是否显式写了 `Cooldown`。
- `Cooldown` 是否大于等于 1.0。
- `Events` 是否为空。
- `RequireAlivePlayers=true` 时玩家是否倒地。

## AnyPlayerEnter 没有重复触发

这是正常行为。`AnyPlayerEnter` 是进入边沿触发，玩家一直在范围内不会按 Cooldown 重复触发。需要重复触发请使用 `AnyPlayerInside`。

## 终端使用/退出卡顿

1. 确认使用 1.1.0 或更新版本。
2. 确认旧 DLL 已删除。
3. 检查事件列表中是否有刷怪、灯光、门状态、Objective 推进等重事件。
4. 检查是否同时加载 AWO、EOS、EOSExt.SecurityDoorTerminal、TerminalQueryAPI。
5. 尽量使用稳定 `TerminalSelector`，避免运行时反复 fallback 匹配。

## 扫描点 Index 不对

使用 ECC / ScanPositionOverride / CTE 日志中的索引，不要凭运行时枚举顺序猜测。

## 大物品 Index 不对

大物品 Index 可能因 Layout、Zone、生成物变化而改变。修改地图后必须重新进图看日志确认。

## 重复加载插件

如果 BepInEx 日志显示 CTE 被加载多次，请删除旧版 DLL。只保留：

```text
CoordinateTriggerEvents_v1.1.0.dll
```
