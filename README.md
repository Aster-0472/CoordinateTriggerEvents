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

## GitHub 发布建议

仓库源码请上传本包根目录内容；正式给玩家下载的文件建议放在 GitHub Release 附件中：

```text
release/CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
release/CoordinateTriggerEvents_1.1.0_performance_optimized_source_CN.zip
release/SHA256SUMS_1.1.0.txt
```

详细步骤见：`docs/GITHUB_UPLOAD_GUIDE_CN.md`。

注意：不要把 GTFO 游戏 DLL、BepInEx interop DLL、GTFO-API DLL 或本地 dotnet SDK 上传到 GitHub。


## Wiki 文档

本仓库包包含 `wiki/` 目录，可作为 GitHub Wiki 页面上传。建议先在 GitHub 网页端创建第一篇 Wiki 页面，再克隆 `<repo>.wiki.git`，把 `wiki/` 中的 Markdown 文件复制进去并推送。

详细步骤见：

```text
docs/WIKI_UPLOAD_GUIDE_CN.md
wiki/GitHub-Upload-and-Release.md
```

Wiki 内容参考 EOS / ExtraObjectiveSetup 的文档组织方式：主页列出功能分类，独立页面解释配置模板、触发器、事件、性能和故障排查。
