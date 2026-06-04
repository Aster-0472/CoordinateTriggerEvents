# 即时加载

CTE 支持配置文件即时加载，但从 `1.1.0` 起不再使用定时轮询扫描文件修改时间。

## 工作方式

插件使用 `FileSystemWatcher` 监听：

```text
<当前Rundown>/Custom/CoordinateTriggerEvents/*.json
```

监听事件：

```text
保存 / Changed
创建 / Created
删除 / Deleted
重命名 / Renamed
```

文件事件发生后，插件不会在 FileSystemWatcher 线程中直接解析 JSON，而是只设置一个“待重载标记”。随后在游戏主线程安全点执行重载。

## 防抖

多数编辑器保存文件时会连续触发多次 Changed 事件。CTE 使用短延迟防抖合并这些事件，避免一次保存重复加载多次。

当前策略：

```text
保存文件
↓
设置待重载标记
↓
等待约 350ms 防抖
↓
主线程执行一次配置重载
```

## 和旧版本差异

旧版本可能每 1~3 秒扫描 JSON 文件 `LastWriteTimeUtc`，这属于轮询。新版本不再扫描所有文件修改时间，正常运行时只检查内存标记。

## 注意事项

- 即时加载只适合修改配置字段、事件列表、触发器参数。
- 如果你修改了 GTFO DataBlock、MTFO PartialData、AWO/EOS 定义文件，仍可能需要重新进图或重启游戏。
- 如果配置 JSON 正在保存过程中被读到半写入状态，插件会在日志中输出解析错误；保存完成后再次触发 watcher 通常会恢复。
