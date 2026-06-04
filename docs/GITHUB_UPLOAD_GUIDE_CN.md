# CoordinateTriggerEvents 上传 GitHub 教程

## 1. 仓库结构参考

本包参考 ExtraObjectiveSetup / EOS 的公开仓库组织方式：源码、JSON/配置相关目录、Patch/工具/文档分开管理。CoordinateTriggerEvents 当前推荐结构如下：

```text
CoordinateTriggerEvents/
├─ source/
│  ├─ Plugin.cs
│  └─ CoordinateTriggerEvents.csproj
├─ Custom/
│  └─ CoordinateTriggerEvents/
│     ├─ Template_CN.json
│     └─ Template_EN.json
├─ docs/
│  ├─ GITHUB_UPLOAD_GUIDE_CN.md
│  └─ RELEASE_NOTES_1.1.0_CN.md
├─ build/
│  ├─ BUILD_REPORT_1.1.0_CN.txt
│  └─ build_1.1.0_realrefs.log
├─ patches/
│  └─ CoordinateTriggerEvents_1.1.0_performance_optimized.patch
├─ release/
│  ├─ CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
│  ├─ CoordinateTriggerEvents_1.1.0_performance_optimized_source_CN.zip
│  └─ SHA256SUMS_1.1.0.txt
├─ README.md
├─ CHANGELOG.md
├─ .gitignore
└─ .gitattributes
```

不要上传 GTFO 游戏 DLL、BepInEx interop 生成 DLL、GTFO-API DLL 或本地 dotnet SDK。仓库只保存源码、模板、说明、补丁、构建报告和 Release 包。

## 2. 在 GitHub 网页端创建仓库

1. 登录 GitHub。
2. 点击右上角 `+`，选择 `New repository`。
3. Repository name 建议填写 `CoordinateTriggerEvents`。
4. Public / Private 根据你的发布意图选择。
5. 不勾选自动生成 README，避免和本包里的 README.md 冲突。
6. 创建仓库。

## 3. 上传源码到仓库

网页端方式：

1. 打开新建的仓库主页。
2. 点击 `Add file` → `Upload files`。
3. 解压本 GitHub 上传包。
4. 进入解压后的 `CoordinateTriggerEvents/` 文件夹。
5. 把其中所有文件和文件夹拖入 GitHub 上传页面。
6. Commit message 填写：

```text
Upload CoordinateTriggerEvents 1.1.0 source
```

7. 点击 `Commit changes`。

命令行方式：

```bash
git init
git branch -M main
git add .
git commit -m "Upload CoordinateTriggerEvents 1.1.0 source"
git remote add origin https://github.com/<你的用户名>/CoordinateTriggerEvents.git
git push -u origin main
```

## 4. 创建 GitHub Release

1. 进入仓库主页。
2. 点击右侧 `Releases`。
3. 点击 `Draft a new release`。
4. Tag 填写：

```text
v1.1.0
```

5. Release title 填写：

```text
CoordinateTriggerEvents 1.1.0
```

6. Release notes 可复制 `docs/RELEASE_NOTES_1.1.0_CN.md` 的内容。
7. 上传附件：

```text
release/CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
release/CoordinateTriggerEvents_1.1.0_performance_optimized_source_CN.zip
release/SHA256SUMS_1.1.0.txt
```

8. 点击 `Publish release`。

## 5. 推荐后续版本规则

按三段进位规则递增：

```text
1.0.8 -> 1.0.9 -> 1.1.0 -> 1.1.1
```

每次发布都需要同步更新：

- DLL 内部版本号
- DLL 文件名
- 插件包名称
- 源码包名称
- README.md
- CHANGELOG.md
- BUILD_REPORT
- SHA256SUMS
- GitHub Release tag

## 6. 发布前检查清单

- `source/Plugin.cs` 中版本号是否一致。
- `CoordinateTriggerEvents.csproj` 是否没有引用本地绝对路径以外的非法打包文件。
- `Custom/CoordinateTriggerEvents/Template_CN.json` 是否仍以用户标准模板为准。
- `Template_EN.json` 是否只是翻译注释，字段结构与中文模板一致。
- `release/SHA256SUMS_1.1.0.txt` 是否对应当前上传附件。
- 仓库中是否没有 GTFO 游戏 DLL / BepInEx interop DLL / GTFO-API DLL。
