# GitHub 上传与 Release

本页说明如何把 CTE 上传到 GitHub，并发布 Release。

## 1. 创建仓库

1. 登录 GitHub。
2. 点击右上角 `+`。
3. 选择 `New repository`。
4. 仓库名建议：

```text
CoordinateTriggerEvents
```

5. 不要勾选自动生成 README，避免覆盖本包里的 README。
6. 创建仓库。

## 2. 上传源码包内容

解压 GitHub 上传包，进入：

```text
CoordinateTriggerEvents/
```

把该目录下所有文件上传到仓库根目录。

网页端：

```text
Add file -> Upload files -> 拖入文件 -> Commit changes
```

命令行：

```bash
git init
git branch -M main
git add .
git commit -m "Upload CoordinateTriggerEvents 1.1.0 source"
git remote add origin https://github.com/<你的用户名>/CoordinateTriggerEvents.git
git push -u origin main
```

## 3. 创建 Release

1. 进入仓库主页。
2. 打开 `Releases`。
3. 点击 `Draft a new release`。
4. Tag：

```text
v1.1.0
```

5. Title：

```text
CoordinateTriggerEvents 1.1.0
```

6. Release notes 复制：

```text
docs/RELEASE_NOTES_1.1.0_CN.md
```

7. 上传附件：

```text
release/CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
release/CoordinateTriggerEvents_1.1.0_performance_optimized_source_CN.zip
release/SHA256SUMS_1.1.0.txt
```

8. 点击 `Publish release`。

## 4. 上传 Wiki

GitHub Wiki 是单独的 Git 仓库。流程：

1. 在 GitHub 仓库页面打开 `Wiki`。
2. 创建第一个页面，可以先随便写 `Home` 并保存。
3. 克隆 Wiki 仓库：

```bash
git clone https://github.com/<你的用户名>/CoordinateTriggerEvents.wiki.git
```

4. 把本包 `wiki/` 目录内的 `.md` 文件复制到刚克隆的 wiki 仓库根目录。
5. 提交并推送：

```bash
cd CoordinateTriggerEvents.wiki
git add .
git commit -m "Add CoordinateTriggerEvents wiki"
git push
```

## 5. 版本号规则

```text
1.0.8 -> 1.0.9 -> 1.1.0 -> 1.1.1
```

不要使用：

```text
1.0.10
```

发布时同步修改：

- DLL 内部版本
- DLL 文件名
- 插件包名称
- 源码包名称
- README / CHANGELOG / BUILD_REPORT
- SHA256 文件
- GitHub Release tag
