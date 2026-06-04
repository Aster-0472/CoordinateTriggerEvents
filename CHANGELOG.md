# CoordinateTriggerEvents 1.1.0 更新日志

## 版本号修正

- 按用户要求的三段进位规则发布：`1.0.9 -> 1.1.0`。
- DLL、插件包、源码包、README、CHANGELOG、BUILD_REPORT、SHA256 文件名和内部版本号全部统一为 `1.1.0`。

## 性能优化

- 保留 `FileSystemWatcher` 即时加载，不再进行文件轮询。
- 即时加载只在 JSON 保存、创建、删除、重命名后触发，并通过 350ms 防抖合并编辑器多次写入。
- 增加 `TriggerMode` / `TargetType` 标准化缓存，减少持续触发器热路径字符串分配。
- 增加 MTFO PartialData persistentID 与 LevelLayout 字符串 ID 解析缓存。
- `_persistentID.json` 只在需要时构建一次缓存，配置重载或远征切换后清空重建。
- 修复新增优化代码带来的可空警告，真实引用库编译结果为 0 警告、0 错误。

## 保留功能

- 保留用户上传的标准 `Template_CN.json` 格式。
- 保留 `Template_EN.json` 英文注释版本。
- 保留持续触发器 `Cooldown` 强制字段与最低 1.0 秒规则。
- 保留终端字段缓存、AWO TSL / Serial / TerminalSelector 兼容、终端事件延迟队列。
