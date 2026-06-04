# 安装与依赖

## 必须依赖

CTE 是 GTFO / BepInEx IL2CPP 插件，必须安装：

```text
BepInExPack_GTFO
GTFO-API
```

插件运行时依赖 BepInEx IL2CPP 环境、Harmony Patch、GTFO-API 的远征事件入口，以及 GTFO 游戏程序集中的 WardenEvent、Terminal、Bioscan、CarryItem 等类型。

## 可选兼容环境

以下不是硬依赖，但插件会尽量兼容：

```text
MTFO.Extension.PartialBlocks / PartialData
AdvancedWardenObjective / AWO
ExtraObjectiveSetup / EOS
ExtraChainedPuzzleCustomization / ScanPositionOverride / ECC
EOSExt 系列插件
Legacy 系列旧事件写法
```

## 安装步骤

1. 下载 GitHub Release 里的插件包：

```text
CoordinateTriggerEvents_1.1.0_performance_optimized_plugin_CN.zip
```

2. 解压后把 DLL 放入你的 Rundown 或插件目录，例如：

```text
BepInEx/plugins/TOA Heavy Industries/CoordinateTriggerEvents_v1.1.0.dll
```

3. 启动游戏一次。
4. 检查当前自定义 Rundown 下是否生成：

```text
Custom/CoordinateTriggerEvents/Template_CN.json
Custom/CoordinateTriggerEvents/Template_EN.json
```

5. 复制模板文件并改名为关卡配置，例如：

```text
Custom/CoordinateTriggerEvents/L10_CTE.json
```

6. 把复制出来的关卡配置中的：

```json
"Enabled": false
```

改为：

```json
"Enabled": true
```

7. 设置 `MainLevelLayoutIDs` 为对应关卡的数字 ID 或 MTFO PartialData 字符串 ID。

## 避免重复加载

安装新版本时请删除旧 DLL，例如：

```text
CoordinateTriggerEvents_v1.0.9.dll
CoordinateTriggerEvents_v1.0.10.dll
```

同一个 GUID 的旧版和新版同时存在时，BepInEx 可能重复加载或加载顺序异常。
