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

internal static class SpoIndexResolver
{
    private static Type? FindType(params string[] fullNames)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (string name in fullNames)
            {
                try
                {
                    Type? t = asm.GetType(name, false, true);
                    if (t != null) return t;
                }
                catch { }
            }
        }
        return null;
    }

    private static object? GetCurrent(string typeName)
    {
        Type? t = FindType(typeName, "ScanPosOverride." + typeName.Split('.').Last());
        if (t == null) return null;
        try
        {
            FieldInfo? f = t.GetField("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object? v = f?.GetValue(null);
            if (v != null) return v;
        }
        catch { }
        try
        {
            PropertyInfo? p = t.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object? v = p?.GetValue(null);
            if (v != null) return v;
        }
        catch { }
        return null;
    }

    public static bool TryGetScanSpoIndex(CP_Bioscan_Core scan, out int index, out string source)
    {
        index = -1;
        source = "Missing:ScanPosOverride.PuzzleOverrideIndex";
        if (scan == null) return false;
        object? mgr = GetCurrent("ScanPosOverride.Managers.PuzzleOverrideManager");
        if (mgr == null) return false;

        try
        {
            MethodInfo? m = mgr.GetType().GetMethod("GetBioscanCoreOverrideIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(CP_Bioscan_Core) }, null);
            object? raw = m?.Invoke(mgr, new object[] { scan });
            if (raw != null && uint.TryParse(raw.ToString(), out uint u) && u > 0)
            {
                index = checked((int)u);
                source = "ScanPosOverride.PuzzleOverrideManager.GetBioscanCoreOverrideIndex";
                return true;
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"SPO scan index reflection failed: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    public static bool TryGetBigPickupSpoIndex(Component? component, out int index, out string source)
    {
        index = -1;
        source = "Missing:ScanPosOverride.BigPickupItemIndex";
        if (component == null) return false;

        CarryItemPickup_Core? item = component.TryCast<CarryItemPickup_Core>();
        if (item == null)
        {
            try { item = component.GetComponent<CarryItemPickup_Core>(); } catch { }
        }
        if (item == null) return false;

        object? mgr = GetCurrent("ScanPosOverride.Managers.PuzzleReqItemManager");
        if (mgr == null) return false;

        // 首选读取 ECC/SPO 私有字典 BigPickupItemsInLevel：Dictionary<int, CarryItemPickup_Core>
        try
        {
            FieldInfo? f = mgr.GetType().GetField("BigPickupItemsInLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? dict = f?.GetValue(mgr);
            if (dict is IEnumerable enumerable)
            {
                int targetInstance = SafeInstanceId(item);
                foreach (object entry in enumerable)
                {
                    if (entry == null) continue;
                    object? key = entry.GetType().GetProperty("Key")?.GetValue(entry);
                    object? value = entry.GetType().GetProperty("Value")?.GetValue(entry);
                    if (key == null || value == null) continue;
                    if (!int.TryParse(key.ToString(), out int k) || k <= 0) continue;

                    Component? valueComp = value as Component;
                    if (valueComp == null && value is CarryItemPickup_Core core) valueComp = core;
                    if (valueComp == null) continue;

                    if (ReferenceEquals(valueComp, item) || SafeInstanceId(valueComp) == targetInstance)
                    {
                        index = k;
                        source = "ScanPosOverride.PuzzleReqItemManager.BigPickupItemsInLevel";
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"SPO BigPickup dictionary reflection failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 兼容 fallback：尝试调用 GetBigPickupItem(1..512) 查找同一对象。
        try
        {
            MethodInfo? m = mgr.GetType().GetMethod("GetBigPickupItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (m != null)
            {
                int targetInstance = SafeInstanceId(item);
                for (int i = 1; i <= 512; i++)
                {
                    object? raw = m.Invoke(mgr, new object[] { i });
                    Component? c = raw as Component;
                    if (c == null && raw is CarryItemPickup_Core core) c = core;
                    if (c != null && (ReferenceEquals(c, item) || SafeInstanceId(c) == targetInstance))
                    {
                        index = i;
                        source = "ScanPosOverride.PuzzleReqItemManager.GetBigPickupItem";
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"SPO BigPickup GetBigPickupItem reflection failed: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static int SafeInstanceId(Component c)
    {
        try { return c.GetInstanceID(); } catch { return c.Pointer.GetHashCode(); }
    }
}

