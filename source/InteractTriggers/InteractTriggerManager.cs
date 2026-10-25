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

internal static class InteractTriggerManager
{
    internal static readonly Dictionary<string, TriggerState> RuleStates = new(StringComparer.OrdinalIgnoreCase);
    internal static int FiredDispatchCount;
    private static readonly Dictionary<string, float> LastRawEventTimes = new(StringComparer.OrdinalIgnoreCase);

    // 大物品状态：只在“放�?-> 拾取”和“拾�?-> 放下”状态变化时触发，避免生�?回放状态误触发�?
    private sealed class BigPickupCycleState
    {
        public bool WaitingForDrop;
        public int CompletedCycles;
    }

    private sealed class BigPickupPlayerSlotState
    {
        public int LastPickupSlot = -1;
        public int LastDropSlot = -1;
    }

    private static readonly Dictionary<string, bool> BigPickupHeldStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> LastBigPickupTransitionTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BigPickupCycleState> BigPickupCycleStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BigPickupPlayerSlotState> BigPickupPlayerSlotStates = new(StringComparer.OrdinalIgnoreCase);

    // 大物品持续状态重复触发只在玩家真实拾�?放下产生过状态边沿后启用，避免关卡初始放置状态直接刷事件�?
    private static readonly HashSet<string> BigPickupRepeatEligibleKeys = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.6：持续触发只处理产生过真实拾�?放下边沿的大物品，避免每�?Tick FindObjectsOfType 全图扫描�?
    private static readonly Dictionary<string, CarryItemPickup_Core> BigPickupRepeatObjects = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.6：持续触发按触发�?Cooldown 到期后才进入匹配/执行路径，避免每 0.2 秒对同一目标重复调用匹配逻辑�?
    private static readonly Dictionary<string, float> BigPickupRepeatNextDueTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RuntimeIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> NextIndexByType = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dumpedTargetIndexes;
    private static float _lastDumpTryTime;

    internal static void Reset()
    {
        RuleStates.Clear();
        FiredDispatchCount = 0;
        LastRawEventTimes.Clear();
        BigPickupHeldStates.Clear();
        LastBigPickupTransitionTimes.Clear();
        BigPickupCycleStates.Clear();
        BigPickupPlayerSlotStates.Clear();
        BigPickupRepeatEligibleKeys.Clear();
        BigPickupRepeatObjects.Clear();
        BigPickupRepeatNextDueTimes.Clear();
        RuntimeIndexes.Clear();
        NextIndexByType.Clear();
        _dumpedTargetIndexes = false;
        _lastDumpTryTime = 0f;
    }

    internal static int GetRuntimeIndex(string sourceKind, Component? source)
    {
        if (source == null) return -1;
        try
        {
            string kind = string.IsNullOrWhiteSpace(sourceKind) ? "Any" : sourceKind.Trim().ToLowerInvariant();
            int instanceId = source.GetInstanceID();
            string key = kind + ":" + instanceId;
            if (RuntimeIndexes.TryGetValue(key, out int existing)) return existing;
            if (!NextIndexByType.TryGetValue(kind, out int next)) next = 0;
            RuntimeIndexes[key] = next;
            NextIndexByType[kind] = next + 1;
            return next;
        }
        catch
        {
            return -1;
        }
    }

    internal static void DumpTargetIndexesIfNeeded()
    {
        if (_dumpedTargetIndexes || !GameStateManager.IsInExpedition) return;
        if (Time.realtimeSinceStartup - _lastDumpTryTime < 3.0f) return;
        _lastDumpTryTime = Time.realtimeSinceStartup;

        int pickups = 0;
        try
        {
            foreach (CarryItemPickup_Core item in UnityEngine.Object.FindObjectsOfType<CarryItemPickup_Core>())
            {
                if (item == null) continue;
                pickups++;
                int runtimeIndex = GetRuntimeIndex("bigpickup", item);
                int spoIndex = -1;
                string spoSource = "Missing:ScanPosOverride.BigPickupItemIndex";
                SpoIndexResolver.TryGetBigPickupSpoIndex(item, out spoIndex, out spoSource);
                BigPickupHeldStates[BuildStableComponentKey("bigpickup", item)] = false;
                Runtime.LogVerbose($"CTE BigPickup SPOIndex={spoIndex} CTERuntimeIndex={runtimeIndex} Source='{spoSource}' Name='{SafeObjectName(item)}' PublicName='{SafeStringMember(item, "PublicName")}' ItemKey='{SafeStringMember(item, "m_itemKey")}' SerialNumber={SafeIntMember(item, "m_serialNumber", "SerialNumber")} SyncID={SafeIntMember(item, "SyncID", "_SyncID_k__BackingField")} Position={SafePosition(item)}");
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"BigPickup index dump failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (pickups > 0)
        {
            _dumpedTargetIndexes = true;
            Runtime.LogVerbose($"CTE target index dump complete. BigPickups={pickups}. Use BigPickup SPOIndex (ScanPosOverride/ECC Item Index, starts at 1) for big item triggers.");
        }
    }

    private static string SafeObjectName(Component source)
    {
        try { return source.gameObject != null ? source.gameObject.name : source.ToString(); } catch { return source.ToString(); }
    }

    private static string SafePosition(Component source)
    {
        try
        {
            Vector3 p = source.transform.position;
            return $"({p.x:F2},{p.y:F2},{p.z:F2})";
        }
        catch { return "<unknown>"; }
    }

    private static int SafeIntMember(object obj, params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? v = p?.GetValue(obj);
                if (v != null && int.TryParse(v.ToString(), out int i)) return i;
                FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                v = f?.GetValue(obj);
                if (v != null && int.TryParse(v.ToString(), out i)) return i;
            }
            catch { }
        }
        return -1;
    }

    private static string SafeStringMember(object obj, params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? v = p?.GetValue(obj);
                if (v != null) return v.ToString() ?? string.Empty;
                FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                v = f?.GetValue(obj);
                if (v != null) return v.ToString() ?? string.Empty;
            }
            catch { }
        }
        return string.Empty;
    }

    private static bool TryGetMinimumInteractRepeatCooldown(string targetType, string triggerMode, out float cooldown)
    {
        cooldown = float.MaxValue;
        bool found = false;
        foreach ((ConfigDocument _config, InteractTriggerRule trigger) in Runtime.GetActiveInteractTriggers())
        {
            if (Runtime.NormalizeTargetType(trigger.TargetType) != targetType) continue;
            if (Runtime.NormalizeInteractionTriggerMode(trigger.TriggerMode) != triggerMode) continue;
            found = true;
            if (trigger.Cooldown < cooldown) cooldown = trigger.Cooldown;
        }
        if (!found) return false;
        if (cooldown == float.MaxValue) cooldown = 1.0f;
        if (cooldown < 1.0f) cooldown = 1.0f;
        return true;
    }

    private static bool IsRepeatDue(Dictionary<string, float> nextDueTimes, string dueKey, float cooldown)
    {
        if (cooldown < 1.0f) cooldown = 1.0f;
        float now = Time.realtimeSinceStartup;
        if (nextDueTimes.TryGetValue(dueKey, out float due) && now < due)
        {
            return false;
        }
        nextDueTimes[dueKey] = now + cooldown;
        return true;
    }

    internal static void ProcessBigPickupRepeatEvents()
    {
        if (!GameStateManager.IsInExpedition) return;

        bool hasRepeatTrigger = false;
        foreach ((ConfigDocument _config, InteractTriggerRule trigger) in Runtime.GetActiveInteractTriggers())
        {
            if (Runtime.NormalizeTargetType(trigger.TargetType) != "bigpickup") continue;
            string mode = Runtime.NormalizeInteractionTriggerMode(trigger.TriggerMode);
            if (mode == "onbigpickupheld" || mode == "onbigpickupplaced")
            {
                hasRepeatTrigger = true;
                break;
            }
        }
        if (!hasRepeatTrigger || BigPickupRepeatObjects.Count == 0) return;

        foreach (KeyValuePair<string, CarryItemPickup_Core> pair in BigPickupRepeatObjects.ToArray())
        {
            string key = pair.Key;
            CarryItemPickup_Core item = pair.Value;
            if (item == null)
            {
                BigPickupRepeatObjects.Remove(key);
                BigPickupRepeatEligibleKeys.Remove(key);
                BigPickupHeldStates.Remove(key);
                BigPickupPlayerSlotStates.Remove(key);
                continue;
            }

            if (!BigPickupRepeatEligibleKeys.Contains(key)) continue;
            if (!BigPickupHeldStates.TryGetValue(key, out bool isHeld)) continue;

            string eventName = isHeld ? "OnBigPickupHeld" : "OnBigPickupPlaced";
            string normalizedMode = Runtime.NormalizeInteractionTriggerMode(eventName);
            if (!TryGetMinimumInteractRepeatCooldown("bigpickup", normalizedMode, out float minCooldown)) continue;
            string suffix = key + "::repeat::" + eventName;
            if (!IsRepeatDue(BigPickupRepeatNextDueTimes, suffix, minCooldown)) continue;
            int playerSlotOverride = GetBigPickupRepeatPlayerSlot(key, isHeld);
            Runtime.FireInteractTrigger("BigPickup", eventName, item, null, "BigPickupRepeat", suffix, playerSlotOverride);
        }
    }

    internal static void OnBigPickupStateChanged(CarryItemPickup_Core? item, ePickupItemStatus status, PlayerAgent? player, string source, bool isRecall = false)
    {
        if (item == null) return;
        if (isRecall) return;

        bool nowHeld = status == ePickupItemStatus.PickedUp;
        if (!TryAcceptBigPickupTransition(item, nowHeld, source)) return;

        string repeatKey = BuildStableComponentKey("bigpickup", item);
        BigPickupRepeatEligibleKeys.Add(repeatKey);
        BigPickupRepeatObjects[repeatKey] = item;
        int playerSlot = Runtime.GetPlayerSlotIndex(player);
        RecordBigPickupPlayerSlot(repeatKey, nowHeld, playerSlot);

        string eventName = nowHeld ? "OnBigPickupPickup" : "OnBigPickupDrop";
        int beforeDispatch = FiredDispatchCount;
        int runtimeIndex = GetRuntimeIndex("BigPickup", item);
        int spoIndex = -1;
        string spoSource = "Missing:ScanPosOverride.BigPickupItemIndex";
        SpoIndexResolver.TryGetBigPickupSpoIndex(item, out spoIndex, out spoSource);
        Runtime.LogVerbose($"Detected BigPickup state change. Event={eventName}, Source={source}, SPOIndex={spoIndex}, CTERuntimeIndex={runtimeIndex}, SPOSource='{spoSource}', Name='{SafeObjectName(item)}'");
        Fire("BigPickup", eventName, item, player, source, useStableStateKey: true, playerSlotOverride: playerSlot);
        if (FiredDispatchCount == beforeDispatch)
        {
            Runtime.LogVerbose($"BigPickup state changed but no InteractTrigger matched. SPOIndex={spoIndex}, CTERuntimeIndex={runtimeIndex}, Serial={SafeIntMember(item, "m_serialNumber", "SerialNumber")}, ItemKey='{SafeStringMember(item, "m_itemKey")}', PublicName='{SafeStringMember(item, "PublicName")}', Source={source}");
        }

        HandleBigPickupPickupDropCycle(item, player, source, nowHeld);
    }

    private static void RecordBigPickupPlayerSlot(string key, bool nowHeld, int playerSlot)
    {
        if (!BigPickupPlayerSlotStates.TryGetValue(key, out BigPickupPlayerSlotState? state))
        {
            state = new BigPickupPlayerSlotState();
            BigPickupPlayerSlotStates[key] = state;
        }

        int effectiveSlot = playerSlot;
        if (!nowHeld && effectiveSlot < 0)
        {
            effectiveSlot = state.LastPickupSlot;
        }

        if (effectiveSlot < 0 || effectiveSlot > 3)
        {
            return;
        }

        if (nowHeld)
        {
            state.LastPickupSlot = effectiveSlot;
        }
        else
        {
            state.LastDropSlot = effectiveSlot;
        }
    }

    private static int GetBigPickupRepeatPlayerSlot(string key, bool isHeld)
    {
        if (!BigPickupPlayerSlotStates.TryGetValue(key, out BigPickupPlayerSlotState? state))
        {
            return -1;
        }

        return isHeld ? state.LastPickupSlot : state.LastDropSlot;
    }

    private static void HandleBigPickupPickupDropCycle(CarryItemPickup_Core item, PlayerAgent? player, string source, bool nowHeld)
    {
        string key = BuildStableComponentKey("bigpickup", item);
        if (!BigPickupCycleStates.TryGetValue(key, out BigPickupCycleState? cycle))
        {
            cycle = new BigPickupCycleState();
            BigPickupCycleStates[key] = cycle;
        }

        if (nowHeld)
        {
            cycle.WaitingForDrop = true;
            return;
        }

        if (!cycle.WaitingForDrop)
        {
            return;
        }

        cycle.WaitingForDrop = false;
        cycle.CompletedCycles++;
        int cycleSpoIndex = -1;
        SpoIndexResolver.TryGetBigPickupSpoIndex(item, out cycleSpoIndex, out _);
        Runtime.LogVerbose($"BigPickup pickup/drop behavior cycle completed. SPOIndex={cycleSpoIndex}, CTERuntimeIndex={GetRuntimeIndex("BigPickup", item)}, Cycles={cycle.CompletedCycles}, Source={source}");
        Runtime.FireBigPickupCycleEvents(item, player, source, cycle.CompletedCycles);
    }

    private static bool TryAcceptBigPickupTransition(CarryItemPickup_Core item, bool nowHeld, string source)
    {
        string key = BuildStableComponentKey("bigpickup", item);
        float now = Time.realtimeSinceStartup;

        if (!BigPickupHeldStates.ContainsKey(key))
        {

    // 初次观察到“放置在关卡中”通常是生�?同步回放，不应触发放下事件�?
    // 但初次观察到 PickedUp 是玩家真实拾取边沿，必须放行，否则第一次拾取会被吞掉�?
    if (!nowHeld)
            {
                BigPickupHeldStates[key] = false;
                return false;
            }
        }
        else if (BigPickupHeldStates.TryGetValue(key, out bool wasHeld))
        {
            if (wasHeld == nowHeld)
            {
                return false;
            }
        }
        else
        {

    // 初次观察到“放置在关卡中”通常是生�?同步回放，不应触发放下事件�?
    if (!nowHeld)
            {
                BigPickupHeldStates[key] = false;
                return false;
            }
        }

        string transitionKey = key + ":" + (nowHeld ? "pickup" : "drop");
        if (LastBigPickupTransitionTimes.TryGetValue(transitionKey, out float last) && now - last < 0.20f)
        {
            return false;
        }

        BigPickupHeldStates[key] = nowHeld;
        LastBigPickupTransitionTimes[transitionKey] = now;
        return true;
    }

    private static object? TryGetObjectMember(object obj, params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? v = p?.GetValue(obj);
                if (v != null) return v;
                FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                v = f?.GetValue(obj);
                if (v != null) return v;
            }
            catch { }
        }
        return null;
    }

    private static string BuildStableComponentKey(string targetType, Component source)
    {
        try { return targetType + ":" + source.GetInstanceID(); }
        catch { return targetType + ":" + source.GetHashCode(); }
    }

    private static void Fire(string targetType, string eventName, Component source, PlayerAgent? player, string sourceLabel, bool useStableStateKey = false, int playerSlotOverride = -1)
    {
        try
        {
            string sourceKey = BuildStableComponentKey(targetType, source);
            string rawKey = sourceKey + ":" + eventName + ":" + (player != null ? player.GetInstanceID() : -1);
            if (LastRawEventTimes.TryGetValue(rawKey, out float last) && Time.realtimeSinceStartup - last < 0.15f)
            {
                return;
            }
            LastRawEventTimes[rawKey] = Time.realtimeSinceStartup;

            int runtimeIndex = GetRuntimeIndex(targetType, source);
            string sourceName = BuildSourceName(source, sourceLabel) + $" Index={runtimeIndex}";
            string suffix = useStableStateKey ? rawKey : rawKey + ":" + Mathf.FloorToInt(Time.realtimeSinceStartup * 2f);
            Runtime.FireInteractTrigger(targetType, eventName, source, player, sourceName, suffix, playerSlotOverride);
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Interact trigger dispatch failed from {sourceLabel}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildSourceName(Component source, string sourceLabel)
    {
        try
        {
            string name = source.gameObject != null ? source.gameObject.name : source.ToString();
            int id = source.GetInstanceID();
            return $"{sourceLabel}:{name}#{id}";
        }
        catch
        {
            return sourceLabel;
        }
    }
}

[HarmonyPatch(typeof(CarryItemPickup_Core), "OnSyncStateChange")]
internal static class CarryItemPickup_Core_OnSyncStateChange_CTEPatch
{
    private static void Postfix(CarryItemPickup_Core __instance, ePickupItemStatus status, pPickupPlacement placement, PlayerAgent player, bool isRecall)
    {
        InteractTriggerManager.OnBigPickupStateChanged(__instance, status, player, "CarryItemPickup_Core.OnSyncStateChange", isRecall);
    }
}

[HarmonyPatch(typeof(CarryItemPickup_Core), "OnInteractionPickUp")]
internal static class CarryItemPickup_Core_OnInteractionPickUp_CTEPatch
{
    private static void Postfix(CarryItemPickup_Core __instance, PlayerAgent player)
    {
        InteractTriggerManager.OnBigPickupStateChanged(__instance, ePickupItemStatus.PickedUp, player, "CarryItemPickup_Core.OnInteractionPickUp");
    }
}


[HarmonyPatch(typeof(CarryItemPickup_Core), "OnPickedUp")]
internal static class CarryItemPickup_Core_OnPickedUp_CTEPatch
{
    private static void Postfix(CarryItemPickup_Core __instance, PlayerAgent player, InventorySlot slot, AmmoType ammoType)
    {
        InteractTriggerManager.OnBigPickupStateChanged(__instance, ePickupItemStatus.PickedUp, player, "CarryItemPickup_Core.OnPickedUp");
    }
}

[HarmonyPatch(typeof(CarryItemPickup_Core), "OnPickUp")]
internal static class CarryItemPickup_Core_OnPickUp_CTEPatch
{
    private static void Postfix(CarryItemPickup_Core __instance, PlayerAgent player)
    {
        InteractTriggerManager.OnBigPickupStateChanged(__instance, ePickupItemStatus.PickedUp, player, "CarryItemPickup_Core.OnPickUp");
    }
}

[HarmonyPatch(typeof(CarryItemPickup_Core), "OnPlacedInLevel")]
internal static class CarryItemPickup_Core_OnPlacedInLevel_CTEPatch
{
    private static void Postfix(CarryItemPickup_Core __instance, pPickupPlacement placement, PlayerAgent player, ItemCuller culler)
    {
        InteractTriggerManager.OnBigPickupStateChanged(__instance, ePickupItemStatus.PlacedInLevel, player, "CarryItemPickup_Core.OnPlacedInLevel");
    }
}






