# Cooldown 与性能规则

## 持续触发器 Cooldown 必填

从 `1.0.8` 起，持续触发器的 `Cooldown` 是强制字段，不允许删除。最低下限为 `1.0` 秒。

如果持续触发器缺少 `Cooldown`，插件会把它视为配置错误并跳过或输出错误；如果 `Cooldown < 1.0`，会被钳制到 1.0。

这个规则参考 AWO `Type: 20001` 事件循环的安全思路：循环型事件不允许过低间隔，否则容易造成卡顿或事件风暴。

## 哪些是持续触发器

### PositionTriggers

```text
AnyPlayerInside
AllPlayersInside
```

### ScanTriggers

```text
OnAllPlayersInsideScan
OnAllPlayersExitedScan
```

### InteractTriggers

```text
OnBigPickupHeld
OnBigPickupPlaced
OnTerminalUsing
OnTerminalExited
```

## AnyPlayerEnter 的 Cooldown 是否有用

`AnyPlayerEnter` 是进入边沿触发：玩家从范围外进入范围内时触发一次。玩家一直站在范围内不会按 Cooldown 重复触发。

所以：

```json
"TriggerMode": "AnyPlayerEnter",
"Cooldown": 1.0
```

只用于限制快速离开再进入的重复触发，不是持续触发间隔。

## 主要性能风险

### Debug Marker

```json
"Debug": {
  "Enabled": true,
  "ShowScanMarkers": true
}
```

调试 Marker 会生成可视化对象。正式发布建议关闭。

### 持续触发器

持续触发器会按 Cooldown 检查并触发事件。Cooldown 越低，触发频率越高。最低 1.0 秒只是保护底线，不代表所有场景都应该写 1.0。

推荐：

```text
轻量提示事件：1~3 秒
普通逻辑事件：3~5 秒
刷怪/灯光/门/目标推进类重事件：5 秒以上或使用一次性触发
```

### 终端事件

终端使用/退出事件已经改为延迟队列处理，避免在终端交互调用栈内同步执行重事件。

### 即时加载

即时加载使用 FileSystemWatcher，不再轮询扫描文件修改时间。只有保存、创建、删除、重命名 JSON 文件后才重载。
