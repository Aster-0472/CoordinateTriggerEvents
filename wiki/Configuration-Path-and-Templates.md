# 配置路径与模板

## 生成位置

CTE 的配置生成路径应参考 MTFO / EOS 风格：优先使用当前 Rundown 的 `Custom` 根目录，而不是 DLL 所在目录。

最终路径：

```text
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_CN.json
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_EN.json
```

这种方式和 EOS 文档里“启动游戏后在 `BepInEx/plugins/YOUR_RUNDOWN/Custom/ExtraObjectiveSetup` 生成文件”的做法类似，只是 CTE 使用自己的子目录 `CoordinateTriggerEvents`。

## 模板文件

当前插件会生成两份模板：

```text
Template_CN.json  中文注释模板
Template_EN.json  英文注释模板
```

`Template_CN.json` 是标准格式，字段顺序、注释位置、默认值都应以该文件为准。英文模板必须使用同一结构，只把中文注释翻译成英文，不应改变字段顺序和示例结构。

## 多配置文件

插件支持读取多个 JSON 文件：

```text
Custom/CoordinateTriggerEvents/*.json
```

建议按关卡拆分：

```text
L10_CTE.json
L11_CTE.json
L12_CTE.json
```

不要直接编辑模板作为正式配置。模板默认 `Enabled=false`，用于复制。

## MainLevelLayoutIDs

`MainLevelLayoutIDs` 用来限制配置只在指定关卡生效。

数字 ID 示例：

```json
"MainLevelLayoutIDs": 65527
```

MTFO PartialData 字符串 ID 示例：

```json
"MainLevelLayoutIDs": "Level_10_L1"
```

如果写成数组或对象，当前标准模板不推荐。为了减少配置误用，建议一个 JSON 文件只对应一个关卡 ID。

## JSON 注释

CTE 的配置读取允许：

```text
// 单行注释
尾随逗号
```

这使模板可以保留详细说明，方便地图作者复制修改。
