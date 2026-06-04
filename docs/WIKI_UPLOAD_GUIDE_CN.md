# CoordinateTriggerEvents Wiki 上传教程

本包已经包含完整 `wiki/` 目录，可直接作为 GitHub Wiki 内容使用。

## Wiki 内容参考

我参考了 EOS / ExtraObjectiveSetup Wiki 的组织方式：

- Home 页面先解释插件用途和功能分类。
- 独立页面分别解释功能、模板、事件、故障排查。
- 配置说明使用带注释的 JSON 模板结构。
- 事件页面按字段和示例说明可用写法。

CTE 的 Wiki 对应拆分为：

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

## 网页端创建 Wiki

1. 打开你的 GitHub 仓库。
2. 点击 `Wiki`。
3. 点击 `Create the first page`。
4. 页面名写 `Home`。
5. 可以先粘贴本包 `wiki/Home.md` 的内容。
6. 保存页面。

## 命令行批量上传 Wiki

创建第一篇 Wiki 页面后，执行：

```bash
git clone https://github.com/<你的用户名>/CoordinateTriggerEvents.wiki.git
cd CoordinateTriggerEvents.wiki
```

复制本包 `wiki/` 下全部 `.md` 文件到该目录，然后：

```bash
git add .
git commit -m "Add CoordinateTriggerEvents wiki"
git push
```

## 后续更新

每次插件版本更新后，建议同步修改：

- `wiki/Home.md` 当前版本号
- `wiki/GitHub-Upload-and-Release.md` Release tag
- `wiki/Developer-Build.md` 构建说明
- `wiki/Cooldown-and-Performance.md` 性能规则
- `wiki/Instant-Reload.md` 即时加载说明

GitHub Wiki 是独立仓库，不会因为你更新主仓库自动变化，除非你额外设置 GitHub Actions 同步。
