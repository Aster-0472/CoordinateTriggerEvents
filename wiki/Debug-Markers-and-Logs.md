# 调试 Marker 与日志

## Debug 配置

```json
"Debug": {
  "Enabled": false,
  "ShowScanMarkers": true,
  "ShowNames": true,
  "MarkerColor": "#00BFFF",
  "LabelColor": "#FFFFFF",
  "MarkerAlpha": 0.35,
  "HeightOffset": 0.05,
  "LabelHeightOffset": 1.0
}
```

调试时可以开启 `Debug.Enabled=true`。正式发布配置建议关闭。

## DebugVisible

每个触发器可以单独控制是否显示调试标记：

```json
"DebugVisible": true
```

即使全局 Debug 开启，也可以通过单个触发器字段关闭某些 Marker。

## 日志索引绑定

CTE 会尽量输出终端、大物品、扫描点等运行时绑定信息，供地图作者配置：

```text
TerminalSelector / TSL
Terminal Serial / SerialText
BigPickup Index
Scan PuzzleOverrideIndex
Dimension / Layer / LocalIndex
```

建议每次改 Layout、Zone、Area、扫描点或大物品生成后，重新进图检查日志。

## Missing material on Cylinder / TextMeshPro

如果日志出现：

```text
TryGetMaterialGroup : Missing material on Cylinder
TryGetMaterialGroup : Missing material on TextMeshPro
```

通常说明某个调试 Marker、扫描点可视化、运行时创建对象或自定义 prefab 缺材质。它不一定会崩溃，但如果对象被频繁创建，会造成日志刷屏和卡顿。

排查建议：

1. 关闭 CTE Debug Marker。
2. 如果仍出现，检查 ECC / ScanPositionOverride / AWO / 地图 AssetBundle 的调试对象。
3. 检查 prefab 的 MeshRenderer / TextMeshPro 是否有合法 Material / FontAsset。
