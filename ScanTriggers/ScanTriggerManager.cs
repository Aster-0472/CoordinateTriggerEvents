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

internal static class ScanTriggerManager
{
    internal static readonly Dictionary<string, TriggerState> RuleStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, ScanRuntimeState> ScanStates = new();
    // 1.0.6：持续扫描触发只遍历已进入状态机的扫描点，不再每次 Tick FindObjectsOfType 全图扫描。
    private static readonly Dictionary<int, CP_Bioscan_Core> ActiveScanRefs = new();
    // 1.0.6：持续扫描触发按每个 ScanTrigger 的 Cooldown 到期后才进入执行路径，避免每 0.2 秒对同一扫描点重复调用事件匹配。
    private static readonly Dictionary<string, float> ScanRepeatNextDueTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, float> LastActivationTimes = new();
    // 每个扫描点在一次激活周期内只允许 OnScanActivated 进入事件系统一次。
    private static readonly HashSet<int> ActivationFiredThisCycle = new();
    private static bool _dumpedScanIndexes;
    private static float _lastScanDumpTryTime;

    internal static void Reset()
    {
        RuleStates.Clear();
        ScanStates.Clear();
        ActiveScanRefs.Clear();
        ScanRepeatNextDueTimes.Clear();
        LastActivationTimes.Clear();
        ActivationFiredThisCycle.Clear();
        _dumpedScanIndexes = false;
        _lastScanDumpTryTime = 0f;
    }


    private static bool IsScanRepeatDue(string dueKey, float cooldown)
    {
        if (cooldown < 1.0f) cooldown = 1.0f;
        float now = Time.realtimeSinceStartup;
        if (ScanRepeatNextDueTimes.TryGetValue(dueKey, out float due) && now < due)
        {
            return false;
        }
        ScanRepeatNextDueTimes[dueKey] = now + cooldown;
        return true;
    }

    internal static void ProcessScanRepeatEvents()
    {
        try
        {
            List<(ConfigDocument Config, ScanTriggerRule Trigger)> active = Runtime.GetActiveScanTriggers();
            if (active.Count == 0)
            {
                return;
            }

            bool hasAllInsideRepeat = false;
            bool hasAllExitedRepeat = false;
            foreach ((ConfigDocument _, ScanTriggerRule trigger) in active)
            {
                string mode = Runtime.NormalizeTriggerMode(trigger.TriggerMode);
                if (mode == "onallplayersinsidescan") hasAllInsideRepeat = true;
                if (mode == "onallplayersexitedscan") hasAllExitedRepeat = true;
            }

            if (!hasAllInsideRepeat && !hasAllExitedRepeat)
            {
                return;
            }

            int eligiblePlayers = CountEligibleScanPlayers();
            if (eligiblePlayers <= 0)
            {
                return;
            }

            foreach (KeyValuePair<int, CP_Bioscan_Core> pair in ActiveScanRefs.ToArray())
            {
                int instanceId = pair.Key;
                CP_Bioscan_Core scan = pair.Value;
                if (scan == null)
                {
                    ActiveScanRefs.Remove(instanceId);
                    ScanStates.Remove(instanceId);
                    continue;
                }
                if (!ScanStates.TryGetValue(instanceId, out ScanRuntimeState? state))
                {
                    continue;
                }

                if (!state.IsActive && !state.AllPlayersExitedRepeatActive)
                {
                    continue;
                }

                ScanBindingInfo binding = GetScanBindingInfo(scan);
                int puzzleIndex = binding.Index;
                int currentPlayers = GetPlayersInScan(scan);
                if (currentPlayers < 0) currentPlayers = 0;

                bool allInside = state.IsActive && currentPlayers >= eligiblePlayers;
                bool allExitedAfterEnter = state.IsActive && currentPlayers == 0 && state.AllPlayersExitedRepeatActive;

                if (hasAllInsideRepeat && allInside)
                {
                    foreach ((ConfigDocument config, ScanTriggerRule trigger) in active)
                    {
                        if (Runtime.NormalizeTriggerMode(trigger.TriggerMode) != "onallplayersinsidescan") continue;
                        if (trigger.PuzzleOverrideIndex >= 0 && trigger.PuzzleOverrideIndex != puzzleIndex) continue;
                        string suffix = $"{instanceId}:allplayersinside-repeat";
                        string dueKey = config.FilePath + "::scan-repeat::" + trigger.ID + "::" + suffix;
                        if (!IsScanRepeatDue(dueKey, trigger.Cooldown)) continue;
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersInsideScan", GetScanSourceName(scan), puzzleIndex, Math.Min(Math.Max(currentPlayers, 1), 4), suffix, GetScanRuntimePosition(scan));
                    }
                }

                if (hasAllExitedRepeat && allExitedAfterEnter)
                {
                    int eventPlayerCount = Math.Min(Math.Max(eligiblePlayers, 1), 4);
                    foreach ((ConfigDocument config, ScanTriggerRule trigger) in active)
                    {
                        if (Runtime.NormalizeTriggerMode(trigger.TriggerMode) != "onallplayersexitedscan") continue;
                        if (trigger.PuzzleOverrideIndex >= 0 && trigger.PuzzleOverrideIndex != puzzleIndex) continue;
                        string suffix = $"{instanceId}:allplayersexited-repeat";
                        string dueKey = config.FilePath + "::scan-repeat::" + trigger.ID + "::" + suffix;
                        if (!IsScanRepeatDue(dueKey, trigger.Cooldown)) continue;
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersExitedScan", GetScanSourceName(scan), puzzleIndex, eventPlayerCount, suffix, GetScanRuntimePosition(scan));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"ProcessScanRepeatEvents failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void DumpScanIndexesIfNeeded()
    {
        if (_dumpedScanIndexes || !GameStateManager.IsInExpedition) return;
        if (Time.realtimeSinceStartup - _lastScanDumpTryTime < 3.0f) return;
        _lastScanDumpTryTime = Time.realtimeSinceStartup;

        int scans = 0;
        try
        {
            foreach (CP_Bioscan_Core scan in UnityEngine.Object.FindObjectsOfType<CP_Bioscan_Core>())
            {
                if (scan == null) continue;
                scans++;
                ScanBindingInfo binding = GetScanBindingInfo(scan);
                Runtime.LogVerbose($"CTE Scan Index={binding.Index} Core={GetInstanceId(scan)} PuzzleIndex=<not-used> PuzzleOverrideIndex={binding.Index} Source={binding.Source} Name='{SafeScanObjectName(scan)}' ScanSource='{GetScanSourceName(scan)}' PlayersInScan={GetPlayersInScan(scan)} Status='{SafeScanStringMember(scan, "m_state", "State", "Status", "m_status")}' Position={SafeScanPosition(scan)}");
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Scan index dump failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (scans > 0)
        {
            _dumpedScanIndexes = true;
            Runtime.LogVerbose($"CTE scan binding dump complete. Scans={scans}. Use BepInEx ScanPosOverride PuzzleOverrideIndex for ScanTriggers[Index].");
        }
    }

    internal static void OnScanActivated(CP_Bioscan_Core? scan)
    {
        if (scan == null)
        {
            return;
        }

        try
        {
            int instanceId = GetInstanceId(scan);
            ActiveScanRefs[instanceId] = scan;
            ScanRuntimeState state = GetOrCreateScanState(instanceId);
            if (state.ActivatedThisCycle)
            {
                return;
            }

            if (ActivationFiredThisCycle.Contains(instanceId))
            {
                return;
            }
            if (LastActivationTimes.TryGetValue(instanceId, out float last) && Time.realtimeSinceStartup - last < 0.25f)
            {
                return;
            }
            LastActivationTimes[instanceId] = Time.realtimeSinceStartup;
            ActivationFiredThisCycle.Add(instanceId);

            ScanBindingInfo binding = GetScanBindingInfo(scan);
            int puzzleIndex = binding.Index;
            int playersInScan = GetPlayersInScan(scan);
            state.IsActive = true;
            state.ActivatedThisCycle = true;
            state.ExitTriggeredThisCycle = false;
            state.HadPlayersInside = playersInScan > 0;
            state.HasObserved = true;
            state.LastPlayerCount = playersInScan;
            state.ActivationPlayerCountEventsFired = playersInScan > 0;
            string suffix = $"{instanceId}:activated";

            Runtime.LogVerbose($"Detected scan activation. Index={puzzleIndex}, BindingSource={binding.Source}, PlayersInScan={playersInScan}, Source={GetScanSourceName(scan)}");

            // 0.9.6 修复：CP_Bioscan_Core.Activate 经常先在 PlayersInScan=0 时触发，
            // 随后 Master_OnPlayerScanChangedCheckProgress / OnSyncStateChange 才同步真实玩家数量。
            // 以前这里会先执行一次 OnScanActivated 事件，随后玩家数量从 0->1 又执行一次，导致“激活扫描点触发两次”。
            // 现在无人时只打开扫描周期并等待第一次 1-4 人边沿；有人时才立即执行事件。
            if (playersInScan <= 0)
            {
                Runtime.LogVerbose($"Detected scan activation with no players. Waiting for first player-count edge before firing OnScanActivated. Index={puzzleIndex}, BindingSource={binding.Source}");
                return;
            }

            foreach ((ConfigDocument config, ScanTriggerRule trigger) in Runtime.GetActiveScanTriggers())
            {
                Runtime.FireScanTrigger(config, trigger, "OnScanActivated", GetScanSourceName(scan), puzzleIndex, playersInScan, suffix, GetScanRuntimePosition(scan));
            }

            HandleAllPlayersScanState(scan, state, 0, playersInScan, "Activate");
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"OnScanActivated failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void OnScanPlayersChanged(CP_Bioscan_Core? scan)
    {
        if (scan == null)
        {
            return;
        }

        OnScanPlayersChanged(scan, GetPlayersInScan(scan), "property");
    }

    internal static void OnScanPlayersChanged(CP_Bioscan_Core? scan, int currentPlayers, string source)
    {
        if (scan == null)
        {
            return;
        }

        try
        {
            int instanceId = GetInstanceId(scan);
            ActiveScanRefs[instanceId] = scan;
            ScanBindingInfo binding = GetScanBindingInfo(scan);
            int puzzleIndex = binding.Index;
            if (currentPlayers < 0)
            {
                currentPlayers = GetPlayersInScan(scan);
            }

            ScanRuntimeState state = GetOrCreateScanState(instanceId);

            if (IsScanClosedOrFinishedSource(source))
            {
                if (state.IsActive || state.HasObserved || state.LastPlayerCount > 0)
                {
                    Runtime.LogVerbose($"Detected scan end state. Closing current scan trigger cycle without firing events. Index={puzzleIndex}, BindingSource={binding.Source}, PlayersInScan={currentPlayers}, Source={source}");
                }
                CloseScanCycle(instanceId, state);
                return;
            }

            if (!state.IsActive && !state.HasObserved)
            {
                // 扫描点尚未通过 OnScanActivated 进入激活状态时，玩家数量同步只作为基线记录。
                state.HasObserved = true;
                state.LastPlayerCount = currentPlayers;
                if (currentPlayers > 0) state.HadPlayersInside = true;
                Runtime.LogVerbose($"Ignored scan player count change before activation. Index={puzzleIndex}, BindingSource={binding.Source}, Current={currentPlayers}, Source={source}");
                return;
            }

            int previousPlayers = state.HasObserved ? state.LastPlayerCount : 0;
            state.HasObserved = true;
            state.IsActive = true;
            state.LastPlayerCount = currentPlayers;
            if (currentPlayers > 0)
            {
                state.HadPlayersInside = true;
            }

            HandleAllPlayersScanState(scan, state, previousPlayers, currentPlayers, source);

            if (currentPlayers == previousPlayers)
            {
                return;
            }

            if (currentPlayers > previousPlayers)
            {
                state.ExitTriggeredThisCycle = false;
                Runtime.LogVerbose($"Detected scan player count increase. Event=OnScanActivated, Index={puzzleIndex}, BindingSource={binding.Source}, Previous={previousPlayers}, Current={currentPlayers}, Source={source}");

                // 0人->有人，或扫描内人数发生变化时，允许 OnScanActivated 按当前人数重复触发。
                // 这样玩家反复进入/退出同一个仍处于激活状态的扫描点时，不会被 ActivatedThisCycle 永久拦截。
                if (currentPlayers >= 1 && currentPlayers <= 4)
                {
                    state.ActivationPlayerCountEventsFired = true;
                    string activationSuffix = $"{instanceId}:activated-playercount:{++state.ActivationEdgeSequence}:{previousPlayers}->{currentPlayers}";
                    Runtime.LogVerbose($"Detected scan activation player count edge. Event=OnScanActivated, Index={puzzleIndex}, BindingSource={binding.Source}, PlayersInScan={currentPlayers}, Previous={previousPlayers}, Source={source}");
                    foreach ((ConfigDocument config, ScanTriggerRule trigger) in Runtime.GetActiveScanTriggers())
                    {
                        Runtime.FireScanTrigger(config, trigger, "OnScanActivated", GetScanSourceName(scan), puzzleIndex, currentPlayers, activationSuffix, GetScanRuntimePosition(scan));
                    }
                }
                return;
            }

            if (previousPlayers <= 0 || currentPlayers != 0 || !state.HadPlayersInside)
            {
                return;
            }

            state.ExitTriggeredThisCycle = true;
            string eventName = "OnPlayerExitScan";
            string suffix = $"{instanceId}:{eventName}:{++state.ExitEdgeSequence}:{previousPlayers}->{currentPlayers}";

            Runtime.LogVerbose($"Detected scan player count decrease. Event={eventName}, Index={puzzleIndex}, BindingSource={binding.Source}, Previous={previousPlayers}, Current={currentPlayers}, Source={source}");

            foreach ((ConfigDocument config, ScanTriggerRule trigger) in Runtime.GetActiveScanTriggers())
            {
                // 退出扫描时 currentPlayers 已经是 0；如果 UsePlayerCountEvents=true，
                // 应使用退出前的玩家数量 previousPlayers 来选择 1/2/3/4 人事件组。
                Runtime.FireScanTrigger(config, trigger, eventName, GetScanSourceName(scan), puzzleIndex, previousPlayers, suffix, GetScanRuntimePosition(scan));
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"OnScanPlayersChanged failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void HandleAllPlayersScanState(CP_Bioscan_Core scan, ScanRuntimeState state, int previousPlayers, int currentPlayers, string source)
    {
        try
        {
            int eligiblePlayers = CountEligibleScanPlayers();
            if (eligiblePlayers <= 0)
            {
                return;
            }

            int instanceId = GetInstanceId(scan);
            ActiveScanRefs[instanceId] = scan;
            ScanBindingInfo binding = GetScanBindingInfo(scan);
            int puzzleIndex = binding.Index;
            bool wasAllInside = state.AllPlayersInsideNow || previousPlayers >= eligiblePlayers;
            bool nowAllInside = currentPlayers >= eligiblePlayers;

            if (nowAllInside)
            {
                state.AllPlayersEnteredThisCycle = true;
                state.AllPlayersExitedRepeatActive = false;
                if (!wasAllInside)
                {
                    state.AllPlayersInsideNow = true;
                    string suffix = $"{instanceId}:allplayersenter:{++state.AllPlayersEnterEdgeSequence}";
                    foreach ((ConfigDocument config, ScanTriggerRule trigger) in Runtime.GetActiveScanTriggers())
                    {
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersEnterScan", GetScanSourceName(scan), puzzleIndex, Math.Min(Math.Max(currentPlayers, 1), 4), suffix, GetScanRuntimePosition(scan));
                    }
                }
                else
                {
                    state.AllPlayersInsideNow = true;
                }
                return;
            }

            if (state.AllPlayersInsideNow && currentPlayers < eligiblePlayers)
            {
                state.AllPlayersInsideNow = false;
            }

            if (state.AllPlayersEnteredThisCycle && previousPlayers > 0 && currentPlayers == 0)
            {
                state.AllPlayersEnteredThisCycle = false;
                state.AllPlayersExitedRepeatActive = true;
                string suffix = $"{instanceId}:allplayersexit:{++state.AllPlayersExitEdgeSequence}";
                int eventPlayerCount = Math.Min(Math.Max(previousPlayers, eligiblePlayers), 4);
                foreach ((ConfigDocument config, ScanTriggerRule trigger) in Runtime.GetActiveScanTriggers())
                {
                    Runtime.FireScanTrigger(config, trigger, "OnAllPlayersExitScan", GetScanSourceName(scan), puzzleIndex, eventPlayerCount, suffix, GetScanRuntimePosition(scan));
                }
                Runtime.LogVerbose($"Detected all players exit scan. Index={puzzleIndex}, EligiblePlayers={eligiblePlayers}, Previous={previousPlayers}, Current={currentPlayers}, Source={source}");
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"HandleAllPlayersScanState failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int CountEligibleScanPlayers()
    {
        int count = 0;
        var list = PlayerManager.PlayerAgentsInLevel;
        if (list == null) return 0;
        for (int i = 0; i < list.Count; i++)
        {
            try
            {
                PlayerAgent? agent = list[i];
                if (agent != null && agent.Alive) count++;
            }
            catch { }
        }
        return count;
    }

    internal static void OnScanStoppedOrTimedOut(CP_Bioscan_Core? scan, string source)
    {
        if (scan == null)
        {
            return;
        }

        try
        {
            int instanceId = GetInstanceId(scan);
            ActivationFiredThisCycle.Remove(instanceId);
            LastActivationTimes.Remove(instanceId);
            ScanRuntimeState state = GetOrCreateScanState(instanceId);

            if (!state.HasObserved)
            {
                ResetScanCycle(state);
                return;
            }

            if (state.LastPlayerCount > 0)
            {
                Runtime.LogVerbose($"Scan stopped or completed while players were inside. Closing scan cycle without firing OnPlayerExitScan. Source={source}, PreviousPlayers={state.LastPlayerCount}");
            }
            CloseScanCycle(instanceId, state);
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"OnScanStoppedOrTimedOut failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsScanClosedOrFinishedSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        string text = source.Trim().ToLowerInvariant();
        return text.Contains("finished")
            || text.Contains("finish")
            || text.Contains("complete")
            || text.Contains("completed")
            || text.Contains("solved")
            || text.Contains("success")
            || text.Contains("done")
            || text.Contains("inactive")
            || text.Contains("timeout")
            || text.Contains("timedout")
            || text.Contains("deactivate")
            || text.Contains("disabled");
    }

    private static void CloseScanCycle(int instanceId, ScanRuntimeState state)
    {
        ActivationFiredThisCycle.Remove(instanceId);
        LastActivationTimes.Remove(instanceId);
        ResetScanCycle(state);
    }

    private static ScanRuntimeState GetOrCreateScanState(int instanceId)
    {
        if (!ScanStates.TryGetValue(instanceId, out ScanRuntimeState? state))
        {
            state = new ScanRuntimeState();
            ScanStates[instanceId] = state;
        }

        return state;
    }

    private static void ResetScanCycle(ScanRuntimeState state)
    {
        state.IsActive = false;
        state.ActivatedThisCycle = false;
        state.ActivationPlayerCountEventsFired = false;
        state.HadPlayersInside = false;
        state.ExitTriggeredThisCycle = false;
        state.ActivationEdgeSequence = 0;
        state.ExitEdgeSequence = 0;
        state.AllPlayersInsideNow = false;
        state.AllPlayersEnteredThisCycle = false;
        state.AllPlayersExitedRepeatActive = false;
        state.AllPlayersEnterEdgeSequence = 0;
        state.AllPlayersExitEdgeSequence = 0;
        state.HasObserved = false;
        state.LastPlayerCount = 0;
        state.Fired = false;
        state.LastFireTime = -999999f;
    }

    internal static CP_Bioscan_Core? TryFindCoreFromScanner(CP_PlayerScanner? scanner)
    {
        if (scanner == null)
        {
            return null;
        }

        try
        {
            CP_Bioscan_Core core = scanner.GetComponent<CP_Bioscan_Core>();
            if (core != null) return core;
        }
        catch { }

        try
        {
            CP_Bioscan_Core core = scanner.GetComponentInParent<CP_Bioscan_Core>();
            if (core != null) return core;
        }
        catch { }

        try
        {
            CP_Bioscan_Core core = scanner.GetComponentInChildren<CP_Bioscan_Core>();
            if (core != null) return core;
        }
        catch { }

        return null;
    }

    private static int GetInstanceId(CP_Bioscan_Core scan)
    {
        try { return scan.GetInstanceID(); } catch { return scan.Pointer.GetHashCode(); }
    }

    private readonly struct ScanBindingInfo
    {
        public readonly int Index;
        public readonly string Source;

        public ScanBindingInfo(int index, string source)
        {
            Index = index;
            Source = source;
        }
    }

    // ScanTriggers[Index] 的唯一正式语义：绑定 BepInEx 控制台
    // "Debug  :ScanPosOverride, PuzzleOverrideIndex: X" 中的 PuzzleOverrideIndex。
    // 0.8.17 起不再回退到 CP_Bioscan_Core.m_puzzleIndex，避免把内部枚举/原版 puzzleIndex
    // 误当作 ScanPositionOverride 的 PuzzleOverrideIndex 使用。读取不到时返回 -1 并跳过匹配。
    private static ScanBindingInfo GetScanBindingInfo(CP_Bioscan_Core scan)
    {
        if (SpoIndexResolver.TryGetScanSpoIndex(scan, out int managerIndex, out string managerSource))
        {
            return new ScanBindingInfo(managerIndex, managerSource);
        }

        if (TryGetPuzzleOverrideIndexFromScanPosOverride(scan, out int overrideIndex, out string overrideSource))
        {
            return new ScanBindingInfo(overrideIndex, overrideSource);
        }

        return new ScanBindingInfo(-1, "Missing:ScanPosOverride.PuzzleOverrideIndex");
    }

    private static bool TryGetPuzzleOverrideIndexFromScanPosOverride(CP_Bioscan_Core scan, out int index, out string source)
    {
        index = -1;
        source = string.Empty;

        foreach (Component component in EnumerateScanRelatedComponents(scan))
        {
            if (component == null) continue;
            string typeName = SafeTypeName(component);
            if (typeName.IndexOf("ScanPosOverride", StringComparison.OrdinalIgnoreCase) < 0
                && typeName.IndexOf("ScanPositionOverride", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (TryReadIntMember(component, out index, "PuzzleOverrideIndex", "puzzleOverrideIndex", "m_puzzleOverrideIndex"))
            {
                source = $"{typeName}.PuzzleOverrideIndex";
                return true;
            }
        }

        // 兼容某些构建中字段被挂在 CP_Bioscan_Core 或同层组件上，但类型名不含 ScanPosOverride 的情况。
        foreach (Component component in EnumerateScanRelatedComponents(scan))
        {
            if (component == null) continue;
            if (TryReadIntMember(component, out index, "PuzzleOverrideIndex", "puzzleOverrideIndex", "m_puzzleOverrideIndex"))
            {
                source = $"{SafeTypeName(component)}.PuzzleOverrideIndex";
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Component> EnumerateScanRelatedComponents(CP_Bioscan_Core scan)
    {
        yield return scan;

        Il2CppArrayBase<Component>? selfComponents = null;
        try { selfComponents = scan.GetComponents<Component>(); } catch { }
        if (selfComponents != null)
        {
            foreach (Component c in selfComponents) if (c != null) yield return c;
        }

        Il2CppArrayBase<Component>? parentComponents = null;
        try { parentComponents = scan.GetComponentsInParent<Component>(true); } catch { }
        if (parentComponents != null)
        {
            foreach (Component c in parentComponents) if (c != null) yield return c;
        }

        Il2CppArrayBase<Component>? childComponents = null;
        try { childComponents = scan.GetComponentsInChildren<Component>(true); } catch { }
        if (childComponents != null)
        {
            foreach (Component c in childComponents) if (c != null) yield return c;
        }
    }

    private static bool TryReadIntMember(object obj, out int value, params string[] names)
    {
        value = -1;
        Type type = obj.GetType();
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? raw = prop?.GetValue(obj);
                if (raw != null && int.TryParse(raw.ToString(), out value)) return true;
            }
            catch { }

            try
            {
                FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? raw = field?.GetValue(obj);
                if (raw != null && int.TryParse(raw.ToString(), out value)) return true;
            }
            catch { }
        }
        return false;
    }

    private static string SafeTypeName(object obj)
    {
        try { return obj.GetType().FullName ?? obj.GetType().Name; } catch { return "<unknown>"; }
    }

    private static int GetInternalPuzzleIndex(CP_Bioscan_Core scan)
    {
        try { return scan.m_puzzleIndex; } catch { }
        try
        {
            PropertyInfo? prop = scan.GetType().GetProperty("m_puzzleIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? value = prop?.GetValue(scan);
            if (value is int i) return i;
            if (value != null && int.TryParse(value.ToString(), out i)) return i;
        }
        catch { }
        return -1;
    }

    private static int GetPlayersInScan(CP_Bioscan_Core scan)
    {
        try
        {
            var players = scan.PlayersOnScan;
            if (players != null)
            {
                return players.Count;
            }
        }
        catch { }

        try
        {
            return scan.m_currentPlayerCount;
        }
        catch { }

        return 0;
    }


    private static string SafeScanObjectName(Component source)
    {
        try { return source.gameObject != null ? source.gameObject.name : source.ToString(); } catch { return source.ToString(); }
    }

    private static string SafeScanPosition(Component source)
    {
        try
        {
            Vector3 p = source.transform.position;
            return $"({p.x:F2},{p.y:F2},{p.z:F2})";
        }
        catch { return "<unknown>"; }
    }


    private static Vector3? GetScanRuntimePosition(Component source)
    {
        // 1.1.6: Prefer a real player position for scan-triggered AWO Closest/Position fallback events.
        // CP_Bioscan_Core root transforms can be zero or not representative of the scanner world position,
        // which caused AWO SetPocketItem(TagType=Closest) to receive Vector3.zero.
        try
        {
            CP_Bioscan_Core? scan = source as CP_Bioscan_Core ?? TryFindCoreFromScanner(source as CP_PlayerScanner);
            if (scan != null)
            {
                try
                {
                    var players = scan.PlayersOnScan;
                    if (players != null)
                    {
                        int count = players.Count;
                        for (int i = 0; i < count; i++)
                        {
                            PlayerAgent? agent = null;
                            try { agent = players[i]; } catch { }
                            if (agent != null && agent.Alive && IsUsableScanContextPosition(agent.Position))
                            {
                                return agent.Position;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            if (source != null && source.transform != null && IsUsableScanContextPosition(source.transform.position))
            {
                return source.transform.position;
            }
        }
        catch { }

        try
        {
            foreach (PlayerAgent agent in PlayerManager.PlayerAgentsInLevel)
            {
                if (agent != null && agent.Alive && IsUsableScanContextPosition(agent.Position))
                {
                    return agent.Position;
                }
            }
        }
        catch { }

        return null;
    }

    private static bool IsUsableScanContextPosition(Vector3 position)
    {
        return !(Math.Abs(position.x) < 0.001f && Math.Abs(position.y) < 0.001f && Math.Abs(position.z) < 0.001f);
    }

    private static string SafeScanStringMember(object obj, params string[] names)
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

    private static string GetScanSourceName(CP_Bioscan_Core scan)
    {
        try
        {
            var owner = scan.Owner;
            if (owner != null)
            {
                return owner.ToString() ?? string.Empty;
            }
        }
        catch { }
        return $"CP_Bioscan_Core#{GetInstanceId(scan)}";
    }
}

[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Activate))]
internal static class CP_Bioscan_Core_Activate_Patch
{
    private static void Postfix(CP_Bioscan_Core __instance)
    {
        ScanTriggerManager.OnScanActivated(__instance);
    }
}

[HarmonyPatch(typeof(CP_PlayerScanner), nameof(CP_PlayerScanner.StartScan))]
internal static class CP_PlayerScanner_StartScan_Patch
{
    private static void Postfix(CP_PlayerScanner __instance)
    {
        ScanTriggerManager.OnScanActivated(ScanTriggerManager.TryFindCoreFromScanner(__instance));
    }
}

[HarmonyPatch(typeof(CP_PlayerScanner), nameof(CP_PlayerScanner.StopScan))]
internal static class CP_PlayerScanner_StopScan_Patch
{
    private static void Postfix(CP_PlayerScanner __instance)
    {
        ScanTriggerManager.OnScanStoppedOrTimedOut(ScanTriggerManager.TryFindCoreFromScanner(__instance), "CP_PlayerScanner.StopScan");
    }
}

[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Deactivate))]
internal static class CP_Bioscan_Core_Deactivate_Patch
{
    private static void Postfix(CP_Bioscan_Core __instance)
    {
        ScanTriggerManager.OnScanStoppedOrTimedOut(__instance, "CP_Bioscan_Core.Deactivate");
    }
}

[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.SetTimeOut))]
internal static class CP_Bioscan_Core_SetTimeOut_Patch
{
    private static void Postfix(CP_Bioscan_Core __instance)
    {
        ScanTriggerManager.OnScanStoppedOrTimedOut(__instance, "CP_Bioscan_Core.SetTimeOut");
    }
}

[HarmonyPatch(typeof(CP_Bioscan_Core), "Master_OnPlayerScanChangedCheckProgress")]
internal static class CP_Bioscan_Core_Master_OnPlayerScanChangedCheckProgress_Patch
{
    private static void Postfix(CP_Bioscan_Core __instance, float scanProgress, Il2CppSystem.Collections.Generic.List<PlayerAgent> playersInScan, int inScanMax, Il2CppStructArray<bool> reqObjsInScan)
    {
        int count = -1;
        try { if (playersInScan != null) count = playersInScan.Count; } catch { }
        ScanTriggerManager.OnScanPlayersChanged(__instance, count, "Master_OnPlayerScanChangedCheckProgress");
    }
}

[HarmonyPatch(typeof(CP_Bioscan_Core), "OnSyncStateChange")]
internal static class CP_Bioscan_Core_OnSyncStateChange_Patch
{
    private static void Postfix(CP_Bioscan_Core __instance, eBioscanStatus status, float progress, Il2CppSystem.Collections.Generic.List<PlayerAgent> playersInScan, int playersMax, Il2CppStructArray<bool> reqItemStatus, bool isDropinState)
    {
        int count = -1;
        try { if (playersInScan != null) count = playersInScan.Count; } catch { }
        ScanTriggerManager.OnScanPlayersChanged(__instance, count, $"OnSyncStateChange:{status}");
    }
}



