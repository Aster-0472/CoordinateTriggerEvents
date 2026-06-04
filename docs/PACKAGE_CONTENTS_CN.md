# 本 GitHub 上传包内容说明

- `source/`：插件源码和 csproj。
- `Custom/CoordinateTriggerEvents/`：配置模板，上传到仓库可作为示例和格式基准。
- `docs/`：上传教程、Release Notes、包内容说明。
- `build/`：本次真实引用库编译报告和构建日志。
- `patches/`：从上一版到 1.1.0 的补丁说明。
- `release/`：建议作为 GitHub Release 附件上传的插件包、源码包和 SHA256 校验文件。

本包不包含 GTFO 游戏引用库。编译时需要在本地通过 `-p:GameDir=<GTFO目录>` 指向你的 GTFO 安装目录。


## Wiki 文档目录

本次包新增 `wiki/` 目录，可直接上传到 GitHub Wiki：

```text
wiki/Home.md
wiki/Installation.md
wiki/Configuration-Path-and-Templates.md
wiki/Quick-Start.md
wiki/Position-Triggers.md
wiki/Scan-Triggers.md
wiki/Interact-Triggers.md
wiki/Warden-Events-and-Compatibility.md
wiki/Player-Count-and-Cycle-Events.md
wiki/Cooldown-and-Performance.md
wiki/Instant-Reload.md
wiki/Debug-Markers-and-Logs.md
wiki/Examples.md
wiki/Troubleshooting.md
wiki/Developer-Build.md
wiki/GitHub-Upload-and-Release.md
wiki/_Sidebar.md
```

主仓库上传后，Wiki 需要单独上传到 `<仓库名>.wiki.git`。
