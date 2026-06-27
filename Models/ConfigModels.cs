using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using ChainedPuzzles;
using GameData;
using Gear;
using GTFO.API;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Localization;
using Player;
using SNetwork;
using UnityEngine;

namespace CoordinateTriggerEvents;

[StructLayout(LayoutKind.Sequential)]
internal sealed class ConfigDocument
{
    public string FilePath = string.Empty;
    public bool Enabled = true;
    public List<JsonElement> MainLevelLayoutIDs = new();
    public DebugOptions Debug = new();
    public List<PositionTriggerRule> PositionTriggers = new();
    public List<ScanTriggerRule> ScanTriggers = new();
    public List<InteractTriggerRule> InteractTriggers = new();
    public List<HudInteractTriggerRule> HudInteractTriggers = new();
}

internal sealed class ScanTriggerRule
{
    // ID is the unique trigger key used by logs, runtime lookup, and CTE custom event Type=700 control.
    // 旧版 Name 字段已删除；新配置必须填写 ID。
    public string ID { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int PuzzleOverrideIndex { get; set; } = -1;
    public string TriggerMode { get; set; } = "OnScanActivated";
    public bool UsePlayerCountEvents { get; set; } = false;
    public List<JsonElement> OnePlayerEvents { get; set; } = new();
    public List<JsonElement> TwoPlayerEvents { get; set; } = new();
    public List<JsonElement> ThreePlayerEvents { get; set; } = new();
    public List<JsonElement> FourPlayerEvents { get; set; } = new();
    public float Cooldown { get; set; } = 0.0f;
    public bool RequireInExpedition { get; set; } = true;
    // 扫描点触发器安全条件：true 时要求关卡内至少存在一名存活玩家才执行事件。
    // 适用于 OnScanActivated 与 OnPlayerExitScan，避免关卡加载/同步阶段无人存活时误触发。
    public bool RequireAlivePlayers { get; set; } = true;
    // 扫描点行为组：OnScanActivated / OnPlayerExitScan 每成功触发一次算 1 组；累计 TriggerCycleCount 组后额外执行 TriggerCycleEvents。
    public bool UseTriggerCycleEvents { get; set; } = false;
    public int TriggerCycleCount { get; set; } = 3;
    public List<JsonElement> TriggerCycleEvents { get; set; } = new();
    public List<JsonElement> Events { get; set; } = new();
    public List<JsonElement> WardenEvents { get; set; } = new();
}

internal sealed class ScanRuntimeState
{
    public int LastPlayerCount = 0;
    public bool HasObserved;
    public bool Fired;
    public float LastFireTime = -999999f;
    public bool IsActive;
    public bool ActivatedThisCycle;
    public bool ActivationPlayerCountEventsFired;
    public bool HadPlayersInside;
    public bool ExitTriggeredThisCycle;
    // 全员扫描点状态：用于 OnAllPlayersEnterScan / OnAllPlayersInsideScan / OnAllPlayersExitScan / OnAllPlayersExitedScan。
    // AllPlayersEnteredThisCycle 只在所有存活玩家至少进入过一次扫描点后置 true；
    // AllPlayersExitedRepeatActive 只有在“全员进入 -> 全员退出”之后才允许持续退出事件，避免初始无人时误触发。
    public bool AllPlayersInsideNow;
    public bool AllPlayersEnteredThisCycle;
    public bool AllPlayersExitedRepeatActive;
    public int ActivationEdgeSequence;
    public int ExitEdgeSequence;
    public int AllPlayersEnterEdgeSequence;
    public int AllPlayersExitEdgeSequence;
}

internal sealed class InteractTriggerRule
{
    // ID is the unique trigger key used by logs, runtime lookup, and CTE custom event Type=700 control.
    // 旧版 Name 字段已删除；新配置必须填写 ID。
    public string ID { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string TargetType { get; set; } = "Any";
    public string TriggerMode { get; set; } = string.Empty;
    public float Cooldown { get; set; } = 0.0f;
    public bool RequireInExpedition { get; set; } = true;
    public int Index { get; set; } = -1;
    public int InstanceID { get; set; } = -1;
    public int SyncID { get; set; } = -1;
    public int SerialNumber { get; set; } = -1;
    public string ItemKey { get; set; } = string.Empty;
    public string PublicName { get; set; } = string.Empty;
    public string TerminalSerial { get; set; } = string.Empty;
    public string WorldEventObjectFilter { get; set; } = string.Empty;
    public uint DataBlockID { get; set; } = 0u;
    public uint ItemID { get; set; } = 0u;
    public string InternalName { get; set; } = string.Empty;
    public PositionData? Position { get; set; }
    public float Radius { get; set; } = 2.0f;
    public bool UseTriggerArea { get; set; } = false;
    public PositionTriggerRule? InteractTriggerArea { get; set; }
    public List<JsonElement> OnePlayerTriggerEvents { get; set; } = new();
    public List<JsonElement> TwoPlayerTriggerEvents { get; set; } = new();
    public List<JsonElement> ThreePlayerTriggerEvents { get; set; } = new();
    public List<JsonElement> FourPlayerTriggerEvents { get; set; } = new();
    // 大物品玩家行为组：拾取 -> 放下 算一组完整行为，可在累计 N 组后执行额外事件组。
    public bool UsePickupDropCycleEvents { get; set; } = false;
    public int PickupDropCycleCount { get; set; } = 3;
    public List<JsonElement> PickupDropCycleEvents { get; set; } = new();
    public List<JsonElement> Events { get; set; } = new();
    public List<JsonElement> WardenEvents { get; set; } = new();
}

internal sealed class HudInteractTriggerRule
{
    public string ID { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public PositionData? Position { get; set; }
    public float Radius { get; set; } = 2.0f;
    public string HudText { get; set; } = "Interact";
    public string ProgressText { get; set; } = string.Empty;
    public float HoldTime { get; set; } = 2.0f;
    public float Cooldown { get; set; } = 1.0f;
    public bool RequireInExpedition { get; set; } = true;
    public bool RequireAlivePlayer { get; set; } = true;
    public bool HostValidateDistance { get; set; } = true;
    public bool DebugVisible { get; set; } = false;
    public string DebugColor { get; set; } = "#00BFFF";
    public float DebugAlpha { get; set; } = 0.35f;
    public bool DebugLabel { get; set; } = true;
    public List<HudInteractProgressEventRule> ProgressEvents { get; set; } = new();
    public List<JsonElement> CancelEvents { get; set; } = new();
    public List<JsonElement> Events { get; set; } = new();
    public List<JsonElement> WardenEvents { get; set; } = new();
}

internal sealed class HudInteractProgressEventRule
{
    public float Progress { get; set; }
    public List<JsonElement> Events { get; set; } = new();
}

internal sealed class DebugOptions
{
    public bool Enabled = false;
    public bool ShowScanMarkers = true;
    public bool ShowNames = true;
    public string MarkerColor = "#00BFFF";
    public string LabelColor = "#FFFFFF";
    public float MarkerAlpha = 0.35f;
    public float HeightOffset = 0.05f;
    public float MarkerHeight = 0.025f;
    public float LabelHeightOffset = 1.0f;
    public float RadiusScale = 1.0f;
    public float MinimumRadius = 0.5f;
    public float RefreshInterval = 1.0f;
    public bool DumpRuntimeIndexes = false;
}

internal sealed class PositionTriggerRule
{
    // ID is the unique trigger key used by logs, runtime lookup, and CTE custom event Type=700 control.
    // 旧版 Name 字段已删除；新配置必须填写 ID。
    public string ID { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public PositionData? Position { get; set; }
    // TriggerAreaMode:
    //   Radius          = 保留旧版半径球形触发范围。
    //   OverrideBigZone = 覆盖指定大区，使用 LocalIndex 对应官图/WardenObjectiveEventData 的 LocalIndex。
    //   OverrideArea    = 覆盖指定小区，使用 Count 对应 AWO/WEE 常用的 Area 编号写法。
    public string TriggerAreaMode { get; set; } = "Radius";
    public float Radius { get; set; } = 3.0f;
    public int LocalIndex { get; set; } = -1;
    public int Count { get; set; } = -1;
    public string Layer { get; set; } = string.Empty;
    public int DimensionIndex { get; set; } = -1;
    public string TriggerMode { get; set; } = "AnyPlayerEnter";
    // UsePlayerCountEvents:
    //   false = 不启用 1/2/3/4 人事件组，始终触发通用 Events/WardenEvents。
    //   true  = 根据触发范围内玩家数量执行 One/Two/Three/FourPlayerEvents。
    public bool UsePlayerCountEvents { get; set; } = false;
    // 位置触发器行为组：触发一次事件触发器算一组，可在累计 N 组后执行额外事件组。
    public bool UseTriggerCycleEvents { get; set; } = false;
    public int TriggerCycleCount { get; set; } = 3;
    public List<JsonElement> TriggerCycleEvents { get; set; } = new();
    public List<JsonElement> OnePlayerEvents { get; set; } = new();
    public List<JsonElement> TwoPlayerEvents { get; set; } = new();
    public List<JsonElement> ThreePlayerEvents { get; set; } = new();
    public List<JsonElement> FourPlayerEvents { get; set; } = new();
    public float Cooldown { get; set; } = 0.0f;
    public bool RequireInExpedition { get; set; } = true;
    public bool RequireAlivePlayers { get; set; } = true;
    public bool IncludeBots { get; set; } = true;
    public bool DebugVisible { get; set; } = true;
    public string DebugColor { get; set; } = string.Empty;
    public List<JsonElement> Events { get; set; } = new();
    public List<JsonElement> WardenEvents { get; set; } = new();
}

internal sealed class PositionData
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }

    public Vector3 ToVector3() => new Vector3(x, y, z);
}

internal sealed class TriggerState
{
    public bool WasInside;
    public bool Fired;
    public float LastFireTime = -999999f;
    public int LastInsidePlayerCount;
    public readonly HashSet<int> FiredPlayerCounts = new();
    public int CompletedCycles;
}
