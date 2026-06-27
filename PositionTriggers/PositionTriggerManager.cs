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

internal static partial class Runtime
{
    private static void EvaluateTrigger(ConfigDocument config, PositionTriggerRule trigger)
    {
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition)
        {
            return;
        }

        // 1.2.0: CTE Events are Host-only by default.
        // Non-host clients may still observe local trigger state, but configured Events/WardenEvents are never executed on clients.
        string key = config.FilePath + "::" + trigger.ID;
        if (!States.TryGetValue(key, out TriggerState? state))
        {
            state = new TriggerState();
            States[key] = state;
        }

        if (trigger.Cooldown > 0f && Time.realtimeSinceStartup - state.LastFireTime < trigger.Cooldown)
        {
            return;
        }

        int insidePlayerCount = CountPlayersInsideTriggerArea(trigger);
        int previousInsidePlayerCount = state.LastInsidePlayerCount;
        bool inside = insidePlayerCount > 0;
        bool wasInside = state.WasInside;
        bool playerCountEventsEnabled = trigger.UsePlayerCountEvents;
        string mode = NormalizePositionTriggerMode(trigger.TriggerMode);

        if ((mode == "anyplayerinside" || mode == "allplayersinside") && trigger.Cooldown < 1.0f)
        {
            trigger.Cooldown = 1.0f;
        }

        if (playerCountEventsEnabled)
        {
            bool shouldFireCount = false;
            int eventPlayerCount = insidePlayerCount;
            int eligiblePlayers = CountEligiblePlayers(trigger);

            switch (mode)
            {
                case "anyplayerinside":
                    shouldFireCount = inside;
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "allplayersinside":
                    shouldFireCount = eligiblePlayers > 0 && insidePlayerCount >= eligiblePlayers;
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "allplayersexit":
                    shouldFireCount = wasInside && !inside;
                    eventPlayerCount = Math.Max(1, previousInsidePlayerCount);
                    break;

                case "anyplayerexit":
                    // 退出触发：任意玩家离开范围都会触发。
                    // UsePlayerCountEvents=true 时使用“离开前的人数”选择 PlayerCountEvents，
                    // 与 OnPlayerExitScan 的退出前人数语义保持一致。
                    shouldFireCount = previousInsidePlayerCount > insidePlayerCount;
                    eventPlayerCount = Math.Max(1, previousInsidePlayerCount);
                    break;

                case "allplayersenter":
                    shouldFireCount = !wasInside && eligiblePlayers > 0 && insidePlayerCount >= eligiblePlayers;
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "anyplayerenter":
                default:
                    shouldFireCount = insidePlayerCount > previousInsidePlayerCount && insidePlayerCount >= 1;
                    eventPlayerCount = insidePlayerCount;
                    break;
            }

            state.WasInside = inside;
            state.LastInsidePlayerCount = insidePlayerCount;

            if (!shouldFireCount || eventPlayerCount < 1 || eventPlayerCount > 4)
            {
                return;
            }

            FireTriggerForPlayerCount(trigger, state, eventPlayerCount);
            return;
        }

        bool shouldFire;
        int eligible = CountEligiblePlayers(trigger);
        switch (mode)
        {
            case "anyplayerinside":
                shouldFire = inside;
                break;

            case "allplayersinside":
                shouldFire = eligible > 0 && insidePlayerCount >= eligible;
                break;

            case "allplayersenter":
                shouldFire = !wasInside && eligible > 0 && insidePlayerCount >= eligible;
                break;

            case "allplayersexit":
                shouldFire = wasInside && !inside;
                break;

            case "anyplayerexit":
                shouldFire = previousInsidePlayerCount > insidePlayerCount;
                break;

            case "anyplayerenter":
            default:
                shouldFire = !wasInside && inside;
                break;
        }

        state.WasInside = inside;
        state.LastInsidePlayerCount = insidePlayerCount;

        if (!shouldFire)
        {
            return;
        }

        FireTrigger(trigger, state);
    }


    internal static string NormalizePositionTriggerMode(string text)
    {
        return GetCachedNormalization(PositionModeNormalizationCache, text, static value =>
        {
                string normalized = (value ?? string.Empty)
                    .Trim()
                    .Replace("_", string.Empty)
                    .Replace("-", string.Empty)
                    .Replace(" ", string.Empty)
                    .ToLowerInvariant();

                return normalized switch
                {
                    "inside" => "anyplayerinside",
                    "anyplayerinside" => "anyplayerinside",
                    "playerinside" => "anyplayerinside",
                    "onplayerinside" => "anyplayerinside",

                    "allinside" => "allplayersinside",
                    "allplayerinside" => "allplayersinside",
                    "allplayersinside" => "allplayersinside",

                    "enter" => "anyplayerenter",
                    "onenter" => "anyplayerenter",
                    "playerenter" => "anyplayerenter",
                    "playerentered" => "anyplayerenter",
                    "anyplayerenter" => "anyplayerenter",
                    "onplayerenter" => "anyplayerenter",
                    "anyplayerentered" => "anyplayerenter",

                    "allenter" => "allplayersenter",
                    "allplayerenter" => "allplayersenter",
                    "allplayersenter" => "allplayersenter",
                    "allplayersentered" => "allplayersenter",

                    "exit" => "anyplayerexit",
                    "leave" => "anyplayerexit",
                    "left" => "anyplayerexit",
                    "onexit" => "anyplayerexit",
                    "onleave" => "anyplayerexit",
                    "playerexit" => "anyplayerexit",
                    "playerleave" => "anyplayerexit",
                    "playerleft" => "anyplayerexit",
                    "playerexited" => "anyplayerexit",
                    "anyplayerexit" => "anyplayerexit",
                    "anyplayerleave" => "anyplayerexit",
                    "anyplayerleft" => "anyplayerexit",
                    "anyplayerexited" => "anyplayerexit",
                    "onplayerexit" => "anyplayerexit",
                    "onplayerleave" => "anyplayerexit",
                    "onplayerleft" => "anyplayerexit",
                    "onplayerexited" => "anyplayerexit",
                    "onplayerexitarea" => "anyplayerexit",
                    "onplayerleavearea" => "anyplayerexit",

                    "allexit" => "allplayersexit",
                    "allleave" => "allplayersexit",
                    "allleft" => "allplayersexit",
                    "allplayerexit" => "allplayersexit",
                    "allplayerleave" => "allplayersexit",
                    "allplayersleft" => "allplayersexit",
                    "allplayersexit" => "allplayersexit",
                    "allplayersleave" => "allplayersexit",
                    "onallplayersexit" => "allplayersexit",
                    "onallplayersleave" => "allplayersexit",

                    _ => "anyplayerenter"
                };
    
        });
    }

    private static bool IsTriggerInside(PositionTriggerRule trigger)
    {
        return CountPlayersInsideTriggerArea(trigger) > 0;
    }

    private static bool AreAllPlayersInside(PositionTriggerRule trigger)
    {
        int eligible = CountEligiblePlayers(trigger);
        return eligible > 0 && CountPlayersInsideTriggerArea(trigger) >= eligible;
    }

    private static int CountEligiblePlayers(PositionTriggerRule trigger)
    {
        int count = 0;
        foreach (PlayerAgent _ in EnumeratePlayers(trigger)) count++;
        return count;
    }

    private static int CountPlayersInsideTriggerArea(PositionTriggerRule trigger)
    {
        int count = 0;
        foreach (PlayerAgent agent in EnumeratePlayers(trigger))
        {
            if (IsPlayerInsideTriggerArea(agent, trigger))
            {
                count++;
            }
        }
        return count;
    }

    private static bool IsPlayerInsideTriggerArea(PlayerAgent agent, PositionTriggerRule trigger)
    {
        string mode = (trigger.TriggerAreaMode ?? "Radius").Trim().ToLowerInvariant();
        if (mode == "overridebigzone" || mode == "bigzone" || mode == "zone")
        {
            return IsPlayerInsideOverrideBigZone(agent, trigger);
        }
        if (mode == "overridearea" || mode == "area")
        {
            return IsPlayerInsideOverrideArea(agent, trigger);
        }

        if (trigger.Position == null)
        {
            LogThrottled($"Coordinate trigger '{trigger.ID}' uses Radius mode but Position is missing.");
            return false;
        }

        Vector3 center = trigger.Position.ToVector3();
        float radiusSqr = Math.Max(0.01f, trigger.Radius * trigger.Radius);
        return (agent.Position - center).sqrMagnitude <= radiusSqr;
    }

    private static bool IsPlayerInsideOverrideBigZone(PlayerAgent agent, PositionTriggerRule trigger)
    {
        // OverrideBigZone 必须严格绑定到配置中的 LocalIndex 对应 LG_Zone。
        // 不允许退回 Position/Radius，也不允许在找不到 Zone 时视为全图触发。
        if (trigger.LocalIndex < 0)
        {
            LogThrottled($"Coordinate trigger '{trigger.ID}' uses TriggerAreaMode=OverrideBigZone but LocalIndex is missing. Radius fallback is not used.");
            return false;
        }

        if (!TryGetCachedZones(trigger, out List<LG_Zone> zones, out string failure))
        {
            if (!IsTransientLookupFailure(failure))
            {
                LogThrottled($"Coordinate trigger '{trigger.ID}' OverrideBigZone unresolved: {failure}");
            }
            return false;
        }

        foreach (LG_Zone zone in zones)
        {
            if (PlayerMatchesAwoZone(agent, zone)) return true;
        }
        return false;
    }

    private static bool IsPlayerInsideOverrideArea(PlayerAgent agent, PositionTriggerRule trigger)
    {
        if (trigger.Count < 0)
        {
            LogThrottled($"Coordinate trigger '{trigger.ID}' uses TriggerAreaMode=OverrideArea but Count is missing. Count should match the AWO-style area index, for example Count=0.");
            return false;
        }

        if (!TryGetCachedAreas(trigger, out List<(LG_Zone Zone, object Area, int Index)> areas, out string failure))
        {
            if (!IsTransientLookupFailure(failure))
            {
                LogThrottled($"Coordinate trigger '{trigger.ID}' OverrideArea unresolved: {failure}");
            }
            return false;
        }

        // 0.9.5 修复：不要仅凭 CourseNode / node.m_area 判定 OverrideArea 命中。
        // GTFO 在门口或 Area 交界处可能会提前把玩家的 CourseNode 切到下一个 Area，
        // 这会导致玩家站在通往 Area_B 的铁门面前就被误判为已进入 Area_B。
        // 因此 OverrideArea 的命中必须以玩家物理位置落入目标 Area 的有效水平范围为准。
        foreach ((LG_Zone Zone, object Area, int Index) item in areas)
        {
            if (IsPositionInsideAreaByReflection(item.Area, agent.Position)) return true;
        }

        return false;
    }

    private static bool ZoneMatchesTrigger(LG_Zone zone, PositionTriggerRule trigger)
    {
        try
        {
            if ((int)zone.LocalIndex != trigger.LocalIndex) return false;
            if (trigger.DimensionIndex >= 0 && (int)zone.DimensionIndex != trigger.DimensionIndex) return false;
            if (!string.IsNullOrWhiteSpace(trigger.Layer))
            {
                int zoneLayer = TryGetLayerIndex(zone);
                if (!LayerMatches(zoneLayer, trigger.Layer)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool AreaMatchesTrigger(object area, PositionTriggerRule trigger)
    {
        // OverrideArea 的 Count 使用 GTFO LG_Zone.m_areas 内部顺序：
        // Count=0 表示该 Zone 的 A 区，Count=1 表示 B 区，以此类推。
        if (trigger.Count < 0) return false;

        try
        {
            if (!TryGetCachedAreas(trigger, out List<(LG_Zone Zone, object Area, int Index)> areas, out _))
            {
                return false;
            }

            foreach ((LG_Zone Zone, object Area, int Index) item in areas)
            {
                if (ReferenceEquals(item.Area, area) || Equals(item.Area, area)) return true;
            }
        }
        catch { }

        return false;
    }
    private static bool AreaMatchesCachedArea(object area, List<(LG_Zone Zone, object Area, int Index)> areas)
    {
        try
        {
            foreach ((LG_Zone Zone, object Area, int Index) item in areas)
            {
                if (ReferenceEquals(item.Area, area) || Equals(item.Area, area)) return true;
            }
        }
        catch { }

        return false;
    }

    private static bool PlayerCourseNodeMatchesCachedArea(object node, List<(LG_Zone Zone, object Area, int Index)> areas)
    {
        try
        {
            object? nodeArea = TryGetObjectMember(node, "m_area", "Area", "area", "m_areaData", "LG_Area", "lgArea");
            if (nodeArea != null && AreaMatchesCachedArea(nodeArea, areas)) return true;

            foreach ((LG_Zone Zone, object Area, int Index) item in areas)
            {
                if (AreaContainsCourseNode(item.Area, node)) return true;
            }
        }
        catch { }

        return false;
    }

    private static bool AreaContainsCourseNode(object area, object node)
    {
        object? nodes = TryGetObjectMember(
            area,
            "m_courseNodes", "CourseNodes", "m_courseNodeList", "CourseNodeList",
            "m_nodes", "Nodes", "nodes", "m_navNodes", "NavNodes", "m_nodeList", "NodeList"
        );
        if (ObjectCollectionContains(nodes, node)) return true;

        // 某些 GTFO/LG_Area 实现不会暴露节点列表，但会在 CourseNode 上保存 Area 引用。
        // 上层已先尝试 node.m_area；这里保持空安全，不做跨 Area 推断。
        return false;
    }


    private static bool TryGetCachedZones(PositionTriggerRule trigger, out List<LG_Zone> zones, out string failure)
    {
        string key = BuildZoneCacheKey("zone", trigger);
        ZoneLookupCacheEntry entry = GetOrRefreshZoneLookupCache(key, trigger, includeAreas: false);
        zones = entry.Zones;
        failure = entry.LastFailure;
        return entry.Resolved && zones.Count > 0;
    }

    private static bool TryGetCachedAreas(PositionTriggerRule trigger, out List<(LG_Zone Zone, object Area, int Index)> areas, out string failure)
    {
        string key = BuildZoneCacheKey("area", trigger);
        ZoneLookupCacheEntry entry = GetOrRefreshZoneLookupCache(key, trigger, includeAreas: true);
        areas = entry.Areas;
        failure = entry.LastFailure;
        return entry.Resolved && areas.Count > 0;
    }

    private static ZoneLookupCacheEntry GetOrRefreshZoneLookupCache(string key, PositionTriggerRule trigger, bool includeAreas)
    {
        if (!ZoneLookupCache.TryGetValue(key, out ZoneLookupCacheEntry? entry))
        {
            entry = new ZoneLookupCacheEntry();
            ZoneLookupCache[key] = entry;
        }

        if (entry.Resolved) return entry;
        if (Time.realtimeSinceStartup - entry.LastAttemptTime < 1.5f) return entry;

        entry.LastAttemptTime = Time.realtimeSinceStartup;
        entry.Zones.Clear();
        entry.Areas.Clear();
        entry.LastFailure = string.Empty;

        if (!TryGetAwoZone(trigger, out LG_Zone? awoZone, out string zoneFailure))
        {
            entry.LastFailure = zoneFailure;
            return entry;
        }

        if (awoZone == null)
        {
            entry.LastFailure = "AWO-style zone lookup returned null zone.";
            return entry;
        }

        entry.Zones.Add(awoZone);
        if (includeAreas)
        {
            foreach ((object Candidate, int Index) item in EnumerateZoneAreas(awoZone))
            {
                if (item.Index == trigger.Count)
                {
                    entry.Areas.Add((awoZone, item.Candidate, item.Index));
                }
            }
        }

        if (!includeAreas && entry.Zones.Count > 0)
        {
            entry.Resolved = true;
            entry.LastFailure = string.Empty;
            LogVerbose($"Resolved OverrideBigZone trigger '{trigger.ID}' to {entry.Zones.Count} LG_Zone(s): {string.Join(", ", entry.Zones.Select(SafeZoneDescriptor).Take(8))}");
            return entry;
        }

        if (includeAreas && entry.Areas.Count > 0)
        {
            entry.Resolved = true;
            entry.LastFailure = string.Empty;
            LogVerbose($"Resolved OverrideArea trigger '{trigger.ID}' to {entry.Areas.Count} area(s): {string.Join(", ", entry.Areas.Select(a => SafeZoneDescriptor(a.Zone) + $"/Area[{a.Index}]").Take(8))}");
            return entry;
        }

        string desired = includeAreas
            ? $"DimensionIndex={trigger.DimensionIndex}, Layer='{trigger.Layer}', LocalIndex={trigger.LocalIndex}, Count={trigger.Count}"
            : $"DimensionIndex={trigger.DimensionIndex}, Layer='{trigger.Layer}', LocalIndex={trigger.LocalIndex}";
        entry.LastFailure = $"no matching {(includeAreas ? "LG_Area" : "LG_Zone")} for {desired}. AvailableZones={DescribeAvailableZonesFromBuilder()}";
        return entry;
    }

    private static string BuildZoneCacheKey(string kind, PositionTriggerRule trigger)
    {
        return $"{kind}|D={trigger.DimensionIndex}|L={trigger.Layer.Trim().ToLowerInvariant()}|Z={trigger.LocalIndex}|A={trigger.Count}";
    }

    private static bool TryGetAwoZone(PositionTriggerRule trigger, out LG_Zone? zone, out string failure)
    {
        zone = null;
        failure = string.Empty;

        if (Builder.CurrentFloor == null)
        {
            failure = "Builder.CurrentFloor is not ready; retrying after level geometry is ready.";
            return false;
        }

        if (trigger.LocalIndex < 0)
        {
            failure = "LocalIndex is missing. AWO-style zone lookup requires LocalIndex.";
            return false;
        }

        eDimensionIndex dimension = trigger.DimensionIndex >= 0 ? (eDimensionIndex)trigger.DimensionIndex : eDimensionIndex.Reality;
        LG_LayerType layer = ParseLayerTypeOrDefault(trigger.Layer);
        eLocalZoneIndex localIndex = (eLocalZoneIndex)trigger.LocalIndex;

        if (!IsAwoFloorReadyForLookup(dimension, out string readinessFailure))
        {
            failure = readinessFailure;
            return false;
        }

        try
        {
            if (Builder.CurrentFloor.TryGetZoneByLocalIndex(dimension, layer, localIndex, out zone) && zone != null)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            failure = $"AWO-style TryGetZoneByLocalIndex({dimension}, {layer}, {localIndex}) failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        failure = $"AWO-style TryGetZoneByLocalIndex({dimension}, {layer}, {localIndex}) returned no zone. AvailableZones={DescribeAvailableZonesFromBuilder()}";
        return false;
    }

    private static bool IsAwoFloorReadyForLookup(eDimensionIndex dimension, out string failure)
    {
        failure = string.Empty;
        object? floor = Builder.CurrentFloor;
        if (floor == null)
        {
            failure = "Builder.CurrentFloor is not ready; retrying after level geometry is ready.";
            return false;
        }

        object? dimensions = TryGetObjectMember(floor, "m_dimensions", "Dimensions", "dimensions");
        int dimensionCount = TryGetCollectionCount(dimensions);
        int dimensionIndex = (int)dimension;
        if (dimensionCount <= 0)
        {
            failure = $"Builder.CurrentFloor dimensions are not ready yet; skip AWO lookup for {dimension} to avoid LG_Floor out-of-bounds spam.";
            return false;
        }

        if (dimensionIndex < 0 || dimensionIndex >= dimensionCount)
        {
            failure = $"DimensionIndex {dimension} ({dimensionIndex}) is outside Builder.CurrentFloor.m_dimensions Count={dimensionCount}.";
            return false;
        }

        object? zones = TryGetObjectMember(floor, "allZones", "m_zones", "Zones", "zones");
        int zoneCount = TryGetCollectionCount(zones);
        if (zoneCount <= 0)
        {
            failure = "Builder.CurrentFloor zones are not ready yet; retrying before AWO zone lookup.";
            return false;
        }

        return true;
    }

    private static LG_LayerType ParseLayerTypeOrDefault(string layerText)
    {
        if (string.IsNullOrWhiteSpace(layerText)) return LG_LayerType.MainLayer;

        string normalized = layerText.Trim();
        if (int.TryParse(normalized, out int layerIndex)) return (LG_LayerType)layerIndex;
        if (Enum.TryParse(normalized, true, out LG_LayerType parsed)) return parsed;

        return normalized.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
        {
            "main" or "mainlayer" => LG_LayerType.MainLayer,
            "secondary" or "secondarylayer" => LG_LayerType.SecondaryLayer,
            "third" or "thirdlayer" => LG_LayerType.ThirdLayer,
            _ => LG_LayerType.MainLayer
        };
    }

    private static bool PlayerMatchesAwoZone(PlayerAgent agent, LG_Zone zone)
    {
        try
        {
            if (agent.CourseNode?.m_zone == null) return false;
            return agent.CourseNode.m_zone.ID == zone.ID;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeAvailableZonesFromBuilder()
    {
        try
        {
            if (Builder.CurrentFloor?.allZones != null)
            {
                return DescribeAvailableZonesFromCollection(Builder.CurrentFloor.allZones);
            }
        }
        catch { }

        try
        {
            return DescribeAvailableZones(UnityEngine.Object.FindObjectsOfType<LG_Zone>());
        }
        catch
        {
            return "<failed to describe zones>";
        }
    }

    private static string DescribeAvailableZonesFromCollection(object zones)
    {
        try
        {
            List<LG_Zone> result = new();
            foreach (object? item in CollectionToObjectList(zones))
            {
                if (item is LG_Zone zone) result.Add(zone);
            }

            if (result.Count > 0) return DescribeAvailableZones(result);
        }
        catch { }

        return "<failed to enumerate Builder.CurrentFloor.allZones>";
    }

    private static string DescribeAvailableZones(IEnumerable<LG_Zone> zones)
    {
        try
        {
            string[] values = zones
                .Where(z => z != null)
                .Select(SafeZoneDescriptor)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToArray();
            return values.Length == 0 ? "<none>" : string.Join(", ", values);
        }
        catch
        {
            return "<failed to describe zones>";
        }
    }

    private static string SafeZoneDescriptor(LG_Zone zone)
    {
        try
        {
            return $"D{(int)zone.DimensionIndex}/L{TryGetLayerIndex(zone)}/Z{(int)zone.LocalIndex}";
        }
        catch
        {
            return "<zone>";
        }
    }

    private static List<(object Candidate, int Index)> EnumerateZoneAreas(LG_Zone zone)
    {
        List<(object Candidate, int Index)> result = new();
        object? areas = TryGetObjectMember(zone, "m_areas", "Areas", "m_areaList", "AreaList");
        if (areas == null) return result;

        try
        {
            int index = 0;
            foreach (object? current in CollectionToObjectList(areas))
            {
                if (current != null) result.Add((current, index));
                index++;
            }
        }
        catch { }

        return result;
    }

    private static bool ZoneContainsCourseNode(LG_Zone zone, object node)
    {
        object? nodes = TryGetObjectMember(zone, "m_courseNodes", "CourseNodes", "m_courseNodeList", "CourseNodeList", "Nodes", "m_nodes");
        return ObjectCollectionContains(nodes, node);
    }

    private static bool ObjectCollectionContains(object? collection, object target)
    {
        if (collection == null) return false;
        try
        {
            if (ReferenceEquals(collection, target)) return true;
            foreach (object? item in CollectionToObjectList(collection))
            {
                if (item == null) continue;
                if (ReferenceEquals(item, target)) return true;
                if (Equals(item, target)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryResolveZoneByPosition(Vector3 position, PositionTriggerRule trigger, bool requireArea)
    {
        try
        {
            foreach (LG_Zone zone in UnityEngine.Object.FindObjectsOfType<LG_Zone>())
            {
                if (!ZoneMatchesTrigger(zone, trigger)) continue;
                if (!requireArea) return IsPositionInsideZoneByReflection(zone, position);
                if (TryAnyAreaInZoneMatchesPosition(zone, position, trigger)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsPositionInsideZoneByReflection(LG_Zone zone, Vector3 position)
    {
        return TryGetZoneBounds(zone, out Bounds bounds) && bounds.Contains(position);
    }

    private static bool TryAnyAreaInZoneMatchesPosition(LG_Zone zone, Vector3 position, PositionTriggerRule trigger)
    {
        try
        {
            foreach ((object Candidate, int Index) item in EnumerateZoneAreas(zone))
            {
                if (item.Index != trigger.Count) continue;
                if (IsPositionInsideAreaByReflection(item.Candidate, position)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsPositionInsideAreaByReflection(object area, Vector3 position)
    {
        // OverrideArea 要覆盖目标 Area 的完整水平范围。GTFO 的 Area Bounds 在 Y 轴上有时很薄，
        // 玩家从后方/边缘进入时直接 Bounds.Contains 可能因高度差误判。
        // 因此这里严格使用 LocalIndex+Count 解析到的 Area，同时用 XZ 平面完整覆盖该 Area。
        return TryGetAreaBounds(area, out Bounds bounds) && BoundsContainsXZ(bounds, position, 0.10f);
    }

    private static bool BoundsContainsXZ(Bounds bounds, Vector3 position, float margin = 0.0f)
    {
        if (!IsUsableBounds(bounds)) return false;
        return position.x >= bounds.min.x - margin
            && position.x <= bounds.max.x + margin
            && position.z >= bounds.min.z - margin
            && position.z <= bounds.max.z + margin;
    }

    private static bool IsValidDebugVector(Vector3 v)
    {
        if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
        if (Mathf.Abs(v.x) >= 9000f || Mathf.Abs(v.y) >= 9000f || Mathf.Abs(v.z) >= 9000f) return false;
        return true;
    }

    private static bool IsUsableBounds(Bounds b)
    {
        if (!IsValidDebugVector(b.center)) return false;
        if (float.IsNaN(b.size.x) || float.IsNaN(b.size.y) || float.IsNaN(b.size.z)) return false;
        if (b.size.sqrMagnitude < 0.01f) return false;
        return true;
    }

    private static bool TryGetVector3Member(object obj, out Vector3 value, params string[] names)
    {
        value = default;
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? v = p?.GetValue(obj);
                if (v is Vector3 pv && IsValidDebugVector(pv)) { value = pv; return true; }
                FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                v = f?.GetValue(obj);
                if (v is Vector3 fv && IsValidDebugVector(fv)) { value = fv; return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryGetNodeOrAreaBounds(object obj, out Bounds bounds)
    {
        bounds = default;
        List<Vector3> points = new();
        try
        {
            if (TryGetVector3Member(obj, out Vector3 selfPos, "m_position", "Position", "position", "m_pos", "pos")) points.Add(selfPos);
            Transform? t = TryGetObjectMember(obj, "transform") as Transform;
            if (t != null && IsValidDebugVector(t.position)) points.Add(t.position);

            object? nodes = TryGetObjectMember(obj, "m_courseNodes", "CourseNodes", "m_courseNodeList", "CourseNodeList", "Nodes", "m_nodes", "m_navNodes", "NavNodes");
            foreach (object? node in CollectionToObjectList(nodes))
            {
                if (node == null) continue;
                if (TryGetVector3Member(node, out Vector3 p, "m_position", "Position", "position", "m_pos", "pos")) points.Add(p);
                Transform? nt = TryGetObjectMember(node, "transform") as Transform;
                if (nt != null && IsValidDebugVector(nt.position)) points.Add(nt.position);
            }
        }
        catch { }

        if (points.Count == 0) return false;
        bounds = new Bounds(points[0], Vector3.one * 2f);
        for (int i = 1; i < points.Count; i++) bounds.Encapsulate(points[i]);
        if (bounds.size.sqrMagnitude < 0.01f) bounds = new Bounds(bounds.center, Vector3.one * 2f);
        return IsUsableBounds(bounds);
    }

    private static bool TryGetZoneBounds(LG_Zone zone, out Bounds bounds, bool allowTransformFallback = true)
    {
        // 对 OverrideBigZone，优先使用 Zone 内 m_areas 的真实 Bounds 联合。
        // 这比 transform.position 或默认 12x6x12 小盒子更接近“整个大区”，
        // 也避免 DebugVisible 在 (0,0,0) 或错误小范围出现。
        bool hasAreaBounds = false;
        Bounds union = default;
        try
        {
            foreach ((object Candidate, int Index) item in EnumerateZoneAreas(zone))
            {
                if (!TryGetAreaBounds(item.Candidate, out Bounds areaBounds, allowTransformFallback)) continue;
                if (!hasAreaBounds)
                {
                    union = areaBounds;
                    hasAreaBounds = true;
                }
                else
                {
                    union.Encapsulate(areaBounds);
                }
            }
        }
        catch { }

        if (hasAreaBounds && IsUsableBounds(union))
        {
            bounds = union;
            return true;
        }

        object? boundsObj = TryGetObjectMember(zone, "m_bounds", "Bounds", "bounds");
        if (boundsObj is Bounds b && IsUsableBounds(b))
        {
            bounds = b;
            return true;
        }

        object? centerObj = TryGetObjectMember(zone, "m_center", "Center", "center");
        object? sizeObj = TryGetObjectMember(zone, "m_size", "Size", "size");
        if (centerObj is Vector3 center && sizeObj is Vector3 size)
        {
            Bounds candidate = new Bounds(center, size);
            if (IsUsableBounds(candidate))
            {
                bounds = candidate;
                return true;
            }
        }

        if (TryGetNodeOrAreaBounds(zone, out bounds))
        {
            return true;
        }

        // 最后的 transform fallback 仅用于“中心点提示”，不再伪装成 Zone 的完整范围。
        Transform? t = allowTransformFallback ? TryGetObjectMember(zone, "transform") as Transform : null;
        if (t != null && IsValidDebugVector(t.position))
        {
            Bounds candidate = new Bounds(t.position, new Vector3(4f, 4f, 4f));
            if (IsUsableBounds(candidate))
            {
                bounds = candidate;
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static float BoundsXZArea(Bounds b)
    {
        if (!IsUsableBounds(b)) return -1f;
        return Math.Max(0.0f, b.size.x) * Math.Max(0.0f, b.size.z);
    }

    private static void AddBoundsCandidate(List<Bounds> candidates, Bounds b)
    {
        if (IsUsableBounds(b)) candidates.Add(b);
    }

    private static bool TryGetHierarchyBounds(object obj, out Bounds bounds)
    {
        bounds = default;
        GameObject? go = null;
        try
        {
            if (obj is GameObject directGo) go = directGo;
            else if (obj is Component c) go = c.gameObject;
            else if (TryGetObjectMember(obj, "gameObject", "GameObject", "m_gameObject", "GO") is GameObject memberGo) go = memberGo;
            else if (TryGetObjectMember(obj, "transform", "Transform", "m_transform") is Transform memberTransform) go = memberTransform.gameObject;
        }
        catch { }

        if (go == null) return false;

        bool has = false;
        Bounds union = default;
        try
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                Bounds b = r.bounds;
                if (!IsUsableBounds(b)) continue;
                if (!has) { union = b; has = true; }
                else union.Encapsulate(b);
            }
        }
        catch { }

        try
        {
            Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
            foreach (Collider c in colliders)
            {
                if (c == null) continue;
                Bounds b = c.bounds;
                if (!IsUsableBounds(b)) continue;
                if (!has) { union = b; has = true; }
                else union.Encapsulate(b);
            }
        }
        catch { }

        if (has && IsUsableBounds(union))
        {
            bounds = union;
            return true;
        }

        return false;
    }

    private static bool TryGetAreaBounds(object area, out Bounds bounds, bool allowTransformFallback = true)
    {
        // 0.9.6 修复：大型地形块的 LG_Area.m_bounds / CourseNode bounds 可能只覆盖导航线或中心走廊，
        // 导致 OverrideArea 触发体积“偏上/偏窄”，玩家从相邻区域进入大块地形时无法命中。
        // 这里收集多种候选 Bounds，并选取 XZ 覆盖面积最大的有效候选，优先贴近 Area 所属地形块实际尺寸。
        List<Bounds> candidates = new();

        if (TryGetHierarchyBounds(area, out Bounds hierarchyBounds))
        {
            AddBoundsCandidate(candidates, hierarchyBounds);
        }

        object? boundsObj = TryGetObjectMember(area, "m_bounds", "Bounds", "bounds");
        if (boundsObj is Bounds b)
        {
            AddBoundsCandidate(candidates, b);
        }

        object? centerObj = TryGetObjectMember(area, "m_center", "Center", "center");
        object? sizeObj = TryGetObjectMember(area, "m_size", "Size", "size");
        if (centerObj is Vector3 center && sizeObj is Vector3 size)
        {
            AddBoundsCandidate(candidates, new Bounds(center, size));
        }

        if (TryGetNodeOrAreaBounds(area, out Bounds nodeBounds))
        {
            AddBoundsCandidate(candidates, nodeBounds);
        }

        Transform? t = allowTransformFallback ? TryGetObjectMember(area, "transform") as Transform : null;
        if (t != null && IsValidDebugVector(t.position))
        {
            AddBoundsCandidate(candidates, new Bounds(t.position, new Vector3(6f, 4f, 6f)));
        }

        if (candidates.Count > 0)
        {
            bounds = candidates
                .OrderByDescending(BoundsXZArea)
                .First();
            return true;
        }

        bounds = default;
        return false;
    }

    internal static bool TryGetPositionTriggerDebugBounds(PositionTriggerRule trigger, out Bounds bounds, out string source)
    {
        string mode = (trigger.TriggerAreaMode ?? "Radius").Trim().ToLowerInvariant();
        if (mode == "overridebigzone" || mode == "bigzone" || mode == "zone")
        {
            if (TryGetCachedZones(trigger, out List<LG_Zone> zones, out string failure))
            {
                foreach (LG_Zone zone in zones)
                {
                    if (TryGetZoneBounds(zone, out bounds, allowTransformFallback: false))
                    {
                        source = $"OverrideBigZone LocalIndex={trigger.LocalIndex}";
                        return true;
                    }
                }
            }

            bounds = default;
            source = $"OverrideBigZone LocalIndex={trigger.LocalIndex} unresolved ({failure})";
            return false;
        }

        if (mode == "overridearea" || mode == "area")
        {
            if (TryGetCachedAreas(trigger, out List<(LG_Zone Zone, object Area, int Index)> areas, out string failure))
            {
                foreach ((LG_Zone Zone, object Area, int Index) item in areas)
                {
                    if (TryGetAreaBounds(item.Area, out bounds, allowTransformFallback: false))
                    {
                        source = $"OverrideArea LocalIndex={trigger.LocalIndex} Count={trigger.Count}";
                        return true;
                    }
                }
            }

            bounds = default;
            source = $"OverrideArea LocalIndex={trigger.LocalIndex} Count={trigger.Count} unresolved ({failure})";
            return false;
        }

        if (trigger.Position == null)
        {
            bounds = default;
            source = "Radius Position missing";
            return false;
        }

        Vector3 center = trigger.Position.ToVector3();
        float radius = Math.Max(0.5f, trigger.Radius);
        bounds = new Bounds(center, new Vector3(radius * 2f, Math.Max(1f, radius), radius * 2f));
        source = "Radius Position";
        return true;
    }

    private static bool LayerMatches(int zoneLayer, string layerText)
    {
        string normalized = layerText.Trim().ToLowerInvariant();
        if (int.TryParse(normalized, out int layerIndex)) return zoneLayer == layerIndex;
        return normalized switch
        {
            "main" or "mainlayer" => zoneLayer == 0,
            "secondary" or "secondarylayer" => zoneLayer == 1,
            "third" or "thirdlayer" => zoneLayer == 2,
            _ => true
        };
    }

    private static bool HasAnyAlivePlayer()
    {
        var list = PlayerManager.PlayerAgentsInLevel;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            try
            {
                PlayerAgent? agent = list[i];
                if (agent != null && agent.Alive) return true;
            }
            catch { }
        }
        return false;
    }

    private static IEnumerable<PlayerAgent> EnumeratePlayers(PositionTriggerRule trigger)
    {
        var list = PlayerManager.PlayerAgentsInLevel;
        if (list == null)
        {
            yield break;
        }

        for (int i = 0; i < list.Count; i++)
        {
            PlayerAgent? agent = null;
            try { agent = list[i]; } catch { }
            if (agent == null)
            {
                continue;
            }

            if (trigger.RequireAlivePlayers && !agent.Alive)
            {
                continue;
            }

            // PlayerAgentsInLevel is already limited to player agents and bots. IncludeBots is retained for future filtering.
            yield return agent;
        }
    }
    private static Vector3? GetPositionTriggerContextPosition(PositionTriggerRule trigger)
    {
        foreach (PlayerAgent agent in EnumeratePlayers(trigger))
        {
            if (IsPlayerInsideTriggerArea(agent, trigger))
            {
                return agent.Position;
            }
        }

        if (TryGetPositionTriggerDebugBounds(trigger, out Bounds bounds, out _))
        {
            return bounds.center;
        }

        if (trigger.Position != null)
        {
            return trigger.Position.ToVector3();
        }

        return null;
    }

    private static void FireTrigger(PositionTriggerRule trigger, TriggerState state)
    {
        state.Fired = true;
        state.LastFireTime = Time.realtimeSinceStartup;
        Vector3? runtimePosition = GetPositionTriggerContextPosition(trigger);
        int count = ExecuteEventList(trigger.Events.Concat(trigger.WardenEvents), $"Coordinate trigger '{trigger.ID}'", runtimePosition);
        LogVerbose($"Coordinate trigger '{trigger.ID}' fired. Mode={trigger.TriggerAreaMode}, ExecutedEvents={count}");
        if (count == 0)
        {
            LogVerbose($"Coordinate trigger '{trigger.ID}' matched its area but executed 0 events. Add Events/WardenEvents, or enable UsePlayerCountEvents with non-empty player-count event arrays.");
        }
        HandlePositionTriggerCycle(trigger, state, $"Coordinate trigger '{trigger.ID}'");
    }

    private static void FireTriggerForPlayerCount(PositionTriggerRule trigger, TriggerState state, int insidePlayerCount)
    {
        state.Fired = true;
        state.FiredPlayerCounts.Add(insidePlayerCount);
        state.LastFireTime = Time.realtimeSinceStartup;

        IEnumerable<JsonElement> events = GetEventsForPlayerCount(trigger, insidePlayerCount);
        Vector3? runtimePosition = GetPositionTriggerContextPosition(trigger);
        int count = ExecuteEventList(events, $"Coordinate trigger '{trigger.ID}' playerCount={insidePlayerCount}", runtimePosition);
        LogVerbose($"Coordinate trigger '{trigger.ID}' fired by player count. Mode={trigger.TriggerAreaMode}, PlayersInside={insidePlayerCount}, ExecutedEvents={count}");
        if (count == 0)
        {
            LogVerbose($"Coordinate trigger '{trigger.ID}' matched with PlayersInside={insidePlayerCount} but that player-count event list is empty.");
        }
        HandlePositionTriggerCycle(trigger, state, $"Coordinate trigger '{trigger.ID}' playerCount={insidePlayerCount}");
    }

    private static void HandlePositionTriggerCycle(PositionTriggerRule trigger, TriggerState state, string ownerLabel)
    {
        if (!trigger.UseTriggerCycleEvents || trigger.TriggerCycleEvents.Count == 0)
        {
            return;
        }

        int required = Math.Max(1, trigger.TriggerCycleCount);
        state.CompletedCycles++;
        if (state.CompletedCycles % required != 0)
        {
            LogVerbose($"Position trigger cycle progress. ID={trigger.ID}, Cycles={state.CompletedCycles}/{required}");
            return;
        }

        Vector3? runtimePosition = GetPositionTriggerContextPosition(trigger);
        int count = ExecuteEventList(trigger.TriggerCycleEvents, $"{ownerLabel} cycle={state.CompletedCycles}", runtimePosition);
        LogVerbose($"Position trigger cycle events fired. ID={trigger.ID}, Cycles={state.CompletedCycles}, Required={required}, ExecutedEvents={count}");
    }

}
