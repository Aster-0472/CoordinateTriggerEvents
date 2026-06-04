# 源码构建

## 仓库内容

GitHub 仓库包包含：

```text
source/Plugin.cs
source/CoordinateTriggerEvents.csproj
Custom/CoordinateTriggerEvents/Template_CN.json
Custom/CoordinateTriggerEvents/Template_EN.json
docs/
build/
patches/
release/
```

不包含：

```text
GTFO 游戏 DLL
BepInEx interop 生成 DLL
GTFO-API DLL
dotnet SDK 压缩包
```

这些文件不应该上传到公开仓库。

## 本地构建

需要 .NET 6 SDK 和真实 GTFO 引用库。

示例：

```bash
dotnet build source/CoordinateTriggerEvents.csproj -c Release -p:GameDir=/path/to/GTFO
```

如果你的项目文件使用 `GameDir` 查找 GTFO/BepInEx/Il2CppInterop/GTFO-API 引用，必须保证该目录结构完整。

## 发布前检查

- `Plugin.cs` 内部版本号是否为当前版本。
- DLL 文件名是否带版本号。
- README、CHANGELOG、BUILD_REPORT、SHA256 是否统一版本号。
- `Template_CN.json` 是否仍为标准模板。
- `Template_EN.json` 是否只翻译注释，不改变字段结构。
- Release 包中是否没有引用库 DLL。
