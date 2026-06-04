# CoordinateTriggerEvents 1.1.0

## 发布说明

本版本按三段进位规则从 1.0.9 升级为 1.1.0。

## 重点改动

- 即时加载改为 FileSystemWatcher + 350ms 防抖，只在 JSON 保存、创建、删除、重命名后重载，不再轮询文件修改时间。
- 对 TriggerMode / TargetType 标准化增加缓存，减少持续触发器热路径字符串分配。
- 对 MTFO PartialData 字符串 ID 与 _persistentID.json 解析增加缓存。
- 持续触发器 Cooldown 保持必填，最低 1.0 秒。
- 终端事件继续使用延迟队列和 Tick 限流，避免使用/退出终端瞬间同步执行重事件。
- 模板继续以用户上传的 Template_CN.json 为标准格式，Template_EN.json 由中文注释翻译生成。

## 推荐上传到 GitHub Release 的附件

- CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
- CoordinateTriggerEvents_1.1.0_performance_optimized_source_CN.zip
- SHA256SUMS_1.1.0.txt
