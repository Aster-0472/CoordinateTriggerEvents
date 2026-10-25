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
using AIGraph;
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

        PositionTriggerPlayerState playerState = GetPositionTriggerPlayerState(trigger);
        int insidePlayerCount = playerState.InsidePlayerCount;
        int eligibleInsidePlayerCount = playerState.EligibleInsidePlayerCount;
        int eligiblePlayers = playerState.EligiblePlayerCount;
        int previousInsidePlayerCount = state.LastInsidePlayerCount;
        int previousEligibleInsidePlayerCount = state.LastEligibleInsidePlayerCount;
        bool inside = insidePlayerCount > 0;
        bool wasInside = state.WasInside;
        bool playerCountEventsEnabled = trigger.UsePlayerCountEvents;
        string mode = NormalizePositionTriggerMode(trigger.TriggerMode);

        if ((mode == "anyplayerinside" || mode == "allplayersinside" || mode == "anyplayeroutside" || mode == "allplayersoutside") && trigger.Cooldown < 1.0f)
        {
            trigger.Cooldown = 1.0f;
        }

        if (!state.Initialized)
        {
            UpdatePositionTriggerState(state, inside, insidePlayerCount, eligibleInsidePlayerCount);
            return;
        }

        if (eligibleInsidePlayerCount > 0)
        {
            state.HasEverHadPlayersInside = true;
        }

        if (playerCountEventsEnabled)
        {
            bool shouldFireCount = false;
            int eventPlayerCount = insidePlayerCount;

            switch (mode)
            {
                case "anyplayerinside":
                    shouldFireCount = inside && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "allplayersinside":
                    shouldFireCount = eligiblePlayers > 0 && eligibleInsidePlayerCount >= eligiblePlayers;
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "anyplayeroutside":
                    shouldFireCount = state.Initialized && state.HasEverHadPlayersInside && eligiblePlayers > 0 && eligibleInsidePlayerCount < eligiblePlayers;
                    eventPlayerCount = Math.Max(1, Math.Min(4, eligiblePlayers - eligibleInsidePlayerCount));
                    break;

                case "allplayersoutside":
                    shouldFireCount = state.Initialized && state.HasEverHadPlayersInside && eligiblePlayers > 0 && eligibleInsidePlayerCount == 0;
                    eventPlayerCount = Math.Max(1, Math.Min(4, eligiblePlayers));
                    break;

                case "allplayersexit":
                    shouldFireCount = wasInside && !inside && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                    eventPlayerCount = Math.Max(1, previousInsidePlayerCount);
                    break;

                case "anyplayerexit":

    // 退出触发：任意玩家离开范围都会触发�?
    // UsePlayerCountEvents=true 时使用“离开前的人数”选择 PlayerCountEvents�?
    // �?OnPlayerExitScan 的退出前人数语义保持一致�?                    shouldFireCount = previousInsidePlayerCount > insidePlayerCount && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                    eventPlayerCount = Math.Max(1, previousInsidePlayerCount);
                    break;

                case "allplayersenter":
                    shouldFireCount = previousInsidePlayerCount < insidePlayerCount && eligiblePlayers > 0 && eligibleInsidePlayerCount >= eligiblePlayers;
                    eventPlayerCount = insidePlayerCount;
                    break;

                case "anyplayerenter":
                default:
                    shouldFireCount = insidePlayerCount > previousInsidePlayerCount && insidePlayerCount >= 1 && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                    eventPlayerCount = insidePlayerCount;
                    break;
            }

            UpdatePositionTriggerState(state, inside, insidePlayerCount, eligibleInsidePlayerCount);

            if (!shouldFireCount || eventPlayerCount < 1 || eventPlayerCount > 4)
            {
                return;
            }

            FireTriggerForPlayerCount(trigger, state, eventPlayerCount);
            return;
        }

        bool shouldFire;
        switch (mode)
        {
            case "anyplayerinside":
                shouldFire = inside && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                break;

            case "allplayersinside":
                shouldFire = eligiblePlayers > 0 && eligibleInsidePlayerCount >= eligiblePlayers;
                break;

            case "anyplayeroutside":
                shouldFire = state.Initialized && state.HasEverHadPlayersInside && eligiblePlayers > 0 && eligibleInsidePlayerCount < eligiblePlayers;
                break;

            case "allplayersoutside":
                shouldFire = state.Initialized && state.HasEverHadPlayersInside && eligiblePlayers > 0 && eligibleInsidePlayerCount == 0;
                break;

            case "allplayersenter":
                shouldFire = previousInsidePlayerCount < insidePlayerCount && eligiblePlayers > 0 && eligibleInsidePlayerCount >= eligiblePlayers;
                break;

            case "allplayersexit":
                shouldFire = wasInside && !inside && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                break;

            case "anyplayerexit":
                shouldFire = previousInsidePlayerCount > insidePlayerCount && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                break;

            case "anyplayerenter":
            default:
                shouldFire = !wasInside && inside && PositionTriggerHasEligibleEventContext(trigger, mode, previousEligibleInsidePlayerCount, eligibleInsidePlayerCount);
                break;
        }

        UpdatePositionTriggerState(state, inside, insidePlayerCount, eligibleInsidePlayerCount);

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

                    "outside" => "anyplayeroutside",
                    "anyoutside" => "anyplayeroutside",
                    "playeroutside" => "anyplayeroutside",
                    "anyplayeroutside" => "anyplayeroutside",
                    "onplayeroutside" => "anyplayeroutside",
                    "outsidearea" => "anyplayeroutside",
                    "anyplayeroutsidearea" => "anyplayeroutside",

                    "alloutside" => "allplayersoutside",
                    "allplayeroutside" => "allplayersoutside",
                    "allplayersoutside" => "allplayersoutside",
                    "allplayersoutsidearea" => "allplayersoutside",

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

    private readonly struct PositionTriggerPlayerState
    {
        public readonly int InsidePlayerCount;
        public readonly int EligibleInsidePlayerCount;
        public readonly int EligiblePlayerCount;

        public PositionTriggerPlayerState(int insidePlayerCount, int eligibleInsidePlayerCount, int eligiblePlayerCount)
        {
            InsidePlayerCount = insidePlayerCount;
            EligibleInsidePlayerCount = eligibleInsidePlayerCount;
            EligiblePlayerCount = eligiblePlayerCount;
        }
    }

    private static void UpdatePositionTriggerState(TriggerState state, bool inside, int insidePlayerCount, int eligibleInsidePlayerCount)
    {
        state.Initialized = true;
        state.WasInside = inside;
        state.LastInsidePlayerCount = insidePlayerCount;
        state.LastEligibleInsidePlayerCount = eligibleInsidePlayerCount;
        if (eligibleInsidePlayerCount > 0)
        {
            state.HasEverHadPlayersInside = true;
        }
    }

    private static PositionTriggerPlayerState GetPositionTriggerPlayerState(PositionTriggerRule trigger)
    {
        int insidePlayerCount = 0;
        int eligibleInsidePlayerCount = 0;
        int eligiblePlayerCount = 0;

        foreach (PlayerAgent agent in EnumeratePlayersForAreaState(trigger))
        {
            bool eligible = !trigger.RequireAlivePlayers || agent.Alive;
            if (eligible)
            {
                eligiblePlayerCount++;
            }

            if (!IsPlayerInsideTriggerArea(agent, trigger))
            {
                continue;
            }

            insidePlayerCount++;
            if (eligible)
            {
                eligibleInsidePlayerCount++;
            }
        }

        return new PositionTriggerPlayerState(insidePlayerCount, eligibleInsidePlayerCount, eligiblePlayerCount);
    }

    private static int CountEligiblePlayers(PositionTriggerRule trigger)
    {
        int count = 0;
        foreach (PlayerAgent _ in EnumeratePlayersForEventEligibility(trigger)) count++;
        return count;
    }

    private static int CountPlayersInsideTriggerArea(PositionTriggerRule trigger)
    {
        int count = 0;
        foreach (PlayerAgent agent in EnumeratePlayersForAreaState(trigger))
        {
            if (IsPlayerInsideTriggerArea(agent, trigger))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountEligiblePlayersInsideTriggerArea(PositionTriggerRule trigger)
    {
        int count = 0;
        foreach (PlayerAgent agent in EnumeratePlayersForEventEligibility(trigger))
        {
            if (IsPlayerInsideTriggerArea(agent, trigger))
            {
                count++;
            }
        }
        return count;
    }

    private static bool PositionTriggerHasEligibleEventContext(PositionTriggerRule trigger, string mode, int previousEligibleInsidePlayerCount, int eligibleInsidePlayerCount)
    {
        if (!trigger.RequireAlivePlayers)
        {
            return true;
        }

        return mode switch
        {
            "anyplayerexit" or "allplayersexit" => previousEligibleInsidePlayerCount > eligibleInsidePlayerCount,
            _ => eligibleInsidePlayerCount > 0
        };
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

    // OverrideBigZone 必须严格绑定到配置中�?LocalIndex 对应 LG_Zone�?
    // 不允许退�?Position/Radius，也不允许在找不�?Zone 时视为全图触发�?
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


        if (TryGetSampledCourseNodeArea(agent.Position, trigger, out object sampledArea))
        {
            return TryAreaInstanceIdMatchesCachedArea(sampledArea, areas);
        }

        if (TryGetPlayerCourseNode(agent, out object playerNode) && PlayerCourseNodeMatchesCachedArea(playerNode, areas))
        {
            return true;
        }

        foreach ((LG_Zone Zone, object Area, int Index) item in areas)
        {
            if (TryGetStrictAreaBoundsContains(item.Area, agent.Position)) return true;
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

    // OverrideArea �?Count 使用 GTFO LG_Zone.m_areas 内部顺序�?
    // Count=0 表示�?Zone �?A 区，Count=1 表示 B 区，以此类推�?
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

    private static bool TryAreaInstanceIdMatchesCachedArea(object area, List<(LG_Zone Zone, object Area, int Index)> areas)
    {
        if (!TryGetAreaInstanceId(area, out int sourceId)) return AreaMatchesCachedArea(area, areas);

        foreach ((LG_Zone Zone, object Area, int Index) item in areas)
        {
            if (TryGetAreaInstanceId(item.Area, out int targetId) && sourceId == targetId) return true;
        }

        return false;
    }

    private static bool TryGetAreaInstanceId(object area, out int instanceId)
    {
        instanceId = 0;
        try
        {
            if (area is LG_Area lgArea && lgArea.gameObject != null)
            {
                instanceId = lgArea.gameObject.GetInstanceID();
                return true;
            }

            if (TryGetObjectMember(area, "gameObject", "GameObject", "m_gameObject", "GO") is GameObject go && go != null)
            {
                instanceId = go.GetInstanceID();
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryGetSampledCourseNodeArea(Vector3 position, PositionTriggerRule trigger, out object sampledArea)
    {
        sampledArea = null!;
        eDimensionIndex dimension = trigger.DimensionIndex >= 0 ? (eDimensionIndex)trigger.DimensionIndex : eDimensionIndex.Reality;

        try
        {
            if (ResolveCourseNodeFromPositionMap(position, dimension, out AIG_CourseNode mappedNode) && mappedNode?.m_area != null)
            {
                sampledArea = mappedNode.m_area;
                return true;
            }
        }
        catch { }

        try
        {
            if (AIG_GeomorphNodeVolume.TryGetCourseNode(dimension, position, out AIG_CourseNode node) && node?.m_area != null)
            {
                sampledArea = node.m_area;
                return true;
            }
        }
        catch { }

        try
        {
            if (AIG_CourseNode.TryGetCourseNode(dimension, position, 1.5f, out AIG_CourseNode node) && node?.m_area != null)
            {
                sampledArea = node.m_area;
                return true;
            }
        }
        catch { }

        return false;
    }

    private const float CourseNodeMapIndexSize = 4f;
    private static readonly Dictionary<eDimensionIndex, CourseNodeDimensionMap> CourseNodePositionMaps = new();
    private static int CourseNodePositionMapClusterCount = -1;

    private sealed class CourseNodeDimensionMap
    {
        public List<AIG_CourseNode>?[,]? BuildNodeMap;
        public AIG_CourseNode[]?[,]? NodeMap;
        public int MinX = int.MaxValue;
        public int MinZ = int.MaxValue;
        public int MaxX = int.MinValue;
        public int MaxZ = int.MinValue;
        public int SizeX;
        public int SizeZ;

        public void UpdateBounds(Vector3 position)
        {
            int x = Mathf.FloorToInt(position.x / CourseNodeMapIndexSize);
            int z = Mathf.FloorToInt(position.z / CourseNodeMapIndexSize);
            MinX = Math.Min(MinX, x);
            MinZ = Math.Min(MinZ, z);
            MaxX = Math.Max(MaxX, x);
            MaxZ = Math.Max(MaxZ, z);
        }

        public bool CreateNodeMap()
        {
            if (MinX == int.MaxValue || MinZ == int.MaxValue || MaxX == int.MinValue || MaxZ == int.MinValue)
            {
                return false;
            }

            SizeX = Math.Max(1, MaxX - MinX + 1);
            SizeZ = Math.Max(1, MaxZ - MinZ + 1);
            BuildNodeMap = new List<AIG_CourseNode>[SizeX, SizeZ];
            NodeMap = new AIG_CourseNode[SizeX, SizeZ][];
            return true;
        }

        public bool AddNode(Vector3 position, AIG_CourseNode node)
        {
            if (BuildNodeMap == null) return false;
            (int x, int z) = GetMapPos(position);
            List<AIG_CourseNode>? list = BuildNodeMap[x, z];
            if (list == null)
            {
                list = new List<AIG_CourseNode>();
                BuildNodeMap[x, z] = list;
            }

            if (!list.Any(existing => existing != null && existing.NodeID == node.NodeID))
            {
                list.Add(node);
            }

            return true;
        }

        public void FinishBuild()
        {
            if (BuildNodeMap == null || NodeMap == null) return;
            for (int x = 0; x < SizeX; x++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    NodeMap[x, z] = BuildNodeMap[x, z]?.ToArray();
                }
            }
            BuildNodeMap = null;
        }

        public AIG_CourseNode[]? GetNodes(Vector3 position)
        {
            if (NodeMap == null) return null;
            (int x, int z) = GetMapPos(position);
            if (NodeMap[x, z] != null) return NodeMap[x, z];

            int maxRadius = Math.Max(SizeX, SizeZ);
            for (int radius = 1; radius < maxRadius; radius++)
            {
                int left = Math.Max(x - radius, 0);
                int right = Math.Min(x + radius, SizeX - 1);
                int top = Math.Max(z - radius, 0);
                int bottom = Math.Min(z + radius, SizeZ - 1);

                for (int xPos = left; xPos <= right; xPos++)
                {
                    if (NodeMap[xPos, top] != null) return NodeMap[xPos, top];
                    if (NodeMap[xPos, bottom] != null) return NodeMap[xPos, bottom];
                }

                for (int zPos = top + 1; zPos <= bottom - 1; zPos++)
                {
                    if (NodeMap[left, zPos] != null) return NodeMap[left, zPos];
                    if (NodeMap[right, zPos] != null) return NodeMap[right, zPos];
                }
            }

            return null;
        }

        private (int x, int z) GetMapPos(Vector3 position)
        {
            int x = Mathf.FloorToInt(position.x / CourseNodeMapIndexSize) - MinX;
            int z = Mathf.FloorToInt(position.z / CourseNodeMapIndexSize) - MinZ;
            return (Mathf.Clamp(x, 0, SizeX - 1), Mathf.Clamp(z, 0, SizeZ - 1));
        }
    }

    private static bool TryRefreshCourseNodePositionMap()
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<AIG_NodeCluster>? clusters = AIG_NodeCluster.AllNodeClusters;
            int clusterCount = clusters?.Count ?? 0;
            if (clusterCount <= 0) return false;
            if (CourseNodePositionMaps.Count > 0 && CourseNodePositionMapClusterCount == clusterCount) return true;

            CourseNodePositionMaps.Clear();
            CourseNodePositionMapClusterCount = clusterCount;

            for (int i = 0; i < clusterCount; i++)
            {
                AIG_NodeCluster? cluster = clusters![i];
                AIG_CourseNode? courseNode = cluster?.m_courseNode;
                if (courseNode?.m_dimension == null || cluster?.m_nodes == null) continue;

                eDimensionIndex dimension = courseNode.m_dimension.DimensionIndex;
                if (!CourseNodePositionMaps.TryGetValue(dimension, out CourseNodeDimensionMap? map))
                {
                    map = new CourseNodeDimensionMap();
                    CourseNodePositionMaps[dimension] = map;
                }

                for (int n = 0; n < cluster.m_nodes.Count; n++)
                {
                    AIG_INode? node = cluster.m_nodes[n];
                    if (node != null) map.UpdateBounds(node.Position);
                }
            }

            foreach (CourseNodeDimensionMap map in CourseNodePositionMaps.Values)
            {
                map.CreateNodeMap();
            }

            for (int i = 0; i < clusterCount; i++)
            {
                AIG_NodeCluster? cluster = clusters![i];
                AIG_CourseNode? courseNode = cluster?.m_courseNode;
                if (courseNode?.m_dimension == null || cluster?.m_nodes == null) continue;

                if (!CourseNodePositionMaps.TryGetValue(courseNode.m_dimension.DimensionIndex, out CourseNodeDimensionMap? map)) continue;
                for (int n = 0; n < cluster.m_nodes.Count; n++)
                {
                    AIG_INode? node = cluster.m_nodes[n];
                    if (node != null) map.AddNode(node.Position, courseNode);
                }
            }

            foreach (CourseNodeDimensionMap map in CourseNodePositionMaps.Values)
            {
                map.FinishBuild();
            }

            return CourseNodePositionMaps.Count > 0;
        }
        catch
        {
            CourseNodePositionMaps.Clear();
            CourseNodePositionMapClusterCount = -1;
            return false;
        }
    }

    private static bool ResolveCourseNodeFromPositionMap(Vector3 position, eDimensionIndex dimension, out AIG_CourseNode node)
    {
        node = null!;
        if (!TryRefreshCourseNodePositionMap()) return false;
        if (!CourseNodePositionMaps.TryGetValue(dimension, out CourseNodeDimensionMap? map)) return false;

        AIG_CourseNode[]? candidates = map.GetNodes(position);
        if (candidates == null || candidates.Length == 0) return false;
        if (candidates.Length == 1)
        {
            node = candidates[0];
            return node != null;
        }

        float bestDistance = float.MaxValue;
        bool bestHasValidHeight = false;
        AIG_CourseNode? bestNode = null;
        foreach (AIG_CourseNode? candidate in candidates)
        {
            if (candidate == null) continue;
            if (!TryGetClosestNodeInCluster(candidate.m_nodeCluster, position, out AIG_INode closestNode) || closestNode == null)
            {
                continue;
            }

            bool validHeight = closestNode.Position.y - 0.25f <= position.y;
            float distance = (position - closestNode.Position).sqrMagnitude;
            if ((!bestHasValidHeight && validHeight) || (bestHasValidHeight == validHeight && distance < bestDistance))
            {
                bestHasValidHeight = validHeight;
                bestDistance = distance;
                bestNode = candidate;
            }
        }

        if (bestNode == null) return false;
        node = bestNode;
        return true;
    }

    private static bool TryGetClosestNodeInCluster(AIG_NodeCluster? cluster, Vector3 position, out AIG_INode node)
    {
        node = null!;
        try
        {
            if (cluster?.m_nodes == null || cluster.m_nodes.Count <= 0) return false;

            float bestDistance = float.MaxValue;
            AIG_INode? bestNode = null;
            for (int i = 0; i < cluster.m_nodes.Count; i++)
            {
                AIG_INode? current = cluster.m_nodes[i];
                if (current == null) continue;
                float distance = (current.Position - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestNode = current;
                }
            }

            if (bestNode == null) return false;
            node = bestNode;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetStrictAreaBoundsContains(object area, Vector3 position)
    {
        return TryGetAreaBounds(area, out Bounds strictBounds, allowTransformFallback: false)
            && BoundsContainsXZ(strictBounds, position);
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

    private static bool TryGetPlayerCourseNode(PlayerAgent agent, out object playerNode)
    {
        playerNode = null!;
        try
        {
            if (agent.CourseNode != null)
            {
                playerNode = agent.CourseNode;
                return true;
            }
        }
        catch { }

        try
        {
            object? candidate = TryGetObjectMember(agent, "CourseNode", "m_courseNode", "courseNode", "m_currentNode");
            if (candidate == null) return false;
            playerNode = candidate;
            return true;
        }
        catch
        {
            playerNode = null!;
            return false;
        }
    }

    private static bool AreaContainsCourseNode(object area, object node)
    {
        object? nodes = TryGetObjectMember(
            area,
            "m_courseNodes", "CourseNodes", "m_courseNodeList", "CourseNodeList",
            "m_nodes", "Nodes", "nodes", "m_navNodes", "NavNodes", "m_nodeList", "NodeList"
        );
        if (ObjectCollectionContains(nodes, node)) return true;


    // 某些 GTFO/LG_Area 实现不会暴露节点列表，但会在 CourseNode 上保�?Area 引用�?
    // 上层已先尝试 node.m_area；这里保持空安全，不做跨 Area 推断�?
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

    private static bool IsPositionInsideAreaByReflection(object area, Vector3 position, bool allowExpandedBounds = false)
    {

        if (TryGetAreaBounds(area, out Bounds strictBounds, allowTransformFallback: false) && BoundsContainsXZ(strictBounds, position))
        {
            return true;
        }

        return allowExpandedBounds
            && TryGetAreaBounds(area, out Bounds bounds, allowTransformFallback: allowExpandedBounds)
            && BoundsContainsXZ(bounds, position);
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

    // �?OverrideBigZone，优先使�?Zone �?m_areas 的真�?Bounds 联合�?
    // 这比 transform.position 或默�?12x6x12 小盒子更接近“整个大区”，

    // 也避�?DebugVisible �?(0,0,0) 或错误小范围出现�?
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

        // Transform fallback is only used as a center-point hint; it is not a full Zone bounds substitute.
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
        if (allowTransformFallback && TryGetHierarchyBounds(area, out Bounds hierarchyBounds))
        {
            bounds = hierarchyBounds;
            return true;
        }

        object? boundsObj = TryGetObjectMember(area, "m_bounds", "Bounds", "bounds");
        if (boundsObj is Bounds b && IsUsableBounds(b))
        {
            bounds = b;
            return true;
        }

        object? centerObj = TryGetObjectMember(area, "m_center", "Center", "center");
        object? sizeObj = TryGetObjectMember(area, "m_size", "Size", "size");
        if (centerObj is Vector3 center && sizeObj is Vector3 size)
        {
            Bounds candidate = new Bounds(center, size);
            if (IsUsableBounds(candidate))
            {
                bounds = candidate;
                return true;
            }
        }

        if (TryGetNodeOrAreaBounds(area, out Bounds nodeBounds))
        {
            bounds = nodeBounds;
            return true;
        }

        Transform? t = allowTransformFallback ? TryGetObjectMember(area, "transform") as Transform : null;
        if (t != null && IsValidDebugVector(t.position))
        {
            Bounds candidate = new Bounds(t.position, new Vector3(6f, 4f, 6f));
            if (IsUsableBounds(candidate))
            {
                bounds = candidate;
                return true;
            }
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

    private static IEnumerable<PlayerAgent> EnumeratePlayersForAreaState(PositionTriggerRule trigger)
    {
        return EnumeratePlayers(trigger, requireAlive: false);
    }

    private static IEnumerable<PlayerAgent> EnumeratePlayersForEventEligibility(PositionTriggerRule trigger)
    {
        return EnumeratePlayers(trigger, requireAlive: trigger.RequireAlivePlayers);
    }

    private static IEnumerable<PlayerAgent> EnumeratePlayers(PositionTriggerRule trigger, bool requireAlive)
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

            if (requireAlive && !agent.Alive)
            {
                continue;
            }


    // PlayerAgentsInLevel is already limited to player agents and bots. IncludeBots is retained for future filtering.
            yield return agent;
        }
    }
    private static Vector3? GetPositionTriggerContextPosition(PositionTriggerRule trigger)
    {
        foreach (PlayerAgent agent in EnumeratePlayersForAreaState(trigger))
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









