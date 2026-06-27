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
    internal static ManualLogSource? Log;
    // 默认静默：不再把配置加载、索引 dump、区域解析、触发成功等诊断信息输出到 BepInEx 控制台。
    // 仅保留 Warning / Error，避免开启 Debug Marker 时刷屏和卡顿。
    internal static readonly bool VerboseConsoleLogging = false;
    internal static void LogVerbose(string message)
    {
        if (VerboseConsoleLogging)
        {
            Log?.LogInfo(message);
        }
    }
    private static readonly Dictionary<string, TriggerState> States = new(StringComparer.OrdinalIgnoreCase);
    // Keep IL2CPP LocalizedText wrappers rooted after creation. Without this, BepInEx/Il2CppInterop
    // can report "Object was garbage collected in IL2CPP domain" when WardenIntel is built from JSON.
    private static readonly List<LocalizedText> LocalizedTextRoots = new();
    // 1.1.3：参考 Inas / EOS SecuritySensor 的事件执行方式：
    // SecuritySensor 在定义读取阶段将 EventsOnTrigger 解析成 List<WardenObjectiveEventData>，运行时只执行已经解析好的事件。
    // CTE 仍保留 JsonElement 配置结构以兼容旧模板，但会把 WardenEvent 构建结果缓存起来，避免每次触发都重新解析 JSON。
    // AWO 事件通过 EOSJson / InjectLibJSON 构建后必须带 WEEData；Legacy/EOS 字符串事件也优先走 EOSJson。
    private sealed class CachedWardenEventBuild
    {
        public bool Success;
        public WardenObjectiveEventData? EventData;
        public string Failure = string.Empty;
        public bool FailureLogged;
    }
    private static readonly Dictionary<string, CachedWardenEventBuild> WardenEventBuildCache = new(StringComparer.Ordinal);
    private const int WardenEventBuildCacheLimit = 4096;
    private static float _lastTickTime;
    private static float _lastLogTime;
    private static readonly Dictionary<string, float> LastLogTimesByMessage = new(StringComparer.Ordinal);
    // 1.1.0：热路径字符串标准化缓存。持续触发器每 0.2 秒都会匹配 TriggerMode/TargetType，
    // 以前每次都会 Trim/Replace/ToLowerInvariant 产生分配；现在按原始字符串缓存结果，不改变任何配置语义。
    private static readonly Dictionary<string, string> PositionModeNormalizationCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ScanModeNormalizationCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> InteractModeNormalizationCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TargetTypeNormalizationCache = new(StringComparer.Ordinal);
    private const int NormalizationCacheLimit = 512;
    // 1.1.0：关卡字符串 ID / MTFO PartialData persistentID 缓存。配置重载或远征切换时清空，
    // 避免同一批配置在多次匹配时重复反射 manager 或扫描 _persistentID.json。
    private static readonly Dictionary<string, uint> PartialDataPersistentIdCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, uint> LevelLayoutStringIdCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, uint>? PersistentIdDumpCache;
    // 0.8.10：终端选择器缓存。玩家按下终端时不再扫描 LG_Zone / 反射字段，避免交互瞬间卡顿。
    private static readonly Dictionary<int, HashSet<string>> TerminalSelectorCache = new();
    // 1.0.7：终端字段 / AWO TSL 解析缓存。只在远征 Tick 或受控 fallback 中预热，避免玩家使用/退出终端时同步扫描 LG_Zone。
    private static readonly Dictionary<int, TerminalTslAddress> TerminalTslAddressCache = new();
    private static readonly Dictionary<string, bool> TerminalSelectorMatchCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> TerminalSelectorCacheMisses = new();
    private static bool TerminalSelectorCacheWarmupComplete;
    private static float LastTerminalSelectorCacheWarmupAttempt = -999999f;
    public const int MaxTerminalSyncSelectorBytes = 96;
    private const string TerminalTriggerSyncEventName = PluginInfo.GUID + ".terminal_trigger_event.v1";
    private const float TerminalSyncMessageLifetimeSeconds = 20.0f;
    private static bool TerminalTriggerSyncRegistered;
    private static bool IsReceivingTerminalTriggerSync;
    private static int NextTerminalSyncMessageId = 1;
    private static readonly Dictionary<string, float> ReceivedTerminalSyncMessages = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ZoneLookupCacheEntry> ZoneLookupCache = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.6：缓存当前关卡已启用触发器，避免 Runtime.Tick / 持续触发器每 0.2 秒重复遍历所有配置文件并分配 List。
    private static readonly object ActiveTriggerCacheLock = new();
    private static readonly List<(ConfigDocument Config, PositionTriggerRule Trigger)> ActivePositionTriggerCache = new();
    private static readonly List<(ConfigDocument Config, ScanTriggerRule Trigger)> ActiveScanTriggerCache = new();
    private static readonly List<(ConfigDocument Config, InteractTriggerRule Trigger)> ActiveInteractTriggerCache = new();
    private static readonly List<(ConfigDocument Config, HudInteractTriggerRule Trigger)> ActiveHudInteractTriggerCache = new();
    private static bool ActiveTriggerCacheDirty = true;
    private static uint ActiveTriggerCacheLayoutId = uint.MaxValue;
    private static string ActiveTriggerCacheLayoutName = string.Empty;
    public const int MaxHudInteractTriggerIdBytes = 96;
    private const string HudInteractRequestEventName = PluginInfo.GUID + ".hud_interact_request.v1";
    private const string HudInteractConfirmEventName = PluginInfo.GUID + ".hud_interact_confirm.v1";
    private static bool HudInteractSyncRegistered;

    private sealed class ZoneLookupCacheEntry
    {
        public readonly List<LG_Zone> Zones = new();
        public readonly List<(LG_Zone Zone, object Area, int Index)> Areas = new();
        public bool Resolved;
        public float LastAttemptTime = -999999f;
        public string LastFailure = string.Empty;
    }

    internal static void MarkActiveTriggerCacheDirty()
    {
        lock (ActiveTriggerCacheLock)
        {
            ActiveTriggerCacheDirty = true;
        }
    }

    internal static void ClearConfigurationResolutionCaches()
    {
        PartialDataPersistentIdCache.Clear();
        LevelLayoutStringIdCache.Clear();
        PersistentIdDumpCache = null;
        WardenEventBuildCache.Clear();
    }

    internal static void SetupTerminalTriggerSync()
    {
        if (TerminalTriggerSyncRegistered) return;
        try
        {
            NetworkAPI.RegisterEvent<CteTerminalTriggerPacket>(TerminalTriggerSyncEventName, OnReceiveTerminalTriggerSync);
            TerminalTriggerSyncRegistered = true;
            LogVerbose($"Registered CTE terminal trigger sync event '{TerminalTriggerSyncEventName}'.");
        }
        catch (Exception ex)
        {
            LogThrottled($"Failed to register CTE terminal trigger sync event '{TerminalTriggerSyncEventName}': {DescribeException(ex)}");
        }
    }

    internal static void SetupHudInteractTriggerSync()
    {
        if (HudInteractSyncRegistered) return;
        try
        {
            NetworkAPI.RegisterEvent<HudInteractTriggerRequestPacket>(HudInteractRequestEventName, HudInteractTriggerManager.OnReceiveRequest);
            NetworkAPI.RegisterEvent<HudInteractTriggerConfirmPacket>(HudInteractConfirmEventName, HudInteractTriggerManager.OnReceiveConfirm);
            HudInteractSyncRegistered = true;
            LogVerbose($"Registered CTE HUD interact sync events '{HudInteractRequestEventName}' and '{HudInteractConfirmEventName}'.");
        }
        catch (Exception ex)
        {
            LogThrottled($"Failed to register CTE HUD interact sync events: {DescribeException(ex)}");
        }
    }

    internal static void BroadcastHudInteractRequest(HudInteractTriggerRequestPacket packet)
    {
        if (!HudInteractSyncRegistered) return;
        try
        {
            NetworkAPI.InvokeEvent(HudInteractRequestEventName, packet, SNet_ChannelType.GameOrderCritical);
        }
        catch (Exception ex)
        {
            LogThrottled($"Failed to broadcast CTE HUD interact request: {DescribeException(ex)}");
        }
    }

    internal static void BroadcastHudInteractConfirm(HudInteractTriggerConfirmPacket packet)
    {
        if (!HudInteractSyncRegistered) return;
        try
        {
            NetworkAPI.InvokeEvent(HudInteractConfirmEventName, packet, SNet_ChannelType.GameOrderCritical);
        }
        catch (Exception ex)
        {
            LogThrottled($"Failed to broadcast CTE HUD interact confirm: {DescribeException(ex)}");
        }
    }

    private static string GetCachedNormalization(Dictionary<string, string> cache, string? text, Func<string, string> normalize)
    {
        string key = text ?? string.Empty;
        if (cache.TryGetValue(key, out string? cached) && cached != null) return cached;
        string normalized = normalize(key);
        if (cache.Count < NormalizationCacheLimit) cache[key] = normalized;
        return normalized;
    }

    private static void ClearHotPathRuntimeCaches()
    {
        if (PositionModeNormalizationCache.Count > NormalizationCacheLimit) PositionModeNormalizationCache.Clear();
        if (ScanModeNormalizationCache.Count > NormalizationCacheLimit) ScanModeNormalizationCache.Clear();
        if (InteractModeNormalizationCache.Count > NormalizationCacheLimit) InteractModeNormalizationCache.Clear();
        if (TargetTypeNormalizationCache.Count > NormalizationCacheLimit) TargetTypeNormalizationCache.Clear();
    }

    private static void EnsureActiveTriggerCache()
    {
        uint layoutId = GetCurrentLevelLayoutId();
        string layoutName = TryGetLevelLayoutName(layoutId);

        lock (ActiveTriggerCacheLock)
        {
            if (!ActiveTriggerCacheDirty && ActiveTriggerCacheLayoutId == layoutId && string.Equals(ActiveTriggerCacheLayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ActivePositionTriggerCache.Clear();
            ActiveScanTriggerCache.Clear();
            ActiveInteractTriggerCache.Clear();
            ActiveHudInteractTriggerCache.Clear();

            foreach (ConfigDocument config in ConfigManager.Configs)
            {
                if (!config.Enabled)
                {
                    continue;
                }

                if (!MatchesCurrentLevel(config, layoutId, layoutName, out _))
                {
                    continue;
                }

                foreach (PositionTriggerRule trigger in config.PositionTriggers)
                {
                    if (trigger.Enabled)
                    {
                        ActivePositionTriggerCache.Add((config, trigger));
                    }
                }

                foreach (ScanTriggerRule trigger in config.ScanTriggers)
                {
                    if (trigger.Enabled)
                    {
                        ActiveScanTriggerCache.Add((config, trigger));
                    }
                }

                foreach (InteractTriggerRule trigger in config.InteractTriggers)
                {
                    if (trigger.Enabled)
                    {
                        ActiveInteractTriggerCache.Add((config, trigger));
                    }
                }

                foreach (HudInteractTriggerRule trigger in config.HudInteractTriggers)
                {
                    if (trigger.Enabled)
                    {
                        ActiveHudInteractTriggerCache.Add((config, trigger));
                    }
                }
            }

            ActiveTriggerCacheLayoutId = layoutId;
            ActiveTriggerCacheLayoutName = layoutName;
            ActiveTriggerCacheDirty = false;

            PrimeWardenEventCacheForActiveTriggers();
        }
    }

    private static void PrimeWardenEventCacheForActiveTriggers()
    {
        // Mirrors SecuritySensor's pre-parsed EventsOnTrigger behavior as closely as possible without breaking CTE's JsonElement config compatibility.
        // This only primes events for currently active triggers; disabled triggers still use the same cache lazily when enabled at runtime.
        foreach ((ConfigDocument _, PositionTriggerRule trigger) in ActivePositionTriggerCache)
        {
            PrimeEventList(trigger.Events);
            PrimeEventList(trigger.WardenEvents);
            PrimeEventList(trigger.OnePlayerEvents);
            PrimeEventList(trigger.TwoPlayerEvents);
            PrimeEventList(trigger.ThreePlayerEvents);
            PrimeEventList(trigger.FourPlayerEvents);
            PrimeEventList(trigger.TriggerCycleEvents);
        }

        foreach ((ConfigDocument _, ScanTriggerRule trigger) in ActiveScanTriggerCache)
        {
            PrimeEventList(trigger.Events);
            PrimeEventList(trigger.WardenEvents);
            PrimeEventList(trigger.OnePlayerEvents);
            PrimeEventList(trigger.TwoPlayerEvents);
            PrimeEventList(trigger.ThreePlayerEvents);
            PrimeEventList(trigger.FourPlayerEvents);
            PrimeEventList(trigger.TriggerCycleEvents);
        }

        foreach ((ConfigDocument _, InteractTriggerRule trigger) in ActiveInteractTriggerCache)
        {
            PrimeEventList(trigger.Events);
            PrimeEventList(trigger.WardenEvents);
            PrimeEventList(trigger.PickupDropCycleEvents);
        }

        foreach ((ConfigDocument _, HudInteractTriggerRule trigger) in ActiveHudInteractTriggerCache)
        {
            PrimeEventList(trigger.Events);
            PrimeEventList(trigger.WardenEvents);
            PrimeEventList(trigger.CancelEvents);
            foreach (HudInteractProgressEventRule progressEvent in trigger.ProgressEvents)
            {
                PrimeEventList(progressEvent.Events);
            }
        }
    }

    private static void PrimeEventList(IEnumerable<JsonElement> events)
    {
        foreach (JsonElement element in events)
        {
            if (element.ValueKind != JsonValueKind.Object || IsTriggerControlEvent(element))
            {
                continue;
            }

            TryGetCachedWardenEvent(element, out _, "CTE event precompile");
        }
    }

    internal static void OnExpeditionStarted()
    {
        States.Clear();
        LocalizedTextRoots.Clear();
        ZoneLookupCache.Clear();
        LastLogTimesByMessage.Clear();
        PendingConfiguredEvents.Clear();
        WardenEventBuildCache.Clear();
        ReceivedTerminalSyncMessages.Clear();
        ClearHotPathRuntimeCaches();
        ClearTerminalSelectorCache();
        ConfigManager.LoadOrCreate(Log, true);
        MarkActiveTriggerCacheDirty();
        EnsureActiveTriggerCache();
        uint id = GetCurrentLevelLayoutId();
        string name = TryGetLevelLayoutName(id);
        int count = GetActiveTriggers().Count;
        int scanCount = GetActiveScanTriggers().Count;
        int interactCount = GetActiveInteractTriggers().Count;
        ScanTriggerManager.Reset();
        InteractTriggerManager.Reset();
        HudInteractTriggerManager.Reset();
        LogVerbose($"Expedition started. LevelLayoutID={id}, LevelLayoutName='{name}', active coordinate triggers={count}, active scan triggers={scanCount}, active interact triggers={interactCount}, configs={ConfigManager.ConfigPathSummary}");
    }

    internal static void Tick()
    {
        try
        {
            if (Time.realtimeSinceStartup - _lastTickTime < 0.2f)
            {
                return;
            }
            _lastTickTime = Time.realtimeSinceStartup;

            ConfigManager.ProcessQueuedReload(Log);

            if (!GameStateManager.IsInExpedition)
            {
                DebugMarkerManager.Clear();
                return;
            }

            if (ShouldDumpRuntimeBindings())
            {
                // Index 型配置调试时才输出运行时绑定清单；正常游玩避免反射扫描造成卡顿。
                ScanTriggerManager.DumpScanIndexesIfNeeded();
                InteractTriggerManager.DumpTargetIndexesIfNeeded();
            }
            WarmTerminalSelectorCacheIfNeeded();
            ProcessQueuedConfiguredEvents();
            InteractTriggerManager.ProcessPendingTerminalEvents();
            InteractTriggerManager.ProcessTerminalRepeatEvents();
            InteractTriggerManager.ProcessBigPickupRepeatEvents();
            HudInteractTriggerManager.Update();
            ScanTriggerManager.ProcessScanRepeatEvents();

            List<(ConfigDocument Config, PositionTriggerRule Trigger)> triggers = GetActiveTriggers();
            DebugMarkerManager.UpdateMarkers(triggers);

            if (triggers.Count == 0)
            {
                return;
            }

            foreach ((ConfigDocument config, PositionTriggerRule trigger) in triggers)
            {
                EvaluateTrigger(config, trigger);
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Tick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<(ConfigDocument Config, PositionTriggerRule Trigger)> GetActiveTriggers()
    {
        EnsureActiveTriggerCache();
        return ActivePositionTriggerCache;
    }

    internal static List<(ConfigDocument Config, ScanTriggerRule Trigger)> GetActiveScanTriggers()
    {
        EnsureActiveTriggerCache();
        return ActiveScanTriggerCache;
    }

    internal static List<(ConfigDocument Config, InteractTriggerRule Trigger)> GetActiveInteractTriggers()
    {
        EnsureActiveTriggerCache();
        return ActiveInteractTriggerCache;
    }

    internal static List<(ConfigDocument Config, HudInteractTriggerRule Trigger)> GetActiveHudInteractTriggers()
    {
        EnsureActiveTriggerCache();
        return ActiveHudInteractTriggerCache;
    }

    private static bool ShouldDumpRuntimeBindings()
    {
        // 只有显式启用 Debug.DumpRuntimeIndexes 时才进行运行时索引 dump；
        // 普通 Debug Marker 不再触发任何控制台索引输出。
        return ConfigManager.ShouldDumpRuntimeIndexes();
    }

    private sealed class PendingConfiguredEvent
    {
        public List<JsonElement> EventGroup = new();
        public string OwnerLabel = string.Empty;
        public float DueTime;
        public bool HasSourcePosition;
        public Vector3 SourcePosition;
    }

    private static readonly Queue<PendingConfiguredEvent> PendingConfiguredEvents = new();
    private const int MaxQueuedConfiguredEventsPerTick = 3;

    // 1.3.5: ARA-style local event execution.
    // CTE no longer owns any text/HUD packet sync path and no longer splits display events.
    // When a trigger state reaches this peer, the original event group is executed locally with
    // WardenObjectiveManager.CheckAndExecuteEventsOnTrigger(e, e.Trigger, true), matching ARA/SecuritySensor.

    private static string NormalizeDisplayEventType(string typeText)
    {
        string normalized = (typeText ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty)
            .ToLowerInvariant();

        if (normalized.StartsWith("wee", StringComparison.Ordinal) && normalized.Length > 3)
        {
            normalized = normalized.Substring(3);
        }

        if (normalized.EndsWith("event", StringComparison.Ordinal) && normalized.Length > 5)
        {
            normalized = normalized.Substring(0, normalized.Length - 5);
        }

        return normalized;
    }

    private static bool CanExecuteConfiguredEvents(string ownerLabel)
    {
        // 1.3.5: ARA-style local trigger execution.
        // Every peer that reaches the trigger executes the same original event group locally.
        return true;
    }

    private static bool TryGetIsMaster(string ownerLabel, out bool isMaster)
    {
        try
        {
            isMaster = SNet.IsMaster;
            return true;
        }
        catch (Exception ex)
        {
            isMaster = false;
            LogThrottled($"{ownerLabel} skipped configured Events because SNet.IsMaster could not be read: {DescribeException(ex)}");
            return false;
        }
    }

    private static bool ExecuteConfiguredEventOnThisPeer(JsonElement rawEvent, WardenObjectiveEventData eventData, string ownerLabel, out bool allowClientHudExecution)
    {
        // ARA/SecuritySensor style: local trigger -> local original WardenEvent execution.
        // Do not synthesize, sanitize, split, or packet-sync text events.
        allowClientHudExecution = true;
        return true;
    }

    private static bool RequiresDisplayOnlySanitizationOnClient(JsonElement rawEvent, string normalizedType, int typeValue)
    {
        // AWO MultiProgression updates the sub-objective UI, but it can also touch AWO/AmorLib
        // SyncTrigger/Replicator state. On a non-master CTE replay this is not equivalent to
        // SecuritySensor's original local sensor flow and can produce "Tried to trigger a SyncTrigger
        // that wasn't valid". Detach only the visible fields and never run the original 20015 on clients.
        if (normalizedType == "multiprogression" || typeValue == 20015)
        {
            return true;
        }

        return false;
    }

    private static int QueueConfiguredEventList(IEnumerable<JsonElement> events, string ownerLabel, float delaySeconds = 0.05f, Vector3? sourcePosition = null)
    {
        List<JsonElement> eventGroup = events.Select(e => e.Clone()).ToList();
        if (eventGroup.Count == 0)
        {
            return 0;
        }

        PendingConfiguredEvents.Enqueue(new PendingConfiguredEvent
        {
            EventGroup = eventGroup,
            OwnerLabel = ownerLabel,
            DueTime = Time.realtimeSinceStartup + Math.Max(0.0f, delaySeconds),
            HasSourcePosition = sourcePosition.HasValue,
            SourcePosition = sourcePosition.GetValueOrDefault()
        });

        return eventGroup.Count;
    }

    private static void ProcessQueuedConfiguredEvents()
    {
        if (PendingConfiguredEvents.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        int processed = 0;
        while (PendingConfiguredEvents.Count > 0 && processed < MaxQueuedConfiguredEventsPerTick)
        {
            PendingConfiguredEvent pending = PendingConfiguredEvents.Peek();
            if (pending.DueTime > now) break;
            PendingConfiguredEvents.Dequeue();
            Vector3? sourcePosition = pending.HasSourcePosition ? pending.SourcePosition : null;
            ExecuteEventList(pending.EventGroup, pending.OwnerLabel, sourcePosition);
            processed++;
        }
    }

    internal static void FireInteractTrigger(string sourceKind, string eventName, Component? source, PlayerAgent? player, string sourceName, string stateKeySuffix, int playerSlotOverride = -1)
    {
        int before = InteractTriggerManager.FiredDispatchCount;
        foreach ((ConfigDocument config, InteractTriggerRule trigger) in GetActiveInteractTriggers())
        {
            FireInteractTrigger(config, trigger, sourceKind, eventName, source, player, sourceName, stateKeySuffix, playerSlotOverride);
        }

        if (InteractTriggerManager.FiredDispatchCount == before && (NormalizeTargetType(sourceKind) == "bigpickup" || NormalizeTargetType(sourceKind) == "terminal"))
        {
            LogVerbose($"No interact trigger matched. Event={eventName}, TargetType={sourceKind}, Source={sourceName}. Check Index/SerialNumber/ItemKey/TerminalSelector in config.");
        }
    }

    internal static void FireBigPickupCycleEvents(Component source, PlayerAgent? player, string sourceName, int completedCycles)
    {
        foreach ((ConfigDocument config, InteractTriggerRule trigger) in GetActiveInteractTriggers())
        {
            if (!trigger.UsePickupDropCycleEvents || trigger.PickupDropCycleEvents.Count == 0) continue;
            if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition) continue;
            if (!InteractTriggerMatches(trigger, "BigPickup", "OnBigPickupDrop", source, ignoreTriggerMode: true)) continue;
            int required = Math.Max(1, trigger.PickupDropCycleCount);
            if (completedCycles % required != 0) continue;

            string key = config.FilePath + "::interact-cycle::" + trigger.ID + "::" + InteractTriggerManager.GetRuntimeIndex("BigPickup", source) + "::" + completedCycles;
            if (!InteractTriggerManager.RuleStates.TryGetValue(key, out TriggerState? state))
            {
                state = new TriggerState();
                InteractTriggerManager.RuleStates[key] = state;
            }
            if (trigger.Cooldown > 0f && Time.realtimeSinceStartup - state.LastFireTime < trigger.Cooldown) continue;
            state.LastFireTime = Time.realtimeSinceStartup;
            state.Fired = true;

            Vector3? runtimePosition = GetInteractRuntimePosition(source, player);
            int count = ExecuteEventList(trigger.PickupDropCycleEvents, $"BigPickup pickup/drop cycle trigger '{trigger.ID}' cycles={completedCycles}", runtimePosition);
            LogVerbose($"BigPickup pickup/drop cycle trigger '{trigger.ID}' fired. Cycles={completedCycles}, Required={required}, Source={sourceName}, ExecutedEvents={count}");
        }
    }

    private static Vector3? GetInteractRuntimePosition(Component? source, PlayerAgent? player)
    {
        try
        {
            if (player != null)
            {
                return player.Position;
            }
        }
        catch { }

        try
        {
            if (source != null && source.transform != null)
            {
                return source.transform.position;
            }
        }
        catch { }
        return null;
    }

    private static void FireInteractTrigger(ConfigDocument config, InteractTriggerRule trigger, string sourceKind, string eventName, Component? source, PlayerAgent? player, string sourceName, string stateKeySuffix, int playerSlotOverride)
    {
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition)
        {
            return;
        }

        if (!InteractTriggerMatches(trigger, sourceKind, eventName, source))
        {
            return;
        }

        if (!InteractTriggerAreaMatches(trigger, sourceKind, eventName, source, player))
        {
            return;
        }

        string key = config.FilePath + "::interact::" + trigger.ID + "::" + stateKeySuffix;
        if (!InteractTriggerManager.RuleStates.TryGetValue(key, out TriggerState? state))
        {
            state = new TriggerState();
            InteractTriggerManager.RuleStates[key] = state;
        }

        if (trigger.Cooldown > 0f && Time.realtimeSinceStartup - state.LastFireTime < trigger.Cooldown)
        {
            return;
        }

        state.Fired = true;
        state.LastFireTime = Time.realtimeSinceStartup;
        int count = 0;

        IEnumerable<JsonElement> interactEvents = trigger.Events
            .Concat(trigger.WardenEvents)
            .Concat(GetPlayerTriggerEventsForAgent(trigger, player, playerSlotOverride));
        bool isTerminalEdgeEvent = NormalizeTargetType(sourceKind) == "terminal"
            && (NormalizeInteractionTriggerMode(eventName) == "onterminaluse" || NormalizeInteractionTriggerMode(eventName) == "onterminalunused");

        if (isTerminalEdgeEvent)
        {
            // 1.0.7：触发条件、Cooldown 与事件列表保持不变，只把终端使用/退出后的实际 WardenEvent 拆到后续 Tick 限流执行。
            // 避免 SecDoorTerminal / EOS / AWO / TerminalQueryAPI 同时存在时，在终端 UI 进入/退出帧同步执行整组事件造成卡顿。
            count = QueueConfiguredEventList(interactEvents, $"Interact trigger '{trigger.ID}'", 0.05f, GetInteractRuntimePosition(source, player));
        }
        else
        {
            count = ExecuteEventList(interactEvents, $"Interact trigger '{trigger.ID}'", GetInteractRuntimePosition(source, player));
        }

        InteractTriggerManager.FiredDispatchCount++;
        LogVerbose($"Interact trigger '{trigger.ID}' fired. Event={eventName}, TargetType={sourceKind}, Source={sourceName}, {(isTerminalEdgeEvent ? "QueuedEvents" : "ExecutedEvents")}={count}");
    }

    private static bool InteractTriggerAreaMatches(InteractTriggerRule trigger, string sourceKind, string eventName, Component? source, PlayerAgent? player)
    {
        if (trigger.InteractTriggerArea == null)
        {
            return true;
        }

        if (!TryGetInteractAreaCheckPosition(sourceKind, eventName, source, player, out Vector3 position))
        {
            LogThrottled($"Interact trigger '{trigger.ID}' has TriggerArea configured but no runtime position was available.");
            return false;
        }

        return IsPositionInsideInteractTriggerArea(position, trigger.InteractTriggerArea);
    }

    private static bool TryGetInteractAreaCheckPosition(string sourceKind, string eventName, Component? source, PlayerAgent? player, out Vector3 position)
    {
        string target = NormalizeTargetType(sourceKind);
        string mode = NormalizeInteractionTriggerMode(eventName);

        if (target == "bigpickup" && (mode == "onbigpickuppickup" || mode == "onbigpickupheld") && TryGetPlayerPosition(player, out position))
        {
            return true;
        }

        if (TryGetComponentPosition(source, out position))
        {
            return true;
        }

        if (TryGetPlayerPosition(player, out position))
        {
            return true;
        }

        position = default;
        return false;
    }

    private static bool TryGetPlayerPosition(PlayerAgent? player, out Vector3 position)
    {
        try
        {
            if (player != null)
            {
                position = player.Position;
                return true;
            }
        }
        catch { }

        position = default;
        return false;
    }

    private static bool TryGetComponentPosition(Component? source, out Vector3 position)
    {
        try
        {
            if (source != null && source.transform != null)
            {
                position = source.transform.position;
                return true;
            }
        }
        catch { }

        position = default;
        return false;
    }

    private static bool IsPositionInsideInteractTriggerArea(Vector3 position, PositionTriggerRule trigger)
    {
        string mode = (trigger.TriggerAreaMode ?? "OverrideArea").Trim().ToLowerInvariant();
        if (mode == "overridearea" || mode == "area")
        {
            if (trigger.Count < 0)
            {
                LogThrottled($"Interact trigger '{trigger.ID}' uses TriggerAreaMode=OverrideArea but Count is missing.");
                return false;
            }

            if (!TryGetCachedAreas(trigger, out List<(LG_Zone Zone, object Area, int Index)> areas, out string failure))
            {
                if (!IsTransientLookupFailure(failure))
                {
                    LogThrottled($"Interact trigger '{trigger.ID}' OverrideArea unresolved: {failure}");
                }
                return false;
            }

            foreach ((LG_Zone Zone, object Area, int Index) item in areas)
            {
                if (IsPositionInsideAreaByReflection(item.Area, position)) return true;
            }
            return false;
        }

        if (mode == "overridebigzone" || mode == "bigzone" || mode == "zone")
        {
            if (trigger.LocalIndex < 0)
            {
                LogThrottled($"Interact trigger '{trigger.ID}' uses TriggerAreaMode=OverrideBigZone but LocalIndex is missing.");
                return false;
            }

            return TryGetCachedZones(trigger, out List<LG_Zone> zones, out string failure)
                ? zones.Any(zone => IsPositionInsideZoneByReflection(zone, position))
                : HandleInteractZoneLookupFailure(trigger, failure);
        }

        if (trigger.Position == null)
        {
            LogThrottled($"Interact trigger '{trigger.ID}' uses Radius TriggerArea but Position is missing.");
            return false;
        }

        Vector3 center = trigger.Position.ToVector3();
        float radiusSqr = Math.Max(0.01f, trigger.Radius * trigger.Radius);
        return (position - center).sqrMagnitude <= radiusSqr;
    }

    private static bool HandleInteractZoneLookupFailure(PositionTriggerRule trigger, string failure)
    {
        if (!IsTransientLookupFailure(failure))
        {
            LogThrottled($"Interact trigger '{trigger.ID}' OverrideBigZone unresolved: {failure}");
        }
        return false;
    }

    private static IEnumerable<JsonElement> GetPlayerTriggerEventsForAgent(InteractTriggerRule trigger, PlayerAgent? player, int playerSlotOverride = -1)
    {
        int slot = playerSlotOverride >= 0 && playerSlotOverride <= 3
            ? playerSlotOverride
            : GetPlayerSlotIndex(player);
        return slot switch
        {
            0 => trigger.OnePlayerTriggerEvents,
            1 => trigger.TwoPlayerTriggerEvents,
            2 => trigger.ThreePlayerTriggerEvents,
            3 => trigger.FourPlayerTriggerEvents,
            _ => Enumerable.Empty<JsonElement>()
        };
    }

    internal static int GetPlayerSlotIndex(PlayerAgent? player)
    {
        if (player == null) return -1;

        try
        {
            var list = PlayerManager.PlayerAgentsInLevel;
            if (list != null)
            {
                for (int i = 0; i < list.Count && i < 4; i++)
                {
                    PlayerAgent? candidate = list[i];
                    if (candidate == null) continue;
                    if (ReferenceEquals(candidate, player)) return i;
                    if (candidate.GetInstanceID() == player.GetInstanceID()) return i;
                }
            }
        }
        catch { }

        return -1;
    }

    private static bool InteractTriggerMatches(InteractTriggerRule trigger, string sourceKind, string eventName, Component? source, bool ignoreTriggerMode = false)
    {
        string target = NormalizeTargetType(trigger.TargetType);
        string kind = NormalizeTargetType(sourceKind);
        if (!string.IsNullOrWhiteSpace(target) && target != "any" && target != kind)
        {
            return false;
        }

        if (!ignoreTriggerMode)
        {
            string requested = NormalizeInteractionTriggerMode(trigger.TriggerMode);
            string actual = NormalizeInteractionTriggerMode(eventName);
            if (!string.IsNullOrWhiteSpace(requested) && requested != actual)
            {
                return false;
            }
        }

        if (source == null)
        {
            return true;
        }

        bool hasSelector = trigger.Index >= 0 || trigger.InstanceID >= 0 || trigger.SyncID >= 0 || trigger.SerialNumber >= 0
            || trigger.DataBlockID != 0u || trigger.ItemID != 0u
            || !string.IsNullOrWhiteSpace(trigger.ItemKey) || !string.IsNullOrWhiteSpace(trigger.PublicName)
            || !string.IsNullOrWhiteSpace(trigger.TerminalSerial) || !string.IsNullOrWhiteSpace(trigger.WorldEventObjectFilter)
            || !string.IsNullOrWhiteSpace(trigger.InternalName) || trigger.Position != null;

        if (!hasSelector)
        {
            return true;
        }

        if (trigger.Index >= 0)
        {
            int idx;
            if (kind == "bigpickup")
            {
                if (trigger.Index <= 0)
                {
                    LogThrottled($"BigPickup trigger '{trigger.ID}' uses invalid Index={trigger.Index}. ScanPosOverride/ECC BigPickup item indices start at 1.");
                    return false;
                }
                if (!SpoIndexResolver.TryGetBigPickupSpoIndex(source, out idx, out string spoSource))
                {
                    LogThrottled($"BigPickup trigger '{trigger.ID}' cannot match: SPO/ECC BigPickup Item Index is unavailable. ConfigIndex={trigger.Index}, Source={source?.name ?? "<null>"}");
                    return false;
                }
                if (idx != trigger.Index) return false;
            }
            else
            {
                idx = InteractTriggerManager.GetRuntimeIndex(kind, source);
                if (idx < 0)
                {
                    idx = TryGetIntMember(source, "Index", "m_index", "m_terminalIndex", "m_terminalSerialIndex", "PuzzleOverrideIndex");
                }
                if (idx != trigger.Index) return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(trigger.WorldEventObjectFilter))
        {
            if (!ObjectMatchesFilter(source, trigger.WorldEventObjectFilter)) return false;
        }

        if (!string.IsNullOrWhiteSpace(trigger.InternalName))
        {
            string objName = TryGetPublicObjectName(source);
            if (objName.IndexOf(trigger.InternalName, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        if (trigger.DataBlockID != 0u)
        {
            uint dataId = TryGetUIntMember(source, "DataBlockID", "ItemDataBlockID", "m_dataBlockID", "m_itemDataBlockID", "ItemID", "m_itemID");
            if (dataId != trigger.DataBlockID) return false;
        }

        if (trigger.InstanceID >= 0)
        {
            int instanceId = -1;
            try { instanceId = source.GetInstanceID(); } catch { }
            if (instanceId != trigger.InstanceID) return false;
        }

        if (trigger.SyncID >= 0)
        {
            int syncId = TryGetIntMember(source, "SyncID", "m_syncID");
            if (syncId != trigger.SyncID) return false;
        }

        if (trigger.SerialNumber >= 0)
        {
            int serial = TryGetIntMember(source, "m_serialNumber", "SerialNumber", "m_serial");
            if (serial != trigger.SerialNumber) return false;
        }

        if (!string.IsNullOrWhiteSpace(trigger.ItemKey))
        {
            string key = TryGetStringMember(source, "m_itemKey", "ItemKey", "Key");
            if (!string.Equals(key, trigger.ItemKey, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (!string.IsNullOrWhiteSpace(trigger.TerminalSerial))
        {
            if (kind != "terminal") return false;
            if (!TerminalMatchesSelector(source, trigger.TerminalSerial)) return false;
        }

        if (!string.IsNullOrWhiteSpace(trigger.PublicName))
        {
            string publicName = TryGetPublicObjectName(source);
            if (publicName.IndexOf(trigger.PublicName, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        if (trigger.Position != null)
        {
            Vector3 sourcePos;
            try { sourcePos = source.transform.position; } catch { return false; }
            Vector3 targetPos = trigger.Position.ToVector3();
            float radius = Math.Max(0.01f, trigger.Radius);
            if ((sourcePos - targetPos).sqrMagnitude > radius * radius) return false;
        }

        return true;
    }

    internal static string NormalizeTargetType(string text)
    {
        return GetCachedNormalization(TargetTypeNormalizationCache, text, static value =>
        {
                if (string.IsNullOrWhiteSpace(value)) return "any";
                return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
                {
                    "terminal" => "terminal",
                    "computerterminal" => "terminal",
                    "interact" => "interact",
                    "interaction" => "interact",
                    "position" => "position",
                    "coordinate" => "coordinate",
                    "scan" => "scan",
                    "bioscan" => "bioscan",
                    "bigpickup" => "bigpickup",
                    "largepickup" => "bigpickup",
                    "carryitem" => "bigpickup",
                    "carryitempickup" => "bigpickup",
                    "any" => "any",
                    "all" => "any",
                    _ => value.Trim().ToLowerInvariant()
                };
    
        });
    }

    internal static string NormalizeInteractionTriggerMode(string text)
    {
        return GetCachedNormalization(InteractModeNormalizationCache, text, static value =>
        {
                if (string.IsNullOrWhiteSpace(value)) return string.Empty;
                return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
                {
                    "pickup" => "onbigpickuppickup",
                    "pickedup" => "onbigpickuppickup",
                    "onpickup" => "onbigpickuppickup",
                    "onpickedup" => "onbigpickuppickup",
                    "bigpickuppickup" => "onbigpickuppickup",
                    "onbigpickuppickup" => "onbigpickuppickup",
                    "bigpickuppickedup" => "onbigpickuppickup",
                    "onbigpickup" => "onbigpickuppickup",
                    "held" => "onbigpickupheld",
                    "holding" => "onbigpickupheld",
                    "bigpickupheld" => "onbigpickupheld",
                    "bigpickupholding" => "onbigpickupheld",
                    "onbigpickupheld" => "onbigpickupheld",
                    "onbigpickupholding" => "onbigpickupheld",
                    "onbigpickuppickuprepeat" => "onbigpickupheld",
                    "onbigpickuppickupinside" => "onbigpickupheld",
                    "drop" => "onbigpickupdrop",
                    "dropped" => "onbigpickupdrop",
                    "ondrop" => "onbigpickupdrop",
                    "onbigpickupdrop" => "onbigpickupdrop",
                    "onbigpickupdropped" => "onbigpickupdrop",
                    "placed" => "onbigpickupplaced",
                    "inlevel" => "onbigpickupplaced",
                    "bigpickupplaced" => "onbigpickupplaced",
                    "bigpickupinlevel" => "onbigpickupplaced",
                    "onbigpickupplaced" => "onbigpickupplaced",
                    "onbigpickupinlevel" => "onbigpickupplaced",
                    "onbigpickupdroprepeat" => "onbigpickupplaced",
                    "onbigpickupdropinside" => "onbigpickupplaced",
                    "terminaluse" => "onterminaluse",
                    "useterminal" => "onterminaluse",
                    "use" => "onterminaluse",
                    "onuse" => "onterminaluse",
                    "onterminaluse" => "onterminaluse",
                    "terminalusing" => "onterminalusing",
                    "usingterminal" => "onterminalusing",
                    "using" => "onterminalusing",
                    "onterminalusing" => "onterminalusing",
                    "onterminaluserepeat" => "onterminalusing",
                    "onterminaluseinside" => "onterminalusing",
                    "terminalheld" => "onterminalusing",
                    "terminalunused" => "onterminalunused",
                    "terminalunuse" => "onterminalunused",
                    "terminalexit" => "onterminalunused",
                    "unuse" => "onterminalunused",
                    "exit" => "onterminalunused",
                    "onterminalunused" => "onterminalunused",
                    "onterminalunuse" => "onterminalunused",
                    "onterminalexit" => "onterminalunused",
                    "terminalexited" => "onterminalexited",
                    "afterterminalexit" => "onterminalexited",
                    "afterterminalunused" => "onterminalexited",
                    "onterminalexited" => "onterminalexited",
                    "onterminalunusedrepeat" => "onterminalexited",
                    "onterminalexitrepeat" => "onterminalexited",
                    "onterminalexitinside" => "onterminalexited",
                    _ => value.Trim().ToLowerInvariant()
                };
    
        });
    }

    private static int TryGetIntMember(object obj, params string[] names)
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

    private static uint TryGetUIntMember(object obj, params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? v = p?.GetValue(obj);
                if (v != null && uint.TryParse(v.ToString(), out uint i)) return i;
                FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                v = f?.GetValue(obj);
                if (v != null && uint.TryParse(v.ToString(), out i)) return i;
            }
            catch { }
        }
        return 0u;
    }

    private static bool ObjectMatchesFilter(Component source, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        foreach (string candidate in EnumerateObjectSelectorStrings(source))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (string.Equals(candidate, f, StringComparison.OrdinalIgnoreCase)) return true;
            if (candidate.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateObjectSelectorStrings(Component source)
    {
        yield return TryGetPublicObjectName(source);
        yield return TryGetStringMember(source, "WorldEventObjectFilter", "m_worldEventObjectFilter", "Filter", "m_filter", "m_publicName", "PublicName", "ItemKey", "m_itemKey", "Key", "m_serial", "m_serialNumber", "SerialNumber", "TerminalSerial", "m_terminalSerial");

        string value = string.Empty;
        try { if (source.gameObject != null) value = source.gameObject.name; } catch { value = string.Empty; }
        if (!string.IsNullOrWhiteSpace(value)) yield return value;

        value = string.Empty;
        try { if (source.transform != null && source.transform.parent != null) value = source.transform.parent.name; } catch { value = string.Empty; }
        if (!string.IsNullOrWhiteSpace(value)) yield return value;

        value = string.Empty;
        try { value = source.ToString() ?? string.Empty; } catch { value = string.Empty; }
        if (!string.IsNullOrWhiteSpace(value)) yield return value;
    }

    private static string TryGetStringMember(object obj, params string[] names)
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

    private static string TryGetPublicObjectName(Component source)
    {
        try
        {
            PropertyInfo? p = source.GetType().GetProperty("PublicName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? v = p?.GetValue(source);
            if (v != null) return v.ToString() ?? string.Empty;
        }
        catch { }
        try { if (source.gameObject != null) return source.gameObject.name ?? string.Empty; } catch { }
        return source.ToString() ?? string.Empty;
    }

    internal static void ClearTerminalSelectorCache()
    {
        TerminalSelectorCache.Clear();
        TerminalTslAddressCache.Clear();
        TerminalSelectorMatchCache.Clear();
        TerminalSelectorCacheMisses.Clear();
        TerminalSelectorCacheWarmupComplete = false;
        LastTerminalSelectorCacheWarmupAttempt = -999999f;
    }

    internal static void CacheTerminalSelectorsForTerminal(LG_ComputerTerminal terminal, int runtimeIndex)
    {
        CacheTerminalSelectorsForTerminalCore(terminal, runtimeIndex, false, default);
    }

    private static void CacheTerminalSelectorsForTerminalCore(LG_ComputerTerminal terminal, int runtimeIndex, bool hasKnownAddress, TerminalTslAddress knownAddress)
    {
        if (terminal == null) return;
        try
        {
            int id = terminal.GetInstanceID();
            HashSet<string> selectors = new(StringComparer.OrdinalIgnoreCase);
            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) selectors.Add(NormalizeSelector(value));
            }

            string publicName = TryGetPublicObjectName(terminal);
            string itemKey = TryGetStringMember(terminal, "ItemKey", "_ItemKey_k__BackingField", "m_itemKey", "Key");
            int serialNumber = TryGetIntMember(terminal, "m_serialNumber", "SerialNumber", "Serial", "TerminalSerialNumber");
            string serialText = TryGetNestedStringMember(terminal, "m_serial", "Serial", "m_serialText", "m_terminalSerial", "TerminalSerial", "TerminalSerialText", "SerialText", "SerialLookup", "TerminalSerialLookup");

            Add(publicName);
            Add(itemKey);
            Add(serialText);
            Add(TryGetStringMember(terminal, "TerminalSerial", "TerminalSerialText", "TerminalSelector", "TerminalTSL", "TSL", "SerialText", "SerialLookup", "TerminalSerialLookup"));
            if (serialNumber >= 0)
            {
                Add(serialNumber.ToString());
                Add($"TERMINAL_{serialNumber}");
                Add($"[TERMINAL_{serialNumber}]");
            }
            if (runtimeIndex >= 0)
            {
                Add(runtimeIndex.ToString());
                Add($"TERMINAL_{runtimeIndex}");
                Add($"[TERMINAL_{runtimeIndex}]");
            }

            TerminalTslAddress address;
            if (hasKnownAddress)
            {
                address = knownAddress;
                TerminalTslAddressCache[id] = address;
                Add(address.ToToken());
                Add(address.ToBracketedToken());
            }
            else if (TryGetTerminalTslAddress(terminal, out address))
            {
                TerminalTslAddressCache[id] = address;
                Add(address.ToToken());
                Add(address.ToBracketedToken());
            }

            TerminalSelectorCache[id] = selectors;
            TerminalSelectorCacheMisses.Remove(id);
            Runtime.LogVerbose($"Cached terminal selectors for {publicName}: {string.Join(", ", selectors.Take(12))}");
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Terminal selector cache build failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void WarmTerminalSelectorCacheIfNeeded(bool force = false)
    {
        if (!GameStateManager.IsInExpedition) return;
        if (!force && TerminalSelectorCacheWarmupComplete) return;
        if (!force && !HasActiveTerminalSelectorTriggers()) return;
        float now = Time.realtimeSinceStartup;
        if (!force && now - LastTerminalSelectorCacheWarmupAttempt < 1.0f) return;
        LastTerminalSelectorCacheWarmupAttempt = now;

        try
        {
            int cached = 0;
            HashSet<int> seen = new();

            foreach (LG_Zone zone in UnityEngine.Object.FindObjectsOfType<LG_Zone>())
            {
                if (zone == null || zone.TerminalsSpawnedInZone == null) continue;
                int dimensionIndex = (int)zone.DimensionIndex;
                int layerIndex = TryGetLayerIndex(zone);
                int zoneLocalIndex = (int)zone.LocalIndex;
                int terminalCount = zone.TerminalsSpawnedInZone.Count;

                for (int i = 0; i < terminalCount; i++)
                {
                    LG_ComputerTerminal terminal = zone.TerminalsSpawnedInZone[i];
                    if (terminal == null) continue;
                    int id = terminal.GetInstanceID();
                    if (!seen.Add(id)) continue;

                    int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("Terminal", terminal);
                    TerminalTslAddress address = new(dimensionIndex, layerIndex, zoneLocalIndex, i);
                    CacheTerminalSelectorsForTerminalCore(terminal, runtimeIndex, true, address);
                    cached++;
                }
            }

            // EOS / SecDoorTerminal 等扩展可能生成未挂到 TerminalsSpawnedInZone 的终端。
            // 对这些终端只缓存 SerialText / PublicName / 运行时 Index，不在交互路径里反复扫描 LG_Zone。
            foreach (LG_ComputerTerminal terminal in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>())
            {
                if (terminal == null) continue;
                int id = terminal.GetInstanceID();
                if (seen.Contains(id)) continue;
                int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("Terminal", terminal);
                CacheTerminalSelectorsForTerminalCore(terminal, runtimeIndex, false, default);
                cached++;
            }

            if (cached > 0)
            {
                TerminalSelectorCacheWarmupComplete = true;
                Runtime.LogVerbose($"Terminal selector cache warmup complete. CachedTerminals={cached}");
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Terminal selector cache warmup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool HasActiveTerminalSelectorTriggers()
    {
        try
        {
            foreach ((ConfigDocument _config, InteractTriggerRule trigger) in GetActiveInteractTriggers())
            {
                if (NormalizeTargetType(trigger.TargetType) != "terminal") continue;
                if (!string.IsNullOrWhiteSpace(trigger.TerminalSerial)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TerminalMatchesSelector(Component source, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return true;
        string requested = NormalizeSelector(selector);
        string stripped = requested.Trim('[', ']');

        LG_ComputerTerminal? cachedTerminal = TryGetLGTerminal(source);
        if (cachedTerminal != null)
        {
            try
            {
                int id = cachedTerminal.GetInstanceID();
                string matchKey = id + "::" + requested;
                if (TerminalSelectorMatchCache.TryGetValue(matchKey, out bool cachedMatch)) return cachedMatch;

                if (!TerminalSelectorCache.ContainsKey(id) && !TerminalSelectorCacheMisses.Contains(id))
                {
                    WarmTerminalSelectorCacheIfNeeded(force: true);
                    if (!TerminalSelectorCache.ContainsKey(id))
                    {
                        int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("Terminal", cachedTerminal);
                        CacheTerminalSelectorsForTerminal(cachedTerminal, runtimeIndex);
                        if (!TerminalSelectorCache.ContainsKey(id)) TerminalSelectorCacheMisses.Add(id);
                    }
                }

                if (TerminalSelectorCache.TryGetValue(id, out HashSet<string>? cached))
                {
                    bool result = cached.Contains(requested) || cached.Contains(stripped);
                    if (!result && TryParseAwoTerminalSelector(selector, out TerminalTslAddress requestedAddress)
                        && TryGetTerminalTslAddress(cachedTerminal, out TerminalTslAddress actualAddress))
                    {
                        result = actualAddress.Matches(requestedAddress);
                    }
                    if (!result && TryParseSingleTerminalIndex(selector, out int globalIndex))
                    {
                        result = InteractTriggerManager.GetRuntimeIndex("terminal", cachedTerminal) == globalIndex;
                    }
                    TerminalSelectorMatchCache[matchKey] = result;
                    return result;
                }
            }
            catch { }
        }

        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in EnumerateTerminalSelectorStrings(source))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            candidates.Add(NormalizeSelector(candidate));
        }

        bool matched = candidates.Contains(requested) || candidates.Contains(stripped);
        if (!matched && TryParseAwoTerminalSelector(selector, out TerminalTslAddress requestedAddressFallback))
        {
            LG_ComputerTerminal? terminal = TryGetLGTerminal(source);
            if (terminal != null && TryGetTerminalTslAddress(terminal, out TerminalTslAddress actualAddress))
            {
                candidates.Add(NormalizeSelector(actualAddress.ToToken()));
                candidates.Add(NormalizeSelector(actualAddress.ToBracketedToken()));
                matched = actualAddress.Matches(requestedAddressFallback);
            }
        }

        if (!matched && TryParseSingleTerminalIndex(selector, out int globalIndexFallback))
        {
            int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("terminal", source);
            matched = runtimeIndex == globalIndexFallback;
        }

        if (!matched)
        {
            Runtime.LogThrottled($"Terminal selector '{selector}' did not match source '{TryGetPublicObjectName(source)}'. Candidates={string.Join(", ", candidates.Take(18))}");
        }
        return matched;
    }

    private static IEnumerable<string> EnumerateTerminalSelectorStrings(Component source)
    {
        yield return TryGetPublicObjectName(source);
        yield return TryGetStringMember(source, "m_serialNumber", "SerialNumber", "TerminalSerial", "m_terminalSerial", "ItemKey");
        yield return TryGetNestedStringMember(source, "m_serial", "Serial", "m_serialText");

        LG_ComputerTerminal? terminal = TryGetLGTerminal(source);
        if (terminal != null)
        {
            int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("terminal", terminal);
            int serialNumber = TryGetIntMember(terminal, "m_serialNumber", "SerialNumber");
            string serialText = TryGetNestedStringMember(terminal, "m_serial", "Serial", "m_serialText");
            string itemKey = TryGetStringMember(terminal, "ItemKey", "_ItemKey_k__BackingField");
            string publicName = TryGetPublicObjectName(terminal);

            yield return publicName;
            yield return itemKey;
            yield return serialNumber >= 0 ? serialNumber.ToString() : string.Empty;
            yield return serialText;
            yield return runtimeIndex >= 0 ? $"TERMINAL_{runtimeIndex}" : string.Empty;
            yield return runtimeIndex >= 0 ? $"[TERMINAL_{runtimeIndex}]" : string.Empty;
            yield return serialNumber >= 0 ? $"TERMINAL_{serialNumber}" : string.Empty;
            yield return serialNumber >= 0 ? $"[TERMINAL_{serialNumber}]" : string.Empty;

            // 输出 AWO Terminal Serial Lookup 格式，便于 TerminalSelector 直接使用。
            if (TryGetTerminalTslAddress(terminal, out TerminalTslAddress address))
            {
                yield return address.ToToken();
                yield return address.ToBracketedToken();
            }
        }
    }

    private static LG_ComputerTerminal? TryGetLGTerminal(Component source)
    {
        try
        {
            if (source is LG_ComputerTerminal terminal) return terminal;
        }
        catch { }

        try
        {
            if (source is Interact_ComputerTerminal interact && interact.m_terminal != null) return interact.m_terminal;
        }
        catch { }

        try
        {
            object? member = TryGetObjectMember(source, "m_terminal", "Terminal", "terminal");
            if (member is LG_ComputerTerminal terminal) return terminal;
        }
        catch { }

        try
        {
            LG_ComputerTerminal terminal = source.GetComponentInParent<LG_ComputerTerminal>();
            if (terminal != null) return terminal;
        }
        catch { }

        try
        {
            LG_ComputerTerminal terminal = source.GetComponentInChildren<LG_ComputerTerminal>();
            if (terminal != null) return terminal;
        }
        catch { }

        return null;
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

    private static int TryGetCollectionCount(object? collection)
    {
        if (collection == null) return 0;

        try
        {
            if (collection is System.Collections.ICollection c) return c.Count;
        }
        catch { }

        Type type = collection.GetType();
        foreach (string name in new[] { "Count", "Length", "count", "length", "m_count", "_size" })
        {
            try
            {
                PropertyInfo? p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? value = p?.GetValue(collection);
                if (TryConvertToInt(value, out int count)) return count;
            }
            catch { }

            try
            {
                FieldInfo? f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? value = f?.GetValue(collection);
                if (TryConvertToInt(value, out int count)) return count;
            }
            catch { }
        }

        try
        {
            MethodInfo? method = type.GetMethod("get_Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? value = method?.Invoke(collection, Array.Empty<object>());
            if (TryConvertToInt(value, out int count)) return count;
        }
        catch { }

        return 0;
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        result = 0;
        if (value == null) return false;

        try
        {
            switch (value)
            {
                case int i:
                    result = i;
                    return true;
                case uint ui when ui <= int.MaxValue:
                    result = (int)ui;
                    return true;
                case long l when l >= int.MinValue && l <= int.MaxValue:
                    result = (int)l;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                default:
                    return int.TryParse(value.ToString(), out result);
            }
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    private static List<object?> CollectionToObjectList(object? collection, int maxCount = 512)
    {
        List<object?> result = new();
        if (collection == null) return result;

        try
        {
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    result.Add(item);
                    if (result.Count >= maxCount) return result;
                }

                return result;
            }
        }
        catch { }

        int count = Math.Min(TryGetCollectionCount(collection), maxCount);
        if (count <= 0) return result;

        Type type = collection.GetType();
        MethodInfo? getItem = null;
        try
        {
            getItem = type.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
        }
        catch { }

        PropertyInfo? indexer = null;
        try
        {
            indexer = type.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, null, new[] { typeof(int) }, null);
        }
        catch { }

        for (int i = 0; i < count; i++)
        {
            try
            {
                object? item = getItem != null
                    ? getItem.Invoke(collection, new object[] { i })
                    : indexer?.GetValue(collection, new object[] { i });
                result.Add(item);
            }
            catch { }
        }

        return result;
    }

    private static string TryGetNestedStringMember(object obj, params string[] names)
    {
        object? member = TryGetObjectMember(obj, names);
        if (member == null) return string.Empty;

        try
        {
            PropertyInfo? textProp = member.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? member.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? text = textProp?.GetValue(member);
            if (text != null) return text.ToString() ?? string.Empty;
        }
        catch { }

        return member.ToString() ?? string.Empty;
    }

    private static string NormalizeSelector(string text)
    {
        return (text ?? string.Empty).Trim().Trim('"').Trim().ToUpperInvariant();
    }

    private readonly struct TerminalTslAddress
    {
        public readonly int DimensionIndex;
        public readonly int LayerIndex;
        public readonly int ZoneLocalIndex;
        public readonly int TerminalIndexInZone;

        public TerminalTslAddress(int dimensionIndex, int layerIndex, int zoneLocalIndex, int terminalIndexInZone)
        {
            DimensionIndex = dimensionIndex;
            LayerIndex = layerIndex;
            ZoneLocalIndex = zoneLocalIndex;
            TerminalIndexInZone = terminalIndexInZone;
        }

        public string ToToken() => $"TERMINAL_{DimensionIndex}_{LayerIndex}_{ZoneLocalIndex}_{TerminalIndexInZone}";
        public string ToBracketedToken() => "[" + ToToken() + "]";

        public bool Matches(TerminalTslAddress other)
        {
            return DimensionIndex == other.DimensionIndex
                && LayerIndex == other.LayerIndex
                && ZoneLocalIndex == other.ZoneLocalIndex
                && TerminalIndexInZone == other.TerminalIndexInZone;
        }
    }

    private static bool TryParseAwoTerminalSelector(string selector, out TerminalTslAddress address)
    {
        address = default;
        string value = selector.Trim().Trim('[', ']').ToUpperInvariant();
        if (!value.StartsWith("TERMINAL_", StringComparison.OrdinalIgnoreCase)) return false;

        // AWO TSL 终端格式固定为 4 个数字：
        // TERMINAL_<DimensionIndex>_<LayerIndex>_<ZoneLocalIndex>_<TerminalIndexInZone>
        string[] parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        if (!int.TryParse(parts[1], out int dimensionIndex)) return false;
        if (!int.TryParse(parts[2], out int layerIndex)) return false;
        if (!int.TryParse(parts[3], out int zoneLocalIndex)) return false;
        if (!int.TryParse(parts[4], out int terminalIndexInZone)) return false;

        address = new TerminalTslAddress(dimensionIndex, layerIndex, zoneLocalIndex, terminalIndexInZone);
        return true;
    }

    private static bool TryParseSingleTerminalIndex(string selector, out int index)
    {
        index = -1;
        string value = selector.Trim().Trim('[', ']').ToUpperInvariant();
        if (!value.StartsWith("TERMINAL_", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        return int.TryParse(parts[1], out index);
    }

    private static bool TryGetTerminalTslAddress(LG_ComputerTerminal terminal, out TerminalTslAddress address)
    {
        address = default;
        if (terminal == null) return false;
        try
        {
            int id = terminal.GetInstanceID();
            if (TerminalTslAddressCache.TryGetValue(id, out address)) return true;
            if (!TerminalSelectorCacheWarmupComplete)
            {
                WarmTerminalSelectorCacheIfNeeded(force: true);
                if (TerminalTslAddressCache.TryGetValue(id, out address)) return true;
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Could not resolve terminal AWO TSL address from cache: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    internal static string GetTerminalTslSelectorText(LG_ComputerTerminal terminal)
    {
        if (terminal != null && TryGetTerminalTslAddress(terminal, out TerminalTslAddress address))
        {
            return address.ToBracketedToken();
        }

        return "<unresolved>";
    }

    internal static void BroadcastTerminalTrigger(Component terminal, bool entering, int playerSlot)
    {
        if (!TerminalTriggerSyncRegistered || IsReceivingTerminalTriggerSync || terminal == null) return;
        try
        {
            int runtimeIndex = InteractTriggerManager.GetRuntimeIndex("terminal", terminal);
            string selector = string.Empty;
            LG_ComputerTerminal? lgTerminal = TryGetLGTerminal(terminal);
            if (lgTerminal != null)
            {
                selector = GetTerminalTslSelectorText(lgTerminal);
                if (string.Equals(selector, "<unresolved>", StringComparison.OrdinalIgnoreCase))
                {
                    selector = string.Empty;
                }
            }
            if (string.IsNullOrWhiteSpace(selector) && runtimeIndex >= 0)
            {
                selector = $"[TERMINAL_{runtimeIndex}]";
            }

            byte[] selectorBytes = string.IsNullOrWhiteSpace(selector) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(selector);
            int byteCount = Math.Min(selectorBytes.Length, MaxTerminalSyncSelectorBytes);
            byte[] payload = new byte[MaxTerminalSyncSelectorBytes];
            if (byteCount > 0)
            {
                Array.Copy(selectorBytes, payload, byteCount);
            }

            int messageId = NextTerminalSyncMessageId++;
            if (NextTerminalSyncMessageId == int.MaxValue) NextTerminalSyncMessageId = 1;

            CteTerminalTriggerPacket packet = new()
            {
                MessageId = messageId,
                RuntimeIndex = runtimeIndex,
                PlayerSlot = playerSlot,
                EventKind = entering ? (byte)1 : (byte)2,
                SelectorByteCount = (ushort)byteCount,
                SelectorPayload = payload
            };

            NetworkAPI.InvokeEvent<CteTerminalTriggerPacket>(TerminalTriggerSyncEventName, packet, SNet_ChannelType.GameOrderCritical);
            LogVerbose($"Broadcast terminal trigger sync. Event={(entering ? "OnTerminalUse" : "OnTerminalUnused")}, Selector='{selector}', RuntimeIndex={runtimeIndex}, PlayerSlot={playerSlot}, MessageId={messageId}.");
        }
        catch (Exception ex)
        {
            LogThrottled($"Failed to broadcast CTE terminal trigger sync: {DescribeException(ex)}");
        }
    }

    private static void OnReceiveTerminalTriggerSync(ulong sender, CteTerminalTriggerPacket packet)
    {
        try
        {
            CleanupExpiredTerminalSyncMessages();
            string messageKey = sender + ":" + packet.MessageId;
            if (packet.MessageId == 0 || ReceivedTerminalSyncMessages.ContainsKey(messageKey)) return;
            ReceivedTerminalSyncMessages[messageKey] = Time.realtimeSinceStartup;

            if (packet.EventKind != 1 && packet.EventKind != 2) return;
            string selector = DecodeTerminalSyncSelector(packet);
            if (!TryFindTerminalForSync(selector, packet.RuntimeIndex, out LG_ComputerTerminal? terminal))
            {
                LogThrottled($"CTE terminal trigger sync could not resolve terminal. Selector='{selector}', RuntimeIndex={packet.RuntimeIndex}, Sender={sender}.");
                return;
            }

            IsReceivingTerminalTriggerSync = true;
            try
            {
                InteractTriggerManager.OnSyncedTerminalTrigger(terminal, packet.EventKind == 1, packet.PlayerSlot, $"CTE terminal sync sender={sender}");
            }
            finally
            {
                IsReceivingTerminalTriggerSync = false;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"CTE terminal trigger sync receive failed: {DescribeException(ex)}");
        }
    }

    private static string DecodeTerminalSyncSelector(CteTerminalTriggerPacket packet)
    {
        try
        {
            if (packet.SelectorByteCount == 0 || packet.SelectorPayload == null || packet.SelectorPayload.Length < packet.SelectorByteCount)
            {
                return string.Empty;
            }
            return Encoding.UTF8.GetString(packet.SelectorPayload, 0, packet.SelectorByteCount);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CleanupExpiredTerminalSyncMessages()
    {
        float now = Time.realtimeSinceStartup;
        foreach (string key in ReceivedTerminalSyncMessages.Where(pair => now - pair.Value > TerminalSyncMessageLifetimeSeconds).Select(pair => pair.Key).ToArray())
        {
            ReceivedTerminalSyncMessages.Remove(key);
        }
    }

    private static bool TryFindTerminalForSync(string selector, int runtimeIndex, out LG_ComputerTerminal? terminal)
    {
        terminal = null;
        try
        {
            WarmTerminalSelectorCacheIfNeeded(force: true);
            foreach (LG_ComputerTerminal candidate in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>())
            {
                if (candidate == null) continue;
                if (!string.IsNullOrWhiteSpace(selector) && TerminalMatchesSelector(candidate, selector))
                {
                    terminal = candidate;
                    return true;
                }
                if (runtimeIndex >= 0 && InteractTriggerManager.GetRuntimeIndex("terminal", candidate) == runtimeIndex)
                {
                    terminal = candidate;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"CTE terminal trigger sync terminal lookup failed: {DescribeException(ex)}");
        }
        return false;
    }

    private static int TryGetLayerIndex(LG_Zone zone)
    {
        try
        {
            if (zone.Layer != null) return (int)zone.Layer.m_type;
        }
        catch { }

        try
        {
            LG_Layer? layer = TryGetObjectMember(zone, "m_layer", "Layer") as LG_Layer;
            if (layer != null) return (int)layer.m_type;
        }
        catch { }

        return 0;
    }

    private static bool IsSameUnityObject(UnityEngine.Object? a, UnityEngine.Object? b)
    {
        if (a == null || b == null) return false;
        try { return a.GetInstanceID() == b.GetInstanceID(); } catch { return false; }
    }

    private static bool TryResolveTerminalSelectorViaAwo(string selector, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); } catch { continue; }
                foreach (Type type in types)
                {
                    string typeName = type.FullName ?? type.Name;
                    if (typeName.IndexOf("TerminalSerialLookup", StringComparison.OrdinalIgnoreCase) < 0
                        && typeName.IndexOf("TerminalSerial", StringComparison.OrdinalIgnoreCase) < 0
                        && typeName.IndexOf("TSL", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (method.ReturnType != typeof(string)) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;

                        string name = method.Name;
                        if (name.IndexOf("Resolve", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Lookup", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Format", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Parse", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        object? result = method.Invoke(null, new object[] { selector });
                        if (result is string s && !string.IsNullOrWhiteSpace(s) && !string.Equals(s, selector, StringComparison.OrdinalIgnoreCase))
                        {
                            resolved = s;
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"AWO/TSL reflection lookup failed for '{selector}': {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    internal static void FireScanTrigger(ConfigDocument config, ScanTriggerRule trigger, string eventName, string sourceName, int puzzleIndex, int playersInScan, string stateKeySuffix, Vector3? sourcePosition = null)
    {
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition)
        {
            return;
        }

        if (trigger.RequireAlivePlayers && !HasAnyAlivePlayer())
        {
            LogThrottled($"Scan trigger '{trigger.ID}' skipped because RequireAlivePlayers=true and no alive players are currently available.");
            return;
        }

        if (trigger.PuzzleOverrideIndex >= 0 && trigger.PuzzleOverrideIndex != puzzleIndex)
        {
            return;
        }

        string mode = NormalizeTriggerMode(trigger.TriggerMode);
        string requested = NormalizeTriggerMode(eventName);
        if (!string.Equals(mode, requested, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (trigger.UsePlayerCountEvents && (playersInScan < 1 || playersInScan > 4))
        {
            LogVerbose($"Scan trigger '{trigger.ID}' matched Event={eventName}, Index={puzzleIndex}, but UsePlayerCountEvents=true and PlayersInScan={playersInScan}; waiting for a 1-4 player count event group and not executing Events/WardenEvents.");
            return;
        }

        string key = config.FilePath + "::scan::" + trigger.ID + "::" + stateKeySuffix;
        if (!ScanTriggerManager.RuleStates.TryGetValue(key, out TriggerState? state))
        {
            state = new TriggerState();
            ScanTriggerManager.RuleStates[key] = state;
        }

        if (trigger.Cooldown > 0f && Time.realtimeSinceStartup - state.LastFireTime < trigger.Cooldown)
        {
            return;
        }

        state.Fired = true;
        state.LastFireTime = Time.realtimeSinceStartup;
        int count = 0;

        IEnumerable<JsonElement> scanEvents;
        if (trigger.UsePlayerCountEvents)
        {
            scanEvents = playersInScan >= 1 && playersInScan <= 4
                ? GetEventsForPlayerCount(trigger, playersInScan)
                : Enumerable.Empty<JsonElement>();
        }
        else
        {
            scanEvents = trigger.Events.Concat(trigger.WardenEvents);
        }

        count = ExecuteEventList(scanEvents, $"Scan trigger '{trigger.ID}'", sourcePosition);

        HandleScanTriggerCycle(config, trigger, eventName, puzzleIndex, sourcePosition);

        LogVerbose($"Scan trigger '{trigger.ID}' fired. Event={eventName}, Index={puzzleIndex}, PlayersInScan={playersInScan}, UsePlayerCountEvents={trigger.UsePlayerCountEvents}, Source={sourceName}, ExecutedEvents={count}");
    }

    private static void HandleScanTriggerCycle(ConfigDocument config, ScanTriggerRule trigger, string eventName, int puzzleIndex, Vector3? sourcePosition)
    {
        if (!trigger.UseTriggerCycleEvents || trigger.TriggerCycleEvents.Count == 0)
        {
            return;
        }

        string key = config.FilePath + "::scan-cycle::" + trigger.ID + "::" + NormalizeTriggerMode(eventName) + "::" + puzzleIndex;
        if (!ScanTriggerManager.RuleStates.TryGetValue(key, out TriggerState? cycleState))
        {
            cycleState = new TriggerState();
            ScanTriggerManager.RuleStates[key] = cycleState;
        }

        cycleState.CompletedCycles++;
        int required = Math.Max(1, trigger.TriggerCycleCount);
        LogVerbose($"Scan trigger cycle progress. ID={trigger.ID}, Event={eventName}, Index={puzzleIndex}, Cycles={cycleState.CompletedCycles}/{required}");
        if (cycleState.CompletedCycles < required)
        {
            return;
        }

        cycleState.CompletedCycles = 0;
        int count = ExecuteEventList(trigger.TriggerCycleEvents, $"Scan trigger '{trigger.ID}' cycle Event={eventName} Index={puzzleIndex}", sourcePosition);
        LogVerbose($"Scan trigger cycle events fired. ID={trigger.ID}, Event={eventName}, Index={puzzleIndex}, ExecutedEvents={count}");
    }

    internal static string NormalizeTriggerMode(string text)
    {
        return GetCachedNormalization(ScanModeNormalizationCache, text, static value =>
        {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return string.Empty;
                }

                return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
                {
                    "activate" => "onscanactivated",
                    "activated" => "onscanactivated",
                    "scanactivated" => "onscanactivated",
                    "onscanactivate" => "onscanactivated",
                    "onscanactivated" => "onscanactivated",

                    // 全员进入扫描点，触发一次。
                    "allenter" => "onallplayersenterscan",
                    "allplayersenter" => "onallplayersenterscan",
                    "allplayersentered" => "onallplayersenterscan",
                    "allplayersenterscan" => "onallplayersenterscan",
                    "allplayersenteredscan" => "onallplayersenterscan",
                    "onallplayersenter" => "onallplayersenterscan",
                    "onallplayersentered" => "onallplayersenterscan",
                    "onallplayersenterscan" => "onallplayersenterscan",
                    "onallplayersenteredscan" => "onallplayersenterscan",

                    // 全员在扫描点内，按 Cooldown 持续触发。
                    "allinside" => "onallplayersinsidescan",
                    "allplayersinside" => "onallplayersinsidescan",
                    "allplayersinscan" => "onallplayersinsidescan",
                    "allplayersinsidescan" => "onallplayersinsidescan",
                    "onallplayersinside" => "onallplayersinsidescan",
                    "onallplayersinscan" => "onallplayersinsidescan",
                    "onallplayersinsidescan" => "onallplayersinsidescan",

                    // 全员退出扫描点，触发一次。
                    "allexit" => "onallplayersexitscan",
                    "allleave" => "onallplayersexitscan",
                    "allplayersexit" => "onallplayersexitscan",
                    "allplayersleave" => "onallplayersexitscan",
                    "allplayersexitscan" => "onallplayersexitscan",
                    "allplayersleavescan" => "onallplayersexitscan",
                    "onallplayersexit" => "onallplayersexitscan",
                    "onallplayersleave" => "onallplayersexitscan",
                    "onallplayersexitscan" => "onallplayersexitscan",
                    "onallplayersleavescan" => "onallplayersexitscan",

                    // 全员退出扫描点后，按 Cooldown 持续触发。必须经历过一次“全员进入 -> 全员退出”。
                    "alloutside" => "onallplayersexitedscan",
                    "allexited" => "onallplayersexitedscan",
                    "allplayersoutside" => "onallplayersexitedscan",
                    "allplayersexited" => "onallplayersexitedscan",
                    "allplayersoutscan" => "onallplayersexitedscan",
                    "allplayersoutsidescan" => "onallplayersexitedscan",
                    "allplayersexitedscan" => "onallplayersexitedscan",
                    "onallplayersoutside" => "onallplayersexitedscan",
                    "onallplayersexited" => "onallplayersexitedscan",
                    "onallplayersoutscan" => "onallplayersexitedscan",
                    "onallplayersoutsidescan" => "onallplayersexitedscan",
                    "onallplayersexitedscan" => "onallplayersexitedscan",

                    "exit" => "onplayerexitscan",
                    "leave" => "onplayerexitscan",
                    "playerexit" => "onplayerexitscan",
                    "playerleave" => "onplayerexitscan",
                    "playerleavescan" => "onplayerexitscan",
                    "playerexitscan" => "onplayerexitscan",
                    "onplayerexit" => "onplayerexitscan",
                    "onplayerleave" => "onplayerexitscan",
                    "onplayerleavescan" => "onplayerexitscan",
                    "onplayerexitscan" => "onplayerexitscan",
                    _ => value.Trim().ToLowerInvariant()
                };
    
        });
    }

    internal static int ExecuteEventList(IEnumerable<JsonElement> events, string ownerLabel, Vector3? sourcePosition = null)
    {
        List<JsonElement> eventGroup = events as List<JsonElement> ?? events.ToList();
        if (eventGroup.Count == 0)
        {
            return 0;
        }

        int count = 0;
        foreach (JsonElement eventElement in eventGroup)
        {
            if (TryExecuteConfiguredEvent(eventElement, ownerLabel, sourcePosition))
            {
                count++;
            }
        }
        return count;
    }

    private static IEnumerable<JsonElement> GetEventsForPlayerCount(PositionTriggerRule trigger, int count)
    {
        return count switch
        {
            1 => trigger.OnePlayerEvents,
            2 => trigger.TwoPlayerEvents,
            3 => trigger.ThreePlayerEvents,
            4 => trigger.FourPlayerEvents,
            _ => Enumerable.Empty<JsonElement>()
        };
    }

    private static bool HasPlayerCountEvents(PositionTriggerRule trigger)
    {
        return trigger.OnePlayerEvents.Count > 0
            || trigger.TwoPlayerEvents.Count > 0
            || trigger.ThreePlayerEvents.Count > 0
            || trigger.FourPlayerEvents.Count > 0;
    }

    private static IEnumerable<JsonElement> GetEventsForPlayerCount(ScanTriggerRule trigger, int count)
    {
        return count switch
        {
            1 => trigger.OnePlayerEvents,
            2 => trigger.TwoPlayerEvents,
            3 => trigger.ThreePlayerEvents,
            4 => trigger.FourPlayerEvents,
            _ => Enumerable.Empty<JsonElement>()
        };
    }

    private static bool HasPlayerCountEvents(ScanTriggerRule trigger)
    {
        return trigger.OnePlayerEvents.Count > 0
            || trigger.TwoPlayerEvents.Count > 0
            || trigger.ThreePlayerEvents.Count > 0
            || trigger.FourPlayerEvents.Count > 0;
    }

    private static bool TryExecuteConfiguredEvent(JsonElement eventElement, string ownerLabel, Vector3? sourcePosition = null)
    {
        try
        {
            if (IsTriggerControlEvent(eventElement))
            {
                if (!CanExecuteConfiguredEvents(ownerLabel))
                {
                    return false;
                }

                if (TryHandleTriggerControlEvent(eventElement, ownerLabel))
                {
                    return true;
                }
            }

            // 0.8.9：WardenIntel 不再作为独立事件提前拦截。
            // 原版/AWO 逻辑中 WardenIntel 是 WardenObjectiveEventData 字段，
            // 必须随完整 WardenEvent 一起进入 WardenObjectiveManager.CheckAndExecuteEventsOnTrigger，
            // 这样普通原版事件、AWO 扩展事件、Legacy 事件都能同时携带 WardenIntel。

            if (TryGetCachedWardenEvent(eventElement, out WardenObjectiveEventData? eventData, ownerLabel) && eventData != null)
            {
                try
                {
                    if (!ExecuteConfiguredEventOnThisPeer(eventElement, eventData, ownerLabel, out bool allowClientHudExecution))
                    {
                        return false;
                    }

                    ApplyRuntimeEventContext(eventElement, eventData, sourcePosition, ownerLabel);
                    ExecuteWardenEvent(eventData, allowClientHudExecution);
                    return true;
                }
                catch (Exception ex)
                {
                    Log?.LogError($"{ownerLabel} failed to execute event '{eventData.Type}': {DescribeException(ex)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.LogError($"{ownerLabel} failed to execute configured event: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }


    private static void ApplyRuntimeEventContext(JsonElement rawEvent, WardenObjectiveEventData eventData, Vector3? sourcePosition, string ownerLabel)
    {
        // 1.1.6: CTE has a richer trigger context than SecuritySensor. SecuritySensor executes prebuilt
        // WardenObjectiveEventData as-is; CTE keeps that behavior, but for AWO events that require a runtime
        // position (notably SetPocketItem with TagType=Closest), CTE can safely supply the trigger/player/source
        // position when the event JSON did not specify Position or WorldEventObjectFilter/SpecialText.
        if (!sourcePosition.HasValue || !IsLikelyAwoEvent(rawEvent))
        {
            return;
        }

        Vector3 pos = sourcePosition.Value;
        if (!IsUsableRuntimePosition(pos))
        {
            return;
        }

        if (TryGetRawEventPosition(rawEvent, out Vector3 rawPos) && IsUsableRuntimePosition(rawPos))
        {
            return;
        }

        // 1.1.7：不要把 SpecialText 的复杂对象 JSON 当成有效 WorldEventObjectFilter。
        // AWO 的 SetPocketItem 等事件可能有 SpecialText/LocaleText 结构，旧逻辑会因为 GetString(object)=JSON 文本
        // 误判“已提供过滤器”，从而不写入 Position，最后 AWO 报 Position is zero。
        if (HasExplicitWorldEventObjectFilter(rawEvent))
        {
            return;
        }

        try
        {
            eventData.Position = pos;
            object? weeData = TryGetAwoWeeDataObject(eventData);
            if (weeData != null)
            {
                if (TrySetVector3Member(weeData, "Position", pos))
                {
                    LogVerbose($"{ownerLabel} supplied runtime Position={FormatVector(pos)} to AWO/WEE event Type='{GetString(rawEvent, "Type", string.Empty)}'.");
                }
                else
                {
                    LogThrottled($"{ownerLabel} could not write runtime Position to AWO/WEE data object '{weeData.GetType().FullName}'.");
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"{ownerLabel} failed to apply runtime AWO event position: {DescribeException(ex)}");
        }
    }

    private static bool TrySetVector3Member(object target, string memberName, Vector3 value)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanWrite && IsVector3Type(property.PropertyType))
            {
                property.SetValue(target, value);
                return true;
            }

            FieldInfo? field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && IsVector3Type(field.FieldType))
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsVector3Type(Type type)
    {
        return type == typeof(Vector3) || string.Equals(type.FullName, typeof(Vector3).FullName, StringComparison.Ordinal);
    }

    private static object? TryGetAwoWeeDataObject(WardenObjectiveEventData data)
    {
        try
        {
            MethodInfo? getMethod = FindAwoWeeDataMethod("GetWEEData", typeof(WardenObjectiveEventData));
            return getMethod?.Invoke(null, new object?[] { data });
        }
        catch
        {
            return null;
        }
    }

    private static bool HasExplicitWorldEventObjectFilter(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // WorldEventObjectFilter 是明确的对象过滤器字段；只有简单非空字符串才阻止 CTE 注入运行时 Position。
        if (element.TryGetProperty("WorldEventObjectFilter", out JsonElement filter))
        {
            if (filter.ValueKind == JsonValueKind.String)
            {
                return !string.IsNullOrWhiteSpace(filter.GetString());
            }
            // 兼容少量 LocaleText/对象写法，但只接受明确的 Text/Value/Filter 字符串。
            if (filter.ValueKind == JsonValueKind.Object && TryGetFirstNonEmptyString(filter, out _))
            {
                return true;
            }
        }

        // SpecialText 在 AWO 中既可作为 WorldEventObjectFilter，也可能是 LocaleText/其他事件参数。
        // 为避免误判，只有 SpecialText 是简单非空字符串时才把它视为过滤器。
        if (element.TryGetProperty("SpecialText", out JsonElement special) && special.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(special.GetString());
        }

        return false;
    }

    private static bool TryGetFirstNonEmptyString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string name in new[] { "Filter", "filter", "Value", "value", "Text", "text", "English", "english" })
        {
            if (element.TryGetProperty(name, out JsonElement nested) && nested.ValueKind == JsonValueKind.String)
            {
                value = nested.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetRawEventPosition(JsonElement element, out Vector3 position)
    {
        position = default;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Position", out JsonElement pos) || pos.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        position = new Vector3(
            GetFloat(pos, "x", GetFloat(pos, "X", 0f)),
            GetFloat(pos, "y", GetFloat(pos, "Y", 0f)),
            GetFloat(pos, "z", GetFloat(pos, "Z", 0f)));
        return true;
    }

    private static bool IsUsableRuntimePosition(Vector3 position)
    {
        return !(Math.Abs(position.x) < 0.001f && Math.Abs(position.y) < 0.001f && Math.Abs(position.z) < 0.001f)
            && !float.IsNaN(position.x) && !float.IsNaN(position.y) && !float.IsNaN(position.z)
            && !float.IsInfinity(position.x) && !float.IsInfinity(position.y) && !float.IsInfinity(position.z);
    }

    private static string FormatVector(Vector3 pos)
    {
        return $"({pos.x:0.00},{pos.y:0.00},{pos.z:0.00})";
    }

    private static bool TryHandleWardenIntelDirect(JsonElement element, string ownerLabel)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("WardenIntel", out JsonElement rawIntel))
            {
                return false;
            }

            string typeText = GetString(element, "Type", string.Empty).Trim();
            bool typeAllowsDirectIntel = string.IsNullOrWhiteSpace(typeText)
                || string.Equals(typeText, "WardenIntel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeText, "2", StringComparison.OrdinalIgnoreCase);
            if (!typeAllowsDirectIntel)
            {
                return false;
            }

            LG_LayerType layer = LG_LayerType.MainLayer;
            if (TryParseEnum(GetString(element, "Layer", "MainLayer"), out LG_LayerType parsedLayer))
            {
                layer = parsedLayer;
            }

            if (TryBuildLocalizedTextIdOnly(rawIntel, out LocalizedText? localized) && localized != null)
            {
                try
                {
                    WardenObjectiveManager.DisplayWardenIntel(layer, localized);
                    LogVerbose($"{ownerLabel} displayed vanilla WardenIntel. Layer={layer}, Value={rawIntel}");
                    return true;
                }
                catch (Exception displayEx)
                {
                    LogThrottled($"Direct DisplayWardenIntel failed for {ownerLabel}: {displayEx.GetType().Name}: {displayEx.Message}");
                    return false;
                }
            }

            // 开发期兼容：如果配置写了原始字符串，则只用 PlayerLayer 普通 HUD 文本显示；
            // 这不是官图 WardenIntel ID 路径。正式配置请写 TextDataBlock ID。
            if (TryReadLocalizedTextRawString(rawIntel, out string rawText) && !string.IsNullOrWhiteSpace(rawText))
            {
                try
                {
                    GuiManager.PlayerLayer.ShowWardenIntel(rawText, 0.0f, GetFloat(element, "WardenIntelDuration", GetFloat(element, "IntelDuration", 5.0f)));
                    LogVerbose($"{ownerLabel} used raw-string WardenIntel fallback. For vanilla behavior use a TextDataBlock ID instead. Text='{rawText}'");
                    return true;
                }
                catch (Exception guiEx)
                {
                    LogThrottled($"Raw-string WardenIntel fallback failed for {ownerLabel}: {guiEx.GetType().Name}: {guiEx.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"TryHandleWardenIntelDirect failed for {ownerLabel}: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    private static void ExecuteWardenEvent(WardenObjectiveEventData eventData, bool allowClientHudExecution = false)
    {
        if (!allowClientHudExecution && !CanExecuteConfiguredEvents($"WardenEvent Type='{eventData.Type}'"))
        {
            return;
        }

        try
        {
            // 1.3.5：复刻 ARA / EOSExt.SecuritySensor 的本地触发事件写法。
            // SecuritySensor 源码的 SensorTriggered 直接调用：
            //   WardenObjectiveManager.CheckAndExecuteEventsOnTrigger(e, e.Trigger, true)
            // AWO/EOS/Legacy 的 WardenEvent detour 可以按与 SecuritySensor 一致的调用形态接管。
            WardenObjectiveManager.CheckAndExecuteEventsOnTrigger(eventData, eventData.Trigger, true);
        }
        catch (Exception ex)
        {
            Log?.LogError($"Failed to execute WardenEvent Type='{eventData.Type}': {DescribeException(ex)}");
        }
    }

    private static void TryDisplayWardenIntel(WardenObjectiveEventData eventData, JsonElement rawEvent)
    {
        try
        {
            if (!TryResolveWardenIntelString(rawEvent, out string intel) || string.IsNullOrWhiteSpace(intel))
            {
                return;
            }

            bool displayed = false;

            try
            {
                // Normal HUD prompt path. The text has already been resolved through TextDataBlock/Text.Get,
                // so numeric IDs such as "1345" are not interpreted as RoleplayedWardenIntel IDs.
                GuiManager.PlayerLayer.ShowWardenIntel(intel, 0.0f, GetFloat(rawEvent, "WardenIntelDuration", GetFloat(rawEvent, "IntelDuration", 5.0f)));
                displayed = true;
            }
            catch (Exception guiEx)
            {
                LogThrottled($"PlayerGui ShowWardenIntel normal-text path failed for event '{eventData.Type}': {guiEx.GetType().Name}: {guiEx.Message}");
            }

            if (!displayed)
            {
                try
                {
                    GuiManager.PlayerLayer.m_gameEventLog.AddLogItem(intel, eGameEventChatLogType.Alert);
                    displayed = true;
                }
                catch (Exception logEx)
                {
                    LogThrottled($"GameEventLog text fallback failed for event '{eventData.Type}': {logEx.GetType().Name}: {logEx.Message}");
                }
            }

            if (!displayed)
            {
                LogThrottled($"WardenIntel normal text was present for event '{eventData.Type}' but no display path succeeded. Text='{intel}'");
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not display WardenIntel normal text for event '{eventData.Type}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryResolveWardenIntelString(JsonElement rawEvent, out string text)
    {
        text = string.Empty;
        if (rawEvent.ValueKind != JsonValueKind.Object || !rawEvent.TryGetProperty("WardenIntel", out JsonElement rawIntel))
        {
            return false;
        }

        return TryResolveTextDataString(rawIntel, out text);
    }

    private static bool TryResolveTextDataString(JsonElement rawValue, out string text)
    {
        text = string.Empty;
        try
        {
            if (rawValue.ValueKind == JsonValueKind.Number && rawValue.TryGetUInt32(out uint idNumber))
            {
                return TryGetTextDataBlockString(idNumber, out text);
            }

            if (rawValue.ValueKind == JsonValueKind.String)
            {
                string? value = rawValue.GetString();
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (uint.TryParse(value.Trim(), out uint idString))
                {
                    return TryGetTextDataBlockString(idString, out text);
                }

                text = value;
                return true;
            }

            if (rawValue.ValueKind == JsonValueKind.Object)
            {
                foreach (string field in new[] { "TextID", "TextId", "textID", "id", "ID", "persistentID", "PersistentID", "Value", "value" })
                {
                    if (rawValue.TryGetProperty(field, out JsonElement nested) && TryResolveTextDataString(nested, out text))
                    {
                        return true;
                    }
                }

                foreach (string field in new[] { "Text", "text", "UntranslatedText", "English" })
                {
                    if (rawValue.TryGetProperty(field, out JsonElement nested) && nested.ValueKind == JsonValueKind.String)
                    {
                        text = nested.GetString() ?? string.Empty;
                        return !string.IsNullOrWhiteSpace(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not resolve TextDataBlock text from json value '{rawValue}': {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static bool TryGetTextDataBlockString(uint id, out string text)
    {
        text = string.Empty;
        try
        {
            text = Text.Get(id);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Localization.Text.Get({id}) failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            TextDataBlock block = TextDataBlock.GetBlock(id);
            if (block != null)
            {
                text = block.GetText(Language.English, false);
                if (!string.IsNullOrWhiteSpace(text)) return true;
                text = block.English;
                if (!string.IsNullOrWhiteSpace(text)) return true;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"TextDataBlock.GetBlock({id}) normal text fallback failed: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static bool TryGetCachedWardenEvent(JsonElement element, out WardenObjectiveEventData? data, string ownerLabel)
    {
        data = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string key = NormalizeWardenIntelOnlyEventJson(element);
        if (WardenEventBuildCache.TryGetValue(key, out CachedWardenEventBuild? cached))
        {
            if (cached.Success && cached.EventData != null)
            {
                data = cached.EventData;
                return true;
            }

            if (!cached.FailureLogged)
            {
                Log?.LogError($"{ownerLabel} skipped cached invalid WardenEvent. {cached.Failure}");
                cached.FailureLogged = true;
            }
            return false;
        }

        if (WardenEventBuildCache.Count > WardenEventBuildCacheLimit)
        {
            WardenEventBuildCache.Clear();
        }

        CachedWardenEventBuild entry = new();
        if (TryBuildWardenEvent(element, out data, out string failure) && data != null)
        {
            entry.Success = true;
            entry.EventData = data;
            WardenEventBuildCache[key] = entry;
            return true;
        }

        entry.Success = false;
        entry.Failure = string.IsNullOrWhiteSpace(failure) ? $"Failed to build WardenEvent Type='{GetString(element, "Type", string.Empty)}'." : failure;
        entry.FailureLogged = true;
        WardenEventBuildCache[key] = entry;
        Log?.LogError($"{ownerLabel} failed to build WardenEvent. {entry.Failure}");
        return false;
    }

    private static bool TryBuildWardenEvent(JsonElement element, out WardenObjectiveEventData? data, out string failure)
    {
        data = null;
        failure = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = "Event element is not a JSON object.";
            return false;
        }

        // 1.1.3：按 Inas/EOS SecuritySensor 的兼容方式扩展到 AWO + Legacy。
        // SecuritySensor 的 EventsOnTrigger 字段直接是 List<WardenObjectiveEventData>，配置读取阶段会经由 EOSJson/InjectLib
        // 把 AWO 的 WEE_EventData 挂到 WardenObjectiveEventData.m_WEEDataRef，并让 EOSWardenEventManager 解析字符串 Type。
        // CTE 现在会预先缓存构建结果，AWO 事件必须带 WEEData，Legacy/EOS 字符串事件也优先走 EOSJson。
        bool requiresAwoWeeData = IsLikelyAwoEvent(element);
        bool isLikelyLegacyOrEosEvent = IsLikelyLegacyOrEosEvent(element);
        string typeTextForDiagnostics = GetString(element, "Type", string.Empty);
        bool legacyStringType = isLikelyLegacyOrEosEvent && !int.TryParse(typeTextForDiagnostics.Trim(), out _);
        bool shouldPreferJson = element.TryGetProperty("WardenIntel", out _) || requiresAwoWeeData || isLikelyLegacyOrEosEvent;

        if (shouldPreferJson)
        {
            if (TryBuildWardenEventViaEosJson(element, out data, out string eosFailure) && data != null)
            {
                NormalizeAwoEventTypeFromRaw(element, data);
                if (!requiresAwoWeeData || EnsureAwoWeeDataAttached(element, data, out string eosWeeFailure))
                {
                    LogVerbose($"CTE {(requiresAwoWeeData ? "AWO/WEE" : "Warden")} event build success via EOSJson. Type={GetString(element, "Type", string.Empty)}.");
                    return true;
                }

                Log?.LogError($"CTE AWO/WEE event build via EOSJson produced no WEE data for Type='{GetString(element, "Type", string.Empty)}'. {eosWeeFailure}");
                data = null;
                return false;
            }

            if (requiresAwoWeeData)
            {
                LogVerbose($"CTE EOSJson path unavailable for AWO/WEE Type='{GetString(element, "Type", string.Empty)}': {eosFailure}");
            }

            if (TryBuildWardenEventViaInjectLibJson(element, out data, out string injectFailure) && data != null)
            {
                NormalizeAwoEventTypeFromRaw(element, data);
                if (!requiresAwoWeeData || EnsureAwoWeeDataAttached(element, data, out string injectWeeFailure))
                {
                    LogVerbose($"CTE {(requiresAwoWeeData ? "AWO/WEE" : "Warden")} event build success via InjectLibJSON. Type={GetString(element, "Type", string.Empty)}.");
                    return true;
                }

                Log?.LogError($"CTE AWO/WEE event build via InjectLibJSON produced no WEE data for Type='{GetString(element, "Type", string.Empty)}'. {injectWeeFailure}");
                data = null;
                return false;
            }

            if (requiresAwoWeeData)
            {
                LogVerbose($"CTE InjectLibJSON path unavailable for AWO/WEE Type='{GetString(element, "Type", string.Empty)}': {injectFailure}");
            }

            // Plain Il2CppJsonNet is kept only as a last compatibility path for old Legacy/fork environments.
            // It is not enough for AWO unless GetWEEData succeeds afterwards.
            if (TryBuildWardenEventViaAwoJson(element, out data, out string il2cppFailure) && data != null)
            {
                NormalizeAwoEventTypeFromRaw(element, data);
                if (!requiresAwoWeeData || EnsureAwoWeeDataAttached(element, data, out string il2cppWeeFailure))
                {
                    LogVerbose($"CTE {(requiresAwoWeeData ? "AWO/WEE" : "Warden")} event build success via Il2CppJsonNet fallback. Type={GetString(element, "Type", string.Empty)}.");
                    return true;
                }

                Log?.LogError($"CTE AWO/WEE event build via Il2CppJsonNet fallback produced no WEE data for Type='{GetString(element, "Type", string.Empty)}'. {il2cppWeeFailure}");
                data = null;
                return false;
            }

            if (requiresAwoWeeData)
            {
                failure = $"CTE AWO/WEE event build failed for Type='{typeTextForDiagnostics}'. "
                    + $"EOSJson failed: {eosFailure}; InjectLibJSON failed: {injectFailure}; Il2CppJsonNet failed: {il2cppFailure}. "
                    + "Native fallback is disabled for AWO extension events.";
                return false;
            }

            if (legacyStringType)
            {
                failure = $"CTE Legacy/EOS string event build failed for Type='{typeTextForDiagnostics}'. "
                    + $"EOSJson failed: {eosFailure}; InjectLibJSON failed: {injectFailure}; Il2CppJsonNet failed: {il2cppFailure}. "
                    + "String Type events require EOSJson/EOSWardenEventManager or an InjectLib-aware JSON path; native enum fallback cannot parse Legacy string event names.";
                return false;
            }
        }

        if (TryBuildNativeWardenEvent(element, out data))
        {
            return true;
        }

        failure = $"Native WardenEvent builder failed for Type='{typeTextForDiagnostics}'.";
        return false;
    }

    private static void NormalizeAwoEventTypeFromRaw(JsonElement element, WardenObjectiveEventData data)
    {
        // 1.1.6: Some JSON paths can attach WEE data but leave the unmanaged enum value as None/0
        // in IL2CPP/forked environments. AWO's detour only routes events when data.Type matches a WEE_Type,
        // so restore the numeric WEE type from the raw JSON before caching/executing.
        if (!IsLikelyAwoEvent(element))
        {
            return;
        }

        if (!TryGetAwoEventTypeValueFromRaw(element, out int rawTypeValue) || rawTypeValue < 10000)
        {
            return;
        }

        int current = 0;
        try { current = Convert.ToInt32(data.Type); } catch { }
        if (current == rawTypeValue)
        {
            return;
        }

        try
        {
            data.Type = (eWardenObjectiveEventType)Enum.ToObject(typeof(eWardenObjectiveEventType), rawTypeValue);
            LogVerbose($"CTE normalized AWO/WEE event Type from '{current}' to '{rawTypeValue}'.");
        }
        catch (Exception ex)
        {
            LogThrottled($"CTE failed to normalize AWO/WEE event Type='{rawTypeValue}': {DescribeException(ex)}");
        }
    }

    private static bool TryGetAwoEventTypeValueFromRaw(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Type", out JsonElement typeElement))
        {
            return false;
        }

        try
        {
            if (typeElement.ValueKind == JsonValueKind.Number && typeElement.TryGetInt32(out int numeric))
            {
                value = numeric;
                return true;
            }

            string text = typeElement.ValueKind == JsonValueKind.String ? (typeElement.GetString() ?? string.Empty) : typeElement.ToString();
            if (int.TryParse(text.Trim(), out numeric))
            {
                value = numeric;
                return true;
            }

            return TryResolveAwoEventTypeValue(text, out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryBuildNativeWardenEvent(JsonElement element, out WardenObjectiveEventData? data)
    {
        data = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string typeText = GetString(element, "Type", string.Empty).Trim();
        eWardenObjectiveEventType type = eWardenObjectiveEventType.None;

        // GTFO 原版枚举中没有 WardenIntel 事件类型；WardenIntel 是任意事件都可携带的字段。
        // 因此 { "WardenIntel": 1345 } 或 { "Type": "WardenIntel", "WardenIntel": 1345 }
        // 都会被构造成 Type=None + WardenIntel 字段，再交给 WardenObjectiveManager 执行。
        bool hasWardenIntel = element.TryGetProperty("WardenIntel", out _);
        bool wardenIntelOnlyAlias = string.Equals(typeText, "WardenIntel", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(typeText) && !wardenIntelOnlyAlias && !TryParseEnum(typeText, out type))
        {
            Log?.LogWarning($"Skipping event with invalid Type='{typeText}'.");
            return false;
        }

        WardenObjectiveEventData e = new WardenObjectiveEventData();
        e.Type = (string.IsNullOrWhiteSpace(typeText) || wardenIntelOnlyAlias) && hasWardenIntel ? eWardenObjectiveEventType.None : type;
        e.Delay = GetFloat(element, "Delay", 0f);
        e.Duration = GetFloat(element, "Duration", 0f);
        e.Enabled = GetBool(element, "Enabled", true);
        e.Count = GetInt(element, "Count", 0);
        e.ChainPuzzle = GetUInt(element, "ChainPuzzle", GetUInt(element, "ChainPuzzleID", 0));
        e.UseStaticBioscanPoints = GetBool(element, "UseStaticBioscanPoints", false);
        e.ClearDimension = GetBool(element, "ClearDimension", false);
        // WardenIntel 只接受 TextDataBlock ID / persistentID，避免原始字符串创建临时 LocalizedText。
        if (TryGetLocalizedTextIdOnly(element, "WardenIntel", out LocalizedText? wardenIntel)) e.WardenIntel = wardenIntel;
        if (TryGetLocalizedText(element, "CustomSubObjectiveHeader", out LocalizedText? customSubObjectiveHeader)) e.CustomSubObjectiveHeader = customSubObjectiveHeader;
        if (TryGetLocalizedText(element, "CustomSubObjective", out LocalizedText? customSubObjective)) e.CustomSubObjective = customSubObjective;
        if (TryGetLocalizedText(element, "SoundSubtitle", out LocalizedText? soundSubtitle)) e.SoundSubtitle = soundSubtitle;
        e.SoundID = GetUInt(element, "SoundID", 0);
        e.DialogueID = GetUInt(element, "DialogueID", 0);
        e.FogSetting = GetUInt(element, "FogSetting", 0);
        e.FogTransitionDuration = GetFloat(element, "FogTransitionDuration", 0f);
        e.EnemyID = GetUInt(element, "EnemyID", 0);
        e.WorldEventObjectFilter = GetString(element, "WorldEventObjectFilter", string.Empty);

        if (TryParseEnum(GetString(element, "Trigger", string.Empty), out eWardenObjectiveEventTrigger trigger)) e.Trigger = trigger;
        if (TryParseEnum(GetString(element, "Layer", string.Empty), out LG_LayerType layer)) e.Layer = layer;
        if (TryParseEnum(GetString(element, "DimensionIndex", string.Empty), out eDimensionIndex dim)) e.DimensionIndex = dim;
        if (TryParseEnum(GetString(element, "LocalIndex", string.Empty), out eLocalZoneIndex zone)) e.LocalIndex = zone;

        if (element.TryGetProperty("Position", out JsonElement pos) && pos.ValueKind == JsonValueKind.Object)
        {
            e.Position = new Vector3(GetFloat(pos, "x", GetFloat(pos, "X", 0)), GetFloat(pos, "y", GetFloat(pos, "Y", 0)), GetFloat(pos, "z", GetFloat(pos, "Z", 0)));
        }

        if (element.TryGetProperty("Condition", out JsonElement condition) && condition.ValueKind == JsonValueKind.Object)
        {
            WorldEventConditionPair pair = new WorldEventConditionPair();
            pair.ConditionIndex = GetInt(condition, "ConditionIndex", GetInt(condition, "Index", 0));
            pair.IsTrue = GetBool(condition, "IsTrue", GetBool(condition, "Value", true));
            e.Condition = pair;
        }
        else if (element.TryGetProperty("ConditionIndex", out _))
        {
            WorldEventConditionPair pair = new WorldEventConditionPair();
            pair.ConditionIndex = GetInt(element, "ConditionIndex", 0);
            pair.IsTrue = GetBool(element, "ConditionValue", GetBool(element, "Value", true));
            e.Condition = pair;
        }

        if (element.TryGetProperty("EnemyWaveData", out JsonElement wave) && wave.ValueKind == JsonValueKind.Object)
        {
            GenericEnemyWaveData waveData = new GenericEnemyWaveData();
            waveData.WaveSettings = GetUInt(wave, "WaveSettings", 0);
            waveData.WavePopulation = GetUInt(wave, "WavePopulation", 0);
            waveData.AreaDistance = GetInt(wave, "AreaDistance", 0);
            waveData.WorldEventObjectFilterSpawnPoint = GetString(wave, "WorldEventObjectFilterSpawnPoint", string.Empty);
            waveData.SpawnDelay = GetFloat(wave, "SpawnDelay", 0f);
            waveData.TriggerAlarm = GetBool(wave, "TriggerAlarm", false);
            e.EnemyWaveData = waveData;
        }
        else if (element.TryGetProperty("WaveSettings", out _) || element.TryGetProperty("WavePopulation", out _))
        {
            GenericEnemyWaveData waveData = new GenericEnemyWaveData();
            waveData.WaveSettings = GetUInt(element, "WaveSettings", 0);
            waveData.WavePopulation = GetUInt(element, "WavePopulation", 0);
            waveData.AreaDistance = GetInt(element, "AreaDistance", 0);
            waveData.WorldEventObjectFilterSpawnPoint = GetString(element, "WorldEventObjectFilterSpawnPoint", string.Empty);
            waveData.SpawnDelay = GetFloat(element, "SpawnDelay", 0f);
            waveData.TriggerAlarm = GetBool(element, "TriggerAlarm", false);
            e.EnemyWaveData = waveData;
        }

        data = e;
        return true;
    }

    private static bool IsLikelyAwoEvent(JsonElement element)
    {
        string typeText = GetString(element, "Type", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (int.TryParse(typeText, out int numericType))
        {
            return numericType >= 10000;
        }

        if (TryParseEnum(typeText, out eWardenObjectiveEventType _))
        {
            return HasAnyAwoOnlyField(element);
        }

        if (IsKnownAwoStringEventType(typeText))
        {
            return true;
        }

        if (TryResolveAwoEventTypeValue(typeText, out _))
        {
            return true;
        }

        return HasAnyAwoOnlyField(element);
    }

    private static bool HasAnyAwoOnlyField(JsonElement element)
    {
        foreach (string field in new[]
        {
            "SpecialBool", "SpecialNumber", "SpecialText", "SubObjective", "Fog", "Reactor", "Countdown",
            "Countup", "CleanupEnemies", "SpawnHibernates", "SpawnScouts", "AddTerminalCommand", "AddCommand",
            "HideTerminalCommand", "HideCommand", "UnhideTerminalCommand", "UnhideCommand", "GiveResource",
            "ActiveEnemyWave", "NestedEvent", "StartEventLoop", "EventLoop", "TeleportPlayer", "InfectPlayer",
            "DamagePlayer", "RevivePlayer", "AdjustTimer", "NavMarker", "CameraShake", "Portal", "SuccessScreen",
            "MultiProgression", "WaveRoarSound", "CustomHudText", "CustomHud", "SpecialHudTimer", "SpecialHud",
            "PlayerDialogue", "SetTerminalLog", "TerminalLog", "ObjectiveItems", "DimensionData", "EnvironmentData",
            "SetPocketItem", "PocketItem", "OutsideDimensionData", "ExpeditionEnvironment", "ClearWardenIntelQueue"
        })
        {
            if (element.TryGetProperty(field, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownAwoStringEventType(string typeText)
    {
        string normalized = NormalizeDisplayEventType(typeText);
        return normalized is
            "closesecuritydoor" or "locksecuritydoor" or "setdoorinteraction" or
            "triggersecuritydooralarm" or "solvesecuritydooralarm" or
            "startreactor" or "modifyreactorwavestate" or "forcecompletereactor" or
            "forcecompletelevel" or "forcefaillevel" or "countdown" or
            "setlevelfailcheckenabled" or "setlevelfailwhenanyplayerdowned" or
            "killallplayers" or "killplayersinzone" or "solvesingleobjectiveitem" or
            "setlightdatainzone" or "alertenemiesinzone" or "cleanupenemiesinzone" or
            "spawnhibernateinzone" or "spawnscoutinzone" or "savecheckpoint" or
            "moveextractionworldposition" or "setblackoutenabled" or
            "addterminalcommand" or "hideterminalcommand" or "unhideterminalcommand" or
            "addchainpuzzletosecuritydoor" or "setactiveenemywave" or "giveresource" or
            "nestedevent" or "starteventloop" or "stopeventloop" or
            "teleportplayer" or "infectplayer" or "damageplayer" or "reviveplayer" or
            "adjustawotimer" or "countup" or "forcecompletechainpuzzle" or
            "spawnnavmarker" or "shakescreen" or "startportalmachine" or
            "setsuccessscreen" or "playsubtitles" or "multiprogression" or
            "playwaveroarsound" or "customhudtext" or "specialhudtimer" or
            "forceplayplayerdialogue" or "setterminallog" or "setpocketitem" or
            "dointeractweakdoorsinzone" or "toggleinteractweakdoorsinzone" or
            "pickupsentries" or "setoutsidedimensiondata" or "setexpeditionenvironment" or
            "clearwardenintelqueue" or "resetonapproachdoor";
    }

    private static bool IsLikelyLegacyOrEosEvent(JsonElement element)
    {
        string typeText = GetString(element, "Type", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (int.TryParse(typeText, out int typeValue))
        {
            return IsKnownLegacyOrEosEventId(typeValue);
        }

        string normalized = typeText.Trim().Replace("-", "_").Replace(" ", "_");
        return KnownLegacyOrEosEventNames.Contains(normalized);
    }

    private static bool IsKnownLegacyOrEosEventId(int typeValue)
    {
        return typeValue == 100 || typeValue == 102
            || typeValue == 107 || typeValue == 108
            || typeValue == 130
            || typeValue == 140 || typeValue == 141 || typeValue == 142
            || typeValue == 156
            || typeValue == 170 || typeValue == 180
            || (typeValue >= 210 && typeValue <= 217)
            || (typeValue >= 220 && typeValue <= 225)
            || (typeValue >= 250 && typeValue <= 252)
            || typeValue == 260 || typeValue == 261
            || typeValue == 270 || typeValue == 280
            || typeValue == 400;
    }

    private static readonly HashSet<string> KnownLegacyOrEosEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // LEGACY.ExtraEvents.LegacyExtraEvents.EventType registrations
        "CloseSecurityDoor_Custom",
        "SetTimerTitle",
        "AlertEnemiesInZone",
        "AlertEnemiesInArea",
        "Terminal_ShowTerminalInfoInZone",
        "KillEnemiesInArea",
        "KillEnemiesInZone",
        "KillEnemiesInDimension",
        "SpawnHibernate",
        "PlayGCEndSequence",
        "FF_ToggleFFCheck",
        "FF_AddPlayersInRangeToCheck",
        "FF_AddPlayersOutOfRangeToCheck",
        "FF_ToggleCheckOnGroup",
        "FF_Reset",
        "FF_ResetGroup",
        "FF_SetExpeditionFailedText",
        "FF_ResetExpeditionFailedText",
        "SetNavMarker",
        "ToggleDummyVisual",
        "ToggleLSFBState",
        "SaveCheckpoint",
        "SetSuccessPageCustomization",
        "ToggleCamaraShake",
        "Info_ZoneHibernate",
        "Info_LevelHibernate",
        "Output_LevelHibernateSpawnEvent",
        "PlayMusic",
        "StopMusic",
        "ToggleEventScanState",
        "ToggleSeamlessReload",
        // LEGACY ResourceStation registration
        "ResourceStation_SetEnabled",
        // EOSExt.SecuritySensor registration
        "ToggleSensorGroupState"
    };

    private static bool TryBuildWardenEventViaEosJson(JsonElement element, out WardenObjectiveEventData? data, out string failure)
    {
        data = null;
        failure = string.Empty;
        try
        {
            Type? eosJsonType = FindLoadedType("ExtraObjectiveSetup.JSON.EOSJson") ?? FindLoadedTypeByName("EOSJson");
            if (eosJsonType == null)
            {
                failure = "ExtraObjectiveSetup.JSON.EOSJson was not found.";
                return false;
            }

            string json = NormalizeWardenIntelOnlyEventJson(element);
            if (TryDeserializeWardenEventWithGenericMethod(eosJsonType, "Deserialize", json, out data, out failure))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            failure = DescribeException(ex);
        }
        return false;
    }

    private static bool TryBuildWardenEventViaInjectLibJson(JsonElement element, out WardenObjectiveEventData? data, out string failure)
    {
        data = null;
        failure = string.Empty;
        try
        {
            Type? injectLibJsonType = FindLoadedType("InjectLib.JsonNETInjection.Supports.InjectLibJSON") ?? FindLoadedTypeByName("InjectLibJSON");
            if (injectLibJsonType == null)
            {
                failure = "InjectLib.JsonNETInjection.Supports.InjectLibJSON was not found.";
                return false;
            }

            string json = NormalizeWardenIntelOnlyEventJson(element);
            if (TryDeserializeWardenEventWithGenericMethod(injectLibJsonType, "Deserialize", json, out data, out failure))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            failure = DescribeException(ex);
        }
        return false;
    }

    private static bool TryBuildWardenEventViaAwoJson(JsonElement element, out WardenObjectiveEventData? data, out string failure)
    {
        data = null;
        failure = string.Empty;
        try
        {
            // Plain Il2CppJsonNet fallback. This path may not run InjectLib handlers in every environment,
            // so AWO events still require a successful GetWEEData check after this returns.
            Type? jsonConvertType = FindLoadedType("Il2CppJsonNet.JsonConvert");
            if (jsonConvertType == null)
            {
                failure = "Il2CppJsonNet.JsonConvert was not found.";
                return false;
            }

            string json = NormalizeWardenIntelOnlyEventJson(element);
            if (TryDeserializeWardenEventWithGenericMethod(jsonConvertType, "DeserializeObject", json, out data, out failure))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            failure = DescribeException(ex);
        }
        return false;
    }

    private static bool TryDeserializeWardenEventWithGenericMethod(Type ownerType, string methodName, string json, out WardenObjectiveEventData? data, out string failure)
    {
        data = null;
        failure = string.Empty;
        List<string> attempts = new();
        try
        {
            foreach (MethodInfo method in ownerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == methodName))
            {
                try
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    object? parsed = null;

                    if (method.IsGenericMethodDefinition && parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        parsed = method.MakeGenericMethod(typeof(WardenObjectiveEventData)).Invoke(null, new object?[] { json });
                    }
                    else if (!method.IsGenericMethodDefinition && parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(Type))
                    {
                        parsed = method.Invoke(null, new object?[] { json, typeof(WardenObjectiveEventData) });
                    }
                    else if (!method.IsGenericMethodDefinition && parameters.Length == 2 && parameters[0].ParameterType == typeof(Type) && parameters[1].ParameterType == typeof(string))
                    {
                        parsed = method.Invoke(null, new object?[] { typeof(WardenObjectiveEventData), json });
                    }
                    else
                    {
                        continue;
                    }

                    if (parsed is WardenObjectiveEventData e)
                    {
                        data = e;
                        return true;
                    }

                    attempts.Add($"{ownerType.FullName}.{methodName} returned {parsed?.GetType().FullName ?? "null"}");
                }
                catch (Exception ex)
                {
                    attempts.Add($"{ownerType.FullName}.{methodName} overload failed: {DescribeException(ex)}");
                }
            }

            failure = attempts.Count == 0
                ? $"No supported {ownerType.FullName}.{methodName} overload was found. Expected Deserialize<T>(string), Deserialize(string, Type), or Deserialize(Type, string)."
                : string.Join(" | ", attempts);
            return false;
        }
        catch (Exception ex)
        {
            failure = DescribeException(ex);
            return false;
        }
    }

    private static bool EnsureAwoWeeDataAttached(JsonElement element, WardenObjectiveEventData data, out string failure)
    {
        // 1.1.2：不再把“手动补挂 WEE_EventData”作为主路径。
        // 正确路径应是 EOSJson/InjectLibJSON 在反序列化 WardenObjectiveEventData 时触发 AWO EventDataHandler，
        // 自动 SetWEEData。手动补挂在部分环境下会 TargetInvocationException，且容易掩盖真实 JSON/字段错误。
        if (HasAwoWeeDataAttached(data, out failure))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(failure))
        {
            failure = "AWO WEE dataholder m_WEEDataRef is missing. EOSJson/InjectLibJSON did not attach WEE_EventData.";
        }
        return false;
    }

    private static bool HasAwoWeeDataAttached(WardenObjectiveEventData data, out string failure)
    {
        failure = string.Empty;
        try
        {
            MethodInfo? getMethod = FindAwoWeeDataMethod("GetWEEData", typeof(WardenObjectiveEventData));
            if (getMethod == null)
            {
                failure = "AWO.CustomFields.WOEventDataFields.GetWEEData was not found. AWO may be missing or not initialized.";
                return false;
            }

            object? weeData = getMethod.Invoke(null, new object?[] { data });
            if (weeData != null)
            {
                return true;
            }

            failure = "AWO GetWEEData returned null. Event was built without m_WEEDataRef.";
            return false;
        }
        catch (Exception ex)
        {
            failure = $"AWO GetWEEData reflection failed: {DescribeException(ex)}";
            return false;
        }
    }

    private static bool TryAttachAwoWeeDataManually(JsonElement element, WardenObjectiveEventData data, out string failure)
    {
        failure = string.Empty;
        try
        {
            Type? weeDataType = FindLoadedType("AWO.Modules.WEE.WEE_EventData");
            if (weeDataType == null)
            {
                failure = "AWO.Modules.WEE.WEE_EventData type was not found.";
                return false;
            }

            Type? jsonConvertType = FindLoadedType("Il2CppJsonNet.JsonConvert");
            if (jsonConvertType == null)
            {
                failure = "Il2CppJsonNet.JsonConvert was not found.";
                return false;
            }

            MethodInfo? deserialize = jsonConvertType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "DeserializeObject" && m.IsGenericMethodDefinition)
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                });
            if (deserialize == null)
            {
                failure = "Il2CppJsonNet.JsonConvert.DeserializeObject<T>(string) was not found.";
                return false;
            }

            if (TryResolveAwoEventTypeValue(GetString(element, "Type", string.Empty), out int typeValue))
            {
                data.Type = (eWardenObjectiveEventType)typeValue;
            }

            string json = NormalizeWardenIntelOnlyEventJson(element);
            object? weeData = deserialize.MakeGenericMethod(weeDataType).Invoke(null, new object?[] { json });
            if (weeData == null)
            {
                failure = "AWO WEE_EventData deserialization returned null.";
                return false;
            }

            MethodInfo? setMethod = FindAwoWeeDataMethod("SetWEEData", typeof(WardenObjectiveEventData), weeDataType);
            if (setMethod == null)
            {
                failure = "AWO.CustomFields.WOEventDataFields.SetWEEData was not found.";
                return false;
            }

            setMethod.Invoke(null, new object?[] { data, weeData });
            return true;
        }
        catch (Exception ex)
        {
            failure = $"manual WEE attachment failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static MethodInfo? FindAwoWeeDataMethod(string methodName, params Type[] parameterTypes)
    {
        // 1.1.6: Never enumerate every type in every loaded IL2CPP assembly here.
        // Some GTFO/Unity/IL2CPP assemblies throw ReflectionTypeLoadException during GetTypes()
        // ("Missing definition for required runtime implemented delegate method"), which previously made
        // valid AWO events fail before GetWEEData could even be called.
        Type? type = FindAwoWeeDataFieldsType();
        if (type == null)
        {
            return null;
        }

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return method;
            }
        }
        return null;
    }

    private static Type? FindAwoWeeDataFieldsType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? exact = assembly.GetType("AWO.CustomFields.WOEventDataFields", false, false);
                if (exact != null)
                {
                    return exact;
                }
            }
            catch
            {
                // Ignore assemblies that do not allow metadata lookup.
            }
        }

        // Fallback only scans assemblies that are likely to be AWO. This keeps the lookup safe and cheap.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string assemblyName = string.Empty;
            try
            {
                assemblyName = assembly.GetName().Name ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (assemblyName.IndexOf("AWO", StringComparison.OrdinalIgnoreCase) < 0
                && assemblyName.IndexOf("WardenObjective", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (Type type in SafeGetTypes(assembly))
            {
                if (string.Equals(type.FullName, "AWO.CustomFields.WOEventDataFields", StringComparison.Ordinal)
                    || string.Equals(type.Name, "WOEventDataFields", StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool TryResolveAwoEventTypeValue(string typeText, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (int.TryParse(typeText.Trim(), out value))
        {
            return value >= 10000;
        }

        string normalized = NormalizeDisplayEventType(typeText);
        switch (normalized)
        {
            case "countdown": value = 10010; return true;
            case "countup": value = 20008; return true;
            case "adjustawotimer": value = 20007; return true;
            case "spawnnavmarker": value = 20010; return true;
            case "shakescreen": value = 20011; return true;
            case "playsubtitles": value = 20014; return true;
            case "multiprogression": value = 20015; return true;
            case "playwaveroarsound": value = 20016; return true;
            case "customhudtext": value = 20017; return true;
            case "customhud": value = 20017; return true;
            case "specialhud": value = 20017; return true;
            case "specialhudtimer": value = 20018; return true;
            case "customsubobjective": value = 0; return false;
            case "customsubobjectiveheader": value = 0; return false;
            case "subobjective": value = 0; return false;
            case "subobjectiveheader": value = 0; return false;
            case "forceplayplayerdialogue": value = 20019; return true;
            case "clearwardenintelqueue": value = 20027; return true;
        }

        Type? weeType = FindLoadedType("AWO.Modules.WEE.WEE_Type");
        if (weeType == null || !weeType.IsEnum)
        {
            return false;
        }

        try
        {
            object parsed = Enum.Parse(weeType, typeText.Trim(), true);
            value = Convert.ToInt32(parsed);
            return value >= 10000;
        }
        catch
        {
            return false;
        }
    }


    private static string NormalizeWardenIntelOnlyEventJson(JsonElement element)
    {
        try
        {
            // AWO/Il2CppJsonNet can parse WardenObjectiveEventData, but "Type": "WardenIntel"
            // is not a real vanilla enum value. Treat this user-friendly alias as Type=None while
            // keeping the WardenIntel field intact. This mirrors the fact that WardenIntel is a field,
            // not an eWardenObjectiveEventType.
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("Type", out JsonElement typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && string.Equals(typeElement.GetString(), "WardenIntel", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object?> obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "Type", StringComparison.OrdinalIgnoreCase))
                    {
                        obj[prop.Name] = "None";
                    }
                    else
                    {
                        obj[prop.Name] = JsonElementToPlainObject(prop.Value);
                    }
                }
                return JsonSerializer.Serialize(obj);
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"NormalizeWardenIntelOnlyEventJson failed: {ex.GetType().Name}: {ex.Message}");
        }
        return element.GetRawText();
    }

    private static object? JsonElementToPlainObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    dict[prop.Name] = JsonElementToPlainObject(prop.Value);
                }
                return dict;
            case JsonValueKind.Array:
                List<object?> list = new List<object?>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(JsonElementToPlainObject(item));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                if (element.TryGetDouble(out double d)) return d;
                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    private static bool MatchesCurrentLevel(ConfigDocument config, uint currentLayoutId, string currentLayoutName, out string reason)
    {
        reason = string.Empty;
        if (config.MainLevelLayoutIDs == null || config.MainLevelLayoutIDs.Count == 0)
        {
            return false;
        }

        foreach (JsonElement selector in config.MainLevelLayoutIDs)
        {
            foreach (string token in ExtractSelectorTokens(selector))
            {
                if (MatchesCurrentLevelLayoutString(token, currentLayoutId, currentLayoutName, out reason))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> ExtractSelectorTokens(JsonElement selector)
    {
        if (selector.ValueKind == JsonValueKind.Number && selector.TryGetUInt32(out uint n))
        {
            yield return n.ToString();
            yield break;
        }

        if (selector.ValueKind == JsonValueKind.String)
        {
            string? s = selector.GetString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
            yield break;
        }

        if (selector.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "persistentID", "PersistentID", "id", "ID", "value", "Value", "LevelLayoutID", "MainLevelLayoutID" })
            {
                if (selector.TryGetProperty(name, out JsonElement value))
                {
                    foreach (string token in ExtractSelectorTokens(value))
                    {
                        yield return token;
                    }
                }
            }
        }
    }

    private static bool MatchesCurrentLevelLayoutString(string token, uint currentLayoutId, string currentLayoutName, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        string normalized = token.Trim();

        if (currentLayoutId != 0u && string.Equals(normalized, currentLayoutId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"numeric text:{normalized}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentLayoutName) && string.Equals(normalized, currentLayoutName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"LevelLayoutDataBlock name:{normalized}";
            return true;
        }

        if (TryResolvePartialDataPersistentId(normalized, out uint partialDataId) && partialDataId == currentLayoutId)
        {
            reason = $"MTFO PartialData persistentID:{normalized}->{partialDataId}";
            return true;
        }

        if (TryResolveLevelLayoutStringToId(normalized, out uint resolvedId) && resolvedId == currentLayoutId)
        {
            reason = $"LevelLayoutDataBlock string:{normalized}->{resolvedId}";
            return true;
        }

        return false;
    }

    private static bool TryResolvePartialDataPersistentId(string persistentId, out uint id)
    {
        id = 0u;
        if (string.IsNullOrWhiteSpace(persistentId)) return false;
        if (PartialDataPersistentIdCache.TryGetValue(persistentId, out uint cachedPartialDataId))
        {
            id = cachedPartialDataId;
            return cachedPartialDataId != 0u;
        }

        try
        {
            Type? managerType = FindLoadedType("MTFO.Ext.PartialData.PersistentIDManager");
            MethodInfo? tryGetId = managerType?.GetMethod("TryGetId", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(uint).MakeByRefType() }, null);
            if (tryGetId != null)
            {
                object?[] args = { persistentId, 0u };
                object? ok = tryGetId.Invoke(null, args);
                if (ok is bool b && b && args[1] is uint resolved && resolved != 0u)
                {
                    id = resolved;
                    PartialDataPersistentIdCache[persistentId] = id;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not resolve PartialData persistentID '{persistentId}' from runtime manager: {ex.Message}");
        }

        bool foundInDump = TryResolvePersistentIdFromDumpFiles(persistentId, out id);
        PartialDataPersistentIdCache[persistentId] = foundInDump ? id : 0u;
        return foundInDump;
    }

    private static bool TryResolvePersistentIdFromDumpFiles(string persistentId, out uint id)
    {
        id = 0u;
        try
        {
            Dictionary<string, uint> map = GetOrBuildPersistentIdDumpCache();
            return map.TryGetValue(persistentId, out id) && id != 0u;
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not scan PartialData persistentID dump files: {ex.Message}");
            return false;
        }
    }

    private static Dictionary<string, uint> GetOrBuildPersistentIdDumpCache()
    {
        if (PersistentIdDumpCache != null) return PersistentIdDumpCache;

        Dictionary<string, uint> result = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in Directory.GetFiles(Paths.PluginPath, "_persistentID.json", SearchOption.AllDirectories))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement entry in document.RootElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string guid = GetString(entry, "GUID", GetString(entry, "persistentID", GetString(entry, "PersistentID", string.Empty)));
                        uint value = GetUInt(entry, "ID", GetUInt(entry, "id", 0u));
                        if (value != 0u && !string.IsNullOrWhiteSpace(guid))
                        {
                            result[guid] = value;
                        }
                    }
                }
                catch (Exception fileEx)
                {
                    LogThrottled($"Could not read PartialData dump '{file}': {fileEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not scan PartialData persistentID dump files: {ex.Message}");
        }

        PersistentIdDumpCache = result;
        return result;
    }

    private static bool TryResolveLevelLayoutStringToId(string levelLayoutString, out uint id)
    {
        id = 0u;
        if (string.IsNullOrWhiteSpace(levelLayoutString)) return false;
        if (LevelLayoutStringIdCache.TryGetValue(levelLayoutString, out uint cachedLayoutId))
        {
            id = cachedLayoutId;
            return cachedLayoutId != 0u;
        }

        try
        {
            if (uint.TryParse(levelLayoutString, out uint parsed))
            {
                id = parsed;
                LevelLayoutStringIdCache[levelLayoutString] = id;
                return parsed != 0u;
            }
            if (LevelLayoutDataBlock.HasBlock(levelLayoutString))
            {
                id = LevelLayoutDataBlock.GetBlockID(levelLayoutString);
                LevelLayoutStringIdCache[levelLayoutString] = id;
                return id != 0u;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not resolve LevelLayout string '{levelLayoutString}': {ex.Message}");
        }
        LevelLayoutStringIdCache[levelLayoutString] = 0u;
        return false;
    }

    private static uint GetCurrentLevelLayoutId()
    {
        try { return RundownManager.ActiveExpedition.LevelLayoutData; } catch { return 0u; }
    }

    private static string TryGetLevelLayoutName(uint id)
    {
        if (id == 0u) return string.Empty;
        try
        {
            string name = LevelLayoutDataBlock.GetBlockName(id);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        try
        {
            LevelLayoutDataBlock block = LevelLayoutDataBlock.GetBlock(id);
            if (block != null && !string.IsNullOrWhiteSpace(block.name)) return block.name;
        }
        catch { }
        return string.Empty;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false, false);
            if (type != null) return type;
        }
        return null;
    }

    private static Type? FindLoadedTypeByName(string shortName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (string.Equals(type.Name, shortName, StringComparison.Ordinal)
                        || string.Equals(type.FullName, shortName, StringComparison.Ordinal)
                        || (type.FullName != null && type.FullName.EndsWith("." + shortName, StringComparison.Ordinal)))
                    {
                        return type;
                    }
                }
            }
            catch
            {
                // Some IL2CPP/runtime assemblies cannot enumerate all types. Ignore and continue.
            }
        }
        return null;
    }

    private static string DescribeException(Exception ex)
    {
        if (ex is TargetInvocationException tie && tie.InnerException != null)
        {
            return $"TargetInvocationException -> {DescribeException(tie.InnerException)}";
        }

        if (ex.InnerException != null)
        {
            return $"{ex.GetType().Name}: {ex.Message} -> {DescribeException(ex.InnerException)}";
        }

        return $"{ex.GetType().Name}: {ex.Message}";
    }

    private static bool TryParseEnum<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (Enum.TryParse(text, true, out value)) return true;
        if (int.TryParse(text, out int i))
        {
            value = (T)Enum.ToObject(typeof(T), i);
            return true;
        }
        return false;
    }

    private static string GetString(JsonElement obj, string name, string defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? defaultValue;
            return v.ToString();
        }
        return defaultValue;
    }

    private static bool GetBool(JsonElement obj, string name, bool defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i != 0;
        }
        return defaultValue;
    }

    private static float GetFloat(JsonElement obj, string name, float defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)) return f;
            if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(), out float sf)) return sf;
        }
        return defaultValue;
    }

    private static int GetInt(JsonElement obj, string name, int defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int si)) return si;
        }
        return defaultValue;
    }

    private static uint GetUInt(JsonElement obj, string name, uint defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint i)) return i;
            if (v.ValueKind == JsonValueKind.String && uint.TryParse(v.GetString(), out uint si)) return si;
        }
        return defaultValue;
    }

    private static bool TryGetLocalizedText(JsonElement obj, string name, out LocalizedText? text)
    {
        text = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        return TryBuildLocalizedText(value, out text);
    }


    private static bool TryGetLocalizedTextIdOnly(JsonElement obj, string name, out LocalizedText? text)
    {
        text = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }
        return TryBuildLocalizedTextIdOnly(value, out text);
    }

    private static bool TryBuildLocalizedTextIdOnly(JsonElement value, out LocalizedText? text)
    {
        text = null;
        try
        {
            if (TryReadLocalizedTextId(value, out uint id))
            {
                text = CreateRootedLocalizedText(id);
                return text != null;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not build ID-only LocalizedText from json value '{value}': {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    private static bool TryBuildLocalizedText(JsonElement value, out LocalizedText? text)
    {
        text = null;
        try
        {
            if (TryReadLocalizedTextId(value, out uint id))
            {
                text = CreateRootedLocalizedText(id);
                return text != null;
            }

            if (TryReadLocalizedTextRawString(value, out string rawText))
            {
                text = CreateRootedLocalizedText(rawText);
                return text != null;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not build LocalizedText from json value '{value}': {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static bool TryReadLocalizedTextId(JsonElement value, out uint id)
    {
        id = 0u;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out id))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = value.GetString() ?? string.Empty;
            return uint.TryParse(raw.Trim(), out id);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string idName in new[] { "Id", "ID", "id", "PersistentId", "PersistentID", "persistentID", "TextID", "TextId", "textId" })
            {
                if (value.TryGetProperty(idName, out JsonElement idValue) && TryReadLocalizedTextId(idValue, out id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadLocalizedTextRawString(JsonElement value, out string text)
    {
        text = string.Empty;
        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = value.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(raw) && !uint.TryParse(raw.Trim(), out _))
            {
                text = raw;
                return true;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string textName in new[] { "UntranslatedText", "Text", "text", "Value", "value" })
            {
                if (value.TryGetProperty(textName, out JsonElement textValue) && TryReadLocalizedTextRawString(textValue, out text))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static LocalizedText? CreateRootedLocalizedText(uint id)
    {
        try
        {
            // Use GTFO's generated implicit converter instead of new LocalizedText(uint). This path mirrors
            // native datablock assignments and is more stable for IL2CPP wrapper lifetime.
            LocalizedText text = id;
            LocalizedTextRoots.Add(text);
            return text;
        }
        catch (Exception implicitEx)
        {
            try
            {
                LocalizedText text = new LocalizedText();
                text.Id = id;
                LocalizedTextRoots.Add(text);
                LogThrottled($"LocalizedText implicit conversion failed for id={id}; used default constructor + Id assignment. Original={implicitEx.GetType().Name}: {implicitEx.Message}");
                return text;
            }
            catch (Exception fallbackEx)
            {
                LogThrottled($"Could not create LocalizedText id={id}: {fallbackEx.GetType().Name}: {fallbackEx.Message}");
                return null;
            }
        }
    }

    private static LocalizedText? CreateRootedLocalizedText(string untranslatedText)
    {
        try
        {
            LocalizedText text = new LocalizedText(untranslatedText);
            LocalizedTextRoots.Add(text);
            return text;
        }
        catch (Exception ex)
        {
            LogThrottled($"Could not create untranslated LocalizedText '{untranslatedText}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }



    internal static void LogThrottled(string message)
    {
        float now = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - _lastLogTime < 0.25f) return;
        if (LastLogTimesByMessage.TryGetValue(message, out float last) && now - last < 30.0f) return;
        _lastLogTime = now;
        LastLogTimesByMessage[message] = now;
        Log?.LogWarning(message);
    }

    internal static bool IsTransientLookupFailure(string failure)
    {
        if (string.IsNullOrWhiteSpace(failure)) return false;
        string text = failure.ToLowerInvariant();
        return text.Contains("not ready")
            || text.Contains("retrying")
            || text.Contains("out-of-bounds spam")
            || text.Contains("dimensions are not ready")
            || text.Contains("zones are not ready")
            || text.Contains("currentfloor is not ready");
    }
}




// 统一读取 ScanPosOverride / ExtraChainedPuzzleCustomization 的运行时索引。
// 规则：ScanTriggers[Index] = PuzzleOverrideManager 的 PuzzleOverrideIndex；
//       BigPickup InteractTriggers[Index] = PuzzleReqItemManager 的 BigPickup Item Index（1 起始）。
