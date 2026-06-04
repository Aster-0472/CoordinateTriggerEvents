# CoordinateTriggerEvents

`CoordinateTriggerEvents` 是一个面向 GTFO 自定义 Rundown 作者的 BepInEx IL2CPP 插件，用于把玩家位置、扫描点状态、大物品交互和终端交互转换为可配置的 WardenEvent。

本 Wiki 的组织方式参考了 `ExtraObjectiveSetup / EOS`：主页先说明插件用途，再按功能分成触发器、事件、配置、性能与故障排查页面。EOS Wiki 的主页也是先说明插件用于添加更多 Warden Objective，并按 Tweaks / Objectives 分类列出文档入口；CTE 这里改为按 Position / Scan / Interact / Event / Performance 分类。

## 插件定位

CTE 不替代 EOS、AWO、ECC 或原版 Objective 系统，而是作为“事件触发层”使用：

- 玩家进入、离开某个坐标半径或 Zone/Area 覆盖范围时触发事件。
- 扫描点激活、玩家退出扫描点、全员进入/退出扫描点时触发事件。
- 大物品拾取、放下、持有、放置状态持续时触发事件。
- 终端使用、退出、正在使用、退出后保持状态时触发事件。
- 事件列表支持原版 WardenObjectiveEventData，同时尽量兼容 AWO/WEE、EOS/Legacy 常见字段。

## 当前版本

当前 GitHub 包版本：`1.1.0`

版本号规则：

```text
1.0.8 -> 1.0.9 -> 1.1.0 -> 1.1.1
```

也就是说 patch 位从 9 再加 1 时进位，不使用 `1.0.10` 这种写法。

## 主要功能页

- [安装与依赖](Installation)
- [配置路径与模板](Configuration-Path-and-Templates)
- [快速开始](Quick-Start)
- [PositionTriggers 坐标/区域触发器](Position-Triggers)
- [ScanTriggers 扫描点触发器](Scan-Triggers)
- [InteractTriggers 大物品/终端触发器](Interact-Triggers)
- [WardenEvent 与 AWO/EOS/Legacy 兼容](Warden-Events-and-Compatibility)
- [Cooldown 与性能规则](Cooldown-and-Performance)
- [即时加载](Instant-Reload)
- [故障排查](Troubleshooting)

## 目录结构建议

运行后模板应生成到当前自定义 Rundown 的 Custom 目录下：

```text
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_CN.json
<当前Rundown>/Custom/CoordinateTriggerEvents/Template_EN.json
```

实际关卡配置建议按关卡拆分：

```text
<当前Rundown>/Custom/CoordinateTriggerEvents/L10_CTE.json
<当前Rundown>/Custom/CoordinateTriggerEvents/L11_CTE.json
<当前Rundown>/Custom/CoordinateTriggerEvents/L12_CTE.json
```

模板文件默认 `Enabled=false`，不会影响关卡。复制模板并改名为关卡配置后，再把 `Enabled` 改为 `true`。
