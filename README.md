# CoordinateTriggerEvents 1.1.0

GTFO / BepInEx IL2CPP 坐标、扫描点、大物品、终端事件触发插件。

## 安装

将 `CoordinateTriggerEvents_v1.1.0.dll` 放入 BepInEx 插件目录。配置模板会按 MTFO / EOS 风格生成到当前自定义 Rundown 的：

```text
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_CN.json
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_EN.json
```

`Template_CN.json` 继续以用户上传的中文模板为标准格式；`Template_EN.json` 使用同一字段顺序与结构，仅将中文注释翻译为英文。

## 1.1.0 性能优化

- 版本号按三段进位规则从 `1.0.9` 之后统一为 `1.1.0`，不再使用 `1.0.10`。
- 保留 1.0.10 的 MTFO 风格 FileSystemWatcher 即时加载：只有保存/创建/删除/重命名 JSON 时才排队重载，不再轮询扫描文件修改时间。
- 对热路径中的 `TriggerMode`、`TargetType` 字符串标准化增加缓存，避免持续触发器每 0.2 秒重复 `Trim/Replace/ToLowerInvariant` 造成额外分配。
- 对 MTFO PartialData 字符串 ID 与 `_persistentID.json` 解析增加缓存，避免同一配置反复反射 Manager 或重复扫描文件。
- 真实 GTFO/BepInEx/Il2CppInterop/GTFO-API 引用库编译通过。

## 性能风险说明

以下字段/功能可能带来额外开销，但本版已尽量在不改功能的前提下限制成本：

- `Debug.Enabled=true` 且 `ShowScanMarkers=true`：会生成/刷新调试可视化标记。正常发布配置建议保持 `Debug.Enabled=false`。
- 持续触发器：`AnyPlayerInside`、`AllPlayersInside`、`OnAllPlayersInsideScan`、`OnAllPlayersExitedScan`、`OnBigPickupHeld`、`OnBigPickupPlaced`、`OnTerminalUsing`、`OnTerminalExited` 必须设置 `Cooldown`，最低 1.0 秒。
- 终端 `OnTerminalUse` / `OnTerminalUnused` 继续使用延迟队列与每 Tick 限流，避免在终端交互调用栈内同步执行重事件。

