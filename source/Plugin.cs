using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

internal static class PluginInfo
{
    public const string GUID = "com.gtfo.coordinatetriggerevents";
    public const string NAME = "CoordinateTriggerEvents";
    public const string VERSION = "1.1.0";
}

[BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
[BepInDependency("dev.gtfomodding.gtfo-api", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("MTFO.Extension.PartialBlocks", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    private Harmony? _harmony;

    public override void Load()
    {
        Runtime.Log = Log;
        ConfigManager.LoadOrCreate(Log, true);
        EventAPI.OnExpeditionStarted += Runtime.OnExpeditionStarted;

        _harmony = new Harmony(PluginInfo.GUID);
        SafePatchAll(_harmony, Log);

        Runtime.LogVerbose($"{PluginInfo.NAME} {PluginInfo.VERSION} loaded. Config roots={ConfigManager.ConfigPathSummary}");
    }
    private static void SafePatchAll(Harmony harmony, ManualLogSource log)
    {
        int patched = 0;
        int skipped = 0;
        try
        {
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (!type.GetCustomAttributes(typeof(HarmonyPatch), true).Any())
                {
                    continue;
                }

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    patched++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    log.LogWarning($"Optional Harmony patch skipped: {type.FullName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError($"SafePatchAll failed unexpectedly: {ex}");
        }

        Runtime.LogVerbose($"Harmony patches applied. PatchedClasses={patched}, SkippedClasses={skipped}");
    }

}

internal sealed class ConfigDocument
{
    public string FilePath = string.Empty;
    public bool Enabled = true;
    public List<JsonElement> MainLevelLayoutIDs = new();
    public DebugOptions Debug = new();
    public List<PositionTriggerRule> PositionTriggers = new();
    public List<ScanTriggerRule> ScanTriggers = new();
    public List<InteractTriggerRule> InteractTriggers = new();
}

internal sealed class ScanTriggerRule
{
    // ID 是触发器唯一标识，用于日志输出、运行时查找和 SetTriggerState/SetTriggerEnabled 控制。
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
    // ID 是触发器唯一标识，用于日志输出、运行时查找和 SetTriggerState/SetTriggerEnabled 控制。
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
    // 大物品玩家行为组：拾取 -> 放下 算一组完整行为，可在累计 N 组后执行额外事件组。
    public bool UsePickupDropCycleEvents { get; set; } = false;
    public int PickupDropCycleCount { get; set; } = 3;
    public List<JsonElement> PickupDropCycleEvents { get; set; } = new();
    public List<JsonElement> Events { get; set; } = new();
    public List<JsonElement> WardenEvents { get; set; } = new();
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
    // ID 是触发器唯一标识，用于日志输出、运行时查找和 SetTriggerState/SetTriggerEnabled 控制。
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

internal static class ConfigManager
{
    internal const string ConfigFolderName = "CoordinateTriggerEvents";
    internal const string TemplateChineseFileName = "Template_CN.json";
    internal const string TemplateEnglishFileName = "Template_EN.json";

    internal static readonly List<ConfigDocument> Configs = new();
    private static readonly object LockObject = new();
    private static string _pluginDir = string.Empty;
    private static readonly Dictionary<string, DateTime> LastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileSystemWatcher> ConfigWatchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ReloadDebounceDelay = TimeSpan.FromMilliseconds(350);
    private static bool _reloadQueued;
    private static DateTime _reloadNotBeforeUtc = DateTime.MinValue;
    private static string _reloadReason = string.Empty;

    internal static string ConfigPathSummary
    {
        get
        {
            lock (LockObject)
            {
                return Configs.Count == 0 ? "<none>" : string.Join(" | ", Configs.Select(c => c.FilePath));
            }
        }
    }

    internal static void LoadOrCreate(ManualLogSource? log, bool force)
    {
        lock (LockObject)
        {
            string pluginPath = Paths.PluginPath;
            _pluginDir = GetPluginDirectory();

            Directory.CreateDirectory(pluginPath);
            CleanupLegacyPluginLocalTemplates(log);
            EnsureTemplateConfigs(log);

            List<string> roots = GetConfigSearchRoots(log)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            EnsureConfigWatchers(roots, log);

            List<string> files = roots
                .SelectMany(root => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
                .Where(p => IsCoordinateTriggerConfigPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool snapshotChanged = RefreshFileSnapshot(files);
            bool changed = force || snapshotChanged;
            if (!changed)
            {
                return;
            }

            Configs.Clear();
            foreach (string file in files)
            {
                try
                {
                    ConfigDocument? doc = ParseConfig(file);
                    if (doc != null)
                    {
                        Configs.Add(doc);
                        Runtime.LogVerbose($"Loaded config: {file} | positionTriggers={doc.PositionTriggers.Count}, scanTriggers={doc.ScanTriggers.Count}, interactTriggers={doc.InteractTriggers.Count}");
                        LogTriggerEnabledStates(log, doc);
                    }
                }
                catch (Exception ex)
                {
                    log?.LogError($"Failed to load config '{file}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            Runtime.ClearConfigurationResolutionCaches();
            Runtime.MarkActiveTriggerCacheDirty();
        }
    }

    private static void LogTriggerEnabledStates(ManualLogSource? log, ConfigDocument doc)
    {
        foreach (PositionTriggerRule trigger in doc.PositionTriggers)
        {
            Runtime.LogVerbose($"Loaded position trigger '{trigger.ID}' Enabled={trigger.Enabled}{(trigger.Enabled ? string.Empty : " (skipped until enabled)")}");
        }
        foreach (ScanTriggerRule trigger in doc.ScanTriggers)
        {
            Runtime.LogVerbose($"Loaded scan trigger '{trigger.ID}' Enabled={trigger.Enabled}{(trigger.Enabled ? string.Empty : " (skipped until enabled)")}");
        }
        foreach (InteractTriggerRule trigger in doc.InteractTriggers)
        {
            Runtime.LogVerbose($"Loaded interact trigger '{trigger.ID}' Enabled={trigger.Enabled}{(trigger.Enabled ? string.Empty : " (skipped until enabled)")}");
        }
    }

    internal static void ProcessQueuedReload(ManualLogSource? log)
    {
        string reason;
        lock (LockObject)
        {
            if (!_reloadQueued || DateTime.UtcNow < _reloadNotBeforeUtc)
            {
                return;
            }

            _reloadQueued = false;
            reason = _reloadReason;
            _reloadReason = string.Empty;
        }

        Runtime.LogVerbose($"Config file save detected; reloading CTE configs. Reason={reason}");
        LoadOrCreate(log, true);
    }

    private static void QueueReloadFromWatcher(string reason)
    {
        lock (LockObject)
        {
            _reloadQueued = true;
            _reloadNotBeforeUtc = DateTime.UtcNow + ReloadDebounceDelay;
            _reloadReason = reason;
        }
    }

    private static bool RefreshFileSnapshot(List<string> files)
    {
        bool changed = files.Count != LastWriteTimes.Count;
        HashSet<string> current = new(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            DateTime time = SafeGetLastWriteTimeUtc(file);
            if (!LastWriteTimes.TryGetValue(file, out DateTime old) || old != time)
            {
                changed = true;
                LastWriteTimes[file] = time;
            }
        }

        foreach (string known in LastWriteTimes.Keys.ToList())
        {
            if (!current.Contains(known))
            {
                changed = true;
                LastWriteTimes.Remove(known);
            }
        }

        return changed;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string file)
    {
        try
        {
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void EnsureConfigWatchers(List<string> customRoots, ManualLogSource? log)
    {
        HashSet<string> desiredFolders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in customRoots)
        {
            string folder = Path.Combine(root, ConfigFolderName);
            if (!Directory.Exists(folder))
            {
                continue;
            }
            desiredFolders.Add(Path.GetFullPath(folder));
        }

        foreach (string oldFolder in ConfigWatchers.Keys.ToList())
        {
            if (desiredFolders.Contains(oldFolder))
            {
                continue;
            }

            try
            {
                ConfigWatchers[oldFolder].EnableRaisingEvents = false;
                ConfigWatchers[oldFolder].Dispose();
            }
            catch
            {
                // ignored
            }
            ConfigWatchers.Remove(oldFolder);
            Runtime.LogVerbose($"Stopped CTE config watcher: {oldFolder}");
        }

        foreach (string folder in desiredFolders)
        {
            if (ConfigWatchers.ContainsKey(folder))
            {
                continue;
            }

            try
            {
                FileSystemWatcher watcher = new(folder, "*.json")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };

                watcher.Changed += OnConfigFileChanged;
                watcher.Created += OnConfigFileChanged;
                watcher.Deleted += OnConfigFileChanged;
                watcher.Renamed += OnConfigFileRenamed;
                watcher.Error += OnConfigWatcherError;
                watcher.EnableRaisingEvents = true;
                ConfigWatchers[folder] = watcher;
                Runtime.LogVerbose($"Started CTE config watcher: {folder}");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Failed to start CTE config watcher for '{folder}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsCoordinateTriggerConfigPath(e.FullPath))
        {
            return;
        }

        QueueReloadFromWatcher($"{e.ChangeType}: {e.FullPath}");
    }

    private static void OnConfigFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsCoordinateTriggerConfigPath(e.FullPath) && !IsCoordinateTriggerConfigPath(e.OldFullPath))
        {
            return;
        }

        QueueReloadFromWatcher($"Renamed: {e.OldFullPath} -> {e.FullPath}");
    }

    private static void OnConfigWatcherError(object sender, ErrorEventArgs e)
    {
        QueueReloadFromWatcher($"WatcherError: {e.GetException().GetType().Name}");
    }

    internal static bool ShouldDumpRuntimeIndexes()
    {
        lock (LockObject)
        {
            return Configs.Any(c => c.Enabled && c.Debug.Enabled && c.Debug.DumpRuntimeIndexes);
        }
    }
    private static void EnsureTemplateConfigs(ManualLogSource? log)
    {
        try
        {
            // Match EOS / MTFO style strictly: generate templates only under the active
            // custom rundown datablock Custom folder, e.g.
            // BepInEx/plugins/YOUR_RUNDOWN/Custom/CoordinateTriggerEvents.
            // Do not generate plugin-local fallback templates, because that creates
            // duplicate config files under BepInEx/plugins/CoordinateTriggerEvents.
            if (!TryGetMtfoCustomPath(out string customPath) || string.IsNullOrWhiteSpace(customPath))
            {
                log?.LogWarning("MTFO CustomPath is not available; CTE template configs were not generated. Load a custom rundown through MTFO first.");
                return;
            }

            string folder = Path.Combine(customPath, ConfigFolderName);
            Directory.CreateDirectory(folder);

            WriteTemplateIfMissing(Path.Combine(folder, TemplateChineseFileName), CreateChineseTemplateJson(), log);
            WriteTemplateIfMissing(Path.Combine(folder, TemplateEnglishFileName), CreateEnglishTemplateJson(), log);
        }
        catch (Exception ex)
        {
            log?.LogWarning($"Failed to ensure CTE template configs: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<string> GetConfigSearchRoots(ManualLogSource? log)
    {
        if (TryGetMtfoCustomPath(out string customPath) && !string.IsNullOrWhiteSpace(customPath))
        {
            yield return customPath;
        }
        else
        {
            log?.LogWarning("MTFO CustomPath is not available; CTE will not read plugin-local Custom fallback configs to avoid duplicate configuration loading.");
        }
    }

    private static void CleanupLegacyPluginLocalTemplates(ManualLogSource? log)
    {
        try
        {
            string legacyFolder = Path.Combine(_pluginDir, "Custom", ConfigFolderName);
            if (!Directory.Exists(legacyFolder))
            {
                return;
            }

            foreach (string templateName in new[] { TemplateChineseFileName, TemplateEnglishFileName })
            {
                string file = Path.Combine(legacyFolder, templateName);
                if (File.Exists(file))
                {
                    File.Delete(file);
                    Runtime.LogVerbose($"Deleted legacy plugin-local template config: {file}");
                }
            }

            if (!Directory.EnumerateFileSystemEntries(legacyFolder).Any())
            {
                Directory.Delete(legacyFolder);
                string customFolder = Path.GetDirectoryName(legacyFolder)!;
                if (Directory.Exists(customFolder) && !Directory.EnumerateFileSystemEntries(customFolder).Any())
                {
                    Directory.Delete(customFolder);
                }
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning($"Failed to clean legacy plugin-local CTE templates: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryGetMtfoCustomPath(out string customPath)
    {
        customPath = string.Empty;
        try
        {
            if (!IL2CPPChainloader.Instance.Plugins.TryGetValue("com.dak.MTFO", out var info))
            {
                return false;
            }

            Assembly? assembly = info?.Instance?.GetType()?.Assembly;
            if (assembly == null)
            {
                return false;
            }

            Type? configManagerType = assembly.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
            if (configManagerType == null)
            {
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            FieldInfo? customPathField = configManagerType.GetField("CustomPath", flags);
            FieldInfo? hasCustomField = configManagerType.GetField("HasCustomContent", flags);

            if (hasCustomField != null && hasCustomField.GetValue(null) is bool hasCustom && !hasCustom)
            {
                return false;
            }

            if (customPathField?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
            {
                customPath = value;
                return true;
            }
        }
        catch
        {
            // ignored; caller will use fallback path
        }

        return false;
    }

    private static void WriteTemplateIfMissing(string path, string content, ManualLogSource? log)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Runtime.LogVerbose($"Created template config: {path}");
    }


    private static bool IsCoordinateTriggerConfigPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains($"/Custom/{ConfigFolderName}/", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPluginDirectory()
    {
        try
        {
            string? location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(location))
            {
                string? dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // ignored
        }

        return Path.Combine(Paths.PluginPath, PluginInfo.NAME);
    }

    private static ConfigDocument? ParseConfig(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        JsonElement root = document.RootElement;
        ConfigDocument result = new ConfigDocument { FilePath = file };

        if (root.ValueKind == JsonValueKind.Array)
        {
            result.MainLevelLayoutIDs = new List<JsonElement>();
            result.PositionTriggers = ReadTriggerArray(root);
            ValidateUniqueTriggerIDs(result);
            return result;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        result.Enabled = GetBool(root, "Enabled", true);
        if (root.TryGetProperty("MainLevelLayoutIDs", out JsonElement layouts))
        {
            result.MainLevelLayoutIDs = ReadSelectorList(layouts);
        }

        result.Debug = ReadDebugOptions(root);

        if (root.TryGetProperty("PositionTriggers", out JsonElement triggers) && triggers.ValueKind == JsonValueKind.Array)
        {
            result.PositionTriggers = ReadTriggerArray(triggers);
        }
        else if (root.TryGetProperty("Triggers", out JsonElement triggers2) && triggers2.ValueKind == JsonValueKind.Array)
        {
            result.PositionTriggers = ReadTriggerArray(triggers2);
        }

        if (root.TryGetProperty("ScanTriggers", out JsonElement scanTriggers) && scanTriggers.ValueKind == JsonValueKind.Array)
        {
            result.ScanTriggers = ReadScanTriggerArray(scanTriggers);
        }

        if (root.TryGetProperty("InteractTriggers", out JsonElement interactTriggers) && interactTriggers.ValueKind == JsonValueKind.Array)
        {
            result.InteractTriggers = ReadInteractTriggerArray(interactTriggers);
        }
        else if (root.TryGetProperty("InteractionTriggers", out JsonElement interactionTriggers) && interactionTriggers.ValueKind == JsonValueKind.Array)
        {
            result.InteractTriggers = ReadInteractTriggerArray(interactionTriggers);
        }
        else if (root.TryGetProperty("ObjectTriggers", out JsonElement objectTriggers) && objectTriggers.ValueKind == JsonValueKind.Array)
        {
            result.InteractTriggers = ReadInteractTriggerArray(objectTriggers);
        }

        ValidateUniqueTriggerIDs(result);

        return result;
    }

    private static void ValidateUniqueTriggerIDs(ConfigDocument doc)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Check(string category, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!seen.Add(id))
            {
                Runtime.Log?.LogError($"CTE config error: duplicate trigger ID '{id}'. Trigger IDs must be globally unique within loaded configs. Category={category}, File={doc.FilePath}");
            }
        }

        foreach (PositionTriggerRule trigger in doc.PositionTriggers) Check("Position", trigger.ID);
        foreach (ScanTriggerRule trigger in doc.ScanTriggers) Check("Scan", trigger.ID);
        foreach (InteractTriggerRule trigger in doc.InteractTriggers) Check("Interact", trigger.ID);
    }

    private const float ContinuousTriggerMinimumCooldown = 1.0f;

    private static bool TryApplyCooldownPolicy(JsonElement element, string category, string triggerId, string triggerMode, bool isContinuous, out float cooldown)
    {
        cooldown = GetFloat(element, "Cooldown", 0.0f);
        if (!isContinuous)
        {
            if (cooldown < 0f) cooldown = 0f;
            return true;
        }

        if (!HasProperty(element, "Cooldown"))
        {
            Runtime.Log?.LogError($"CTE config error: continuous {category} trigger '{triggerId}' uses TriggerMode='{triggerMode}' but is missing required Cooldown. Continuous triggers require Cooldown >= {ContinuousTriggerMinimumCooldown:0.0}. Trigger skipped.");
            return false;
        }

        if (cooldown < ContinuousTriggerMinimumCooldown)
        {
            Runtime.Log?.LogWarning($"CTE config warning: continuous {category} trigger '{triggerId}' uses TriggerMode='{triggerMode}' with Cooldown={cooldown:0.###}. Clamped to {ContinuousTriggerMinimumCooldown:0.0} to match AWO StartEventLoop safety semantics.");
            cooldown = ContinuousTriggerMinimumCooldown;
        }

        return true;
    }

    private static bool HasProperty(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out _)) return true;
        }
        return false;
    }

    private static bool IsContinuousPositionTriggerMode(string triggerMode)
    {
        string mode = Runtime.NormalizePositionTriggerMode(triggerMode);
        return mode == "anyplayerinside" || mode == "allplayersinside";
    }

    private static bool IsContinuousScanTriggerMode(string triggerMode)
    {
        string mode = Runtime.NormalizeTriggerMode(triggerMode);
        return mode == "onallplayersinsidescan" || mode == "onallplayersexitedscan";
    }

    private static bool IsContinuousInteractTriggerMode(string targetType, string triggerMode)
    {
        string target = Runtime.NormalizeTargetType(targetType);
        string mode = Runtime.NormalizeInteractionTriggerMode(triggerMode);
        return (target == "bigpickup" && (mode == "onbigpickupheld" || mode == "onbigpickupplaced"))
            || (target == "terminal" && (mode == "onterminalusing" || mode == "onterminalexited"));
    }

    private static List<InteractTriggerRule> ReadInteractTriggerArray(JsonElement array)
    {
        List<InteractTriggerRule> list = new();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            InteractTriggerRule rule = new InteractTriggerRule
            {
                ID = GetString(element, "ID", GetString(element, "Id", GetString(element, "id", string.Empty))),
                Enabled = GetBool(element, "Enabled", true),
                TargetType = GetString(element, "TargetType", GetString(element, "Target", GetString(element, "ObjectType", "Any"))),
                TriggerMode = GetString(element, "TriggerMode", GetString(element, "Trigger", string.Empty)),
                Cooldown = GetFloat(element, "Cooldown", 0.0f),
                RequireInExpedition = GetBool(element, "RequireInExpedition", true),
                Index = GetInt(element, "Index", -1),
                InstanceID = GetInt(element, "InstanceID", GetInt(element, "ObjectInstanceID", -1)),
                SyncID = GetInt(element, "SyncID", GetInt(element, "SyncId", -1)),
                SerialNumber = GetInt(element, "SerialNumber", GetInt(element, "Serial", -1)),
                ItemKey = GetString(element, "ItemKey", GetString(element, "Key", string.Empty)),
                PublicName = GetString(element, "PublicName", GetString(element, "NameContains", string.Empty)),
                TerminalSerial = GetString(element, "TSL", GetString(element, "TerminalTSL", GetString(element, "TerminalTsl", GetString(element, "TerminalSelector", GetString(element, "TerminalSerial", GetString(element, "TerminalSerialNumber", GetString(element, "TerminalSerialText", GetString(element, "SerialText", GetString(element, "SerialLookup", GetString(element, "TerminalSerialLookup", string.Empty)))))))))),
                WorldEventObjectFilter = GetString(element, "WorldEventObjectFilter", GetString(element, "Filter", GetString(element, "ObjectFilter", string.Empty))),
                DataBlockID = GetUInt(element, "DataBlockID", GetUInt(element, "ItemDataBlockID", GetUInt(element, "ItemID", 0u))),
                ItemID = GetUInt(element, "ItemID", GetUInt(element, "PickupID", 0u)),
                InternalName = GetString(element, "InternalName", GetString(element, "PrefabName", string.Empty)),
                Radius = GetFloat(element, "Radius", 2.0f),
                UsePickupDropCycleEvents = GetBool(element, "UsePickupDropCycleEvents", GetBool(element, "UseBehaviorCycleEvents", GetBool(element, "UsePickupDropGroupEvents", false))),
                PickupDropCycleCount = Math.Max(1, GetInt(element, "PickupDropCycleCount", GetInt(element, "BehaviorCycleCount", GetInt(element, "ActionGroupCount", 3))))
            };

            if (element.TryGetProperty("Position", out JsonElement pos) && pos.ValueKind == JsonValueKind.Object)
            {
                rule.Position = new PositionData
                {
                    x = GetFloat(pos, "x", GetFloat(pos, "X", 0f)),
                    y = GetFloat(pos, "y", GetFloat(pos, "Y", 0f)),
                    z = GetFloat(pos, "z", GetFloat(pos, "Z", 0f))
                };
            }

            if (element.TryGetProperty("Events", out JsonElement events) && events.ValueKind == JsonValueKind.Array)
            {
                rule.Events = events.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            if (element.TryGetProperty("WardenEvents", out JsonElement wardenEvents) && wardenEvents.ValueKind == JsonValueKind.Array)
            {
                rule.WardenEvents = wardenEvents.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            rule.PickupDropCycleEvents = ReadFirstArrayProperty(element,
                "PickupDropCycleEvents", "PickupDropGroupEvents", "BehaviorCycleEvents", "ActionGroupEvents", "CycleEvents");

            if (string.IsNullOrWhiteSpace(rule.ID))
            {
                Runtime.Log?.LogError($"CTE config error: trigger is missing required ID. File may be skipped partly. Category={rule.GetType().Name}");
                continue;
            }

            if (!TryApplyCooldownPolicy(element, "Interact", rule.ID, rule.TriggerMode, IsContinuousInteractTriggerMode(rule.TargetType, rule.TriggerMode), out float normalizedInteractCooldown))
            {
                continue;
            }
            rule.Cooldown = normalizedInteractCooldown;

            list.Add(rule);
        }
        return list;
    }

    private static List<ScanTriggerRule> ReadScanTriggerArray(JsonElement array)
    {
        List<ScanTriggerRule> list = new();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ScanTriggerRule rule = new ScanTriggerRule
            {
                ID = GetString(element, "ID", GetString(element, "Id", GetString(element, "id", string.Empty))),
                Enabled = GetBool(element, "Enabled", true),
                PuzzleOverrideIndex = GetInt(element, "Index", GetInt(element, "PuzzleOverrideIndex", GetInt(element, "PuzzleIndex", -1))),
                TriggerMode = GetString(element, "TriggerMode", GetString(element, "Trigger", "OnScanActivated")),
                UsePlayerCountEvents = GetBool(element, "UsePlayerCountEvents", GetBool(element, "EnablePlayerCountEvents", false)),
                Cooldown = GetFloat(element, "Cooldown", 0.0f),
                RequireInExpedition = GetBool(element, "RequireInExpedition", true),
                RequireAlivePlayers = GetBool(element, "RequireAlivePlayers", true),
                UseTriggerCycleEvents = GetBool(element, "UseTriggerCycleEvents", GetBool(element, "UseScanCycleEvents", GetBool(element, "UseTriggerGroupEvents", false))),
                TriggerCycleCount = Math.Max(1, GetInt(element, "TriggerCycleCount", GetInt(element, "ScanCycleCount", GetInt(element, "TriggerGroupCount", 3))))
            };

            if (element.TryGetProperty("Events", out JsonElement events) && events.ValueKind == JsonValueKind.Array)
            {
                rule.Events = events.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            if (element.TryGetProperty("WardenEvents", out JsonElement wardenEvents) && wardenEvents.ValueKind == JsonValueKind.Array)
            {
                rule.WardenEvents = wardenEvents.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            rule.OnePlayerEvents = ReadFirstArrayProperty(element,
                "OnePlayerEvents", "EventsOnOnePlayer", "OnePlayerWardenEvents", "WardenEventsOnOnePlayer", "Player1Events", "Events1P", "Events_1P");
            rule.TwoPlayerEvents = ReadFirstArrayProperty(element,
                "TwoPlayerEvents", "EventsOnTwoPlayers", "TwoPlayerWardenEvents", "WardenEventsOnTwoPlayers", "Player2Events", "Events2P", "Events_2P");
            rule.ThreePlayerEvents = ReadFirstArrayProperty(element,
                "ThreePlayerEvents", "EventsOnThreePlayers", "ThreePlayerWardenEvents", "WardenEventsOnThreePlayers", "Player3Events", "Events3P", "Events_3P");
            rule.FourPlayerEvents = ReadFirstArrayProperty(element,
                "FourPlayerEvents", "EventsOnFourPlayers", "FourPlayerWardenEvents", "WardenEventsOnFourPlayers", "Player4Events", "Events4P", "Events_4P");
            ReadPlayerCountEventsObject(element, rule);
            rule.TriggerCycleEvents = ReadFirstArrayProperty(element,
                "TriggerCycleEvents", "ScanCycleEvents", "TriggerGroupEvents", "CycleEvents");

            if (string.IsNullOrWhiteSpace(rule.ID))
            {
                Runtime.Log?.LogError($"CTE config error: trigger is missing required ID. File may be skipped partly. Category={rule.GetType().Name}");
                continue;
            }

            if (!TryApplyCooldownPolicy(element, "Scan", rule.ID, rule.TriggerMode, IsContinuousScanTriggerMode(rule.TriggerMode), out float normalizedScanCooldown))
            {
                continue;
            }
            rule.Cooldown = normalizedScanCooldown;

            list.Add(rule);
        }
        return list;
    }

    private static List<PositionTriggerRule> ReadTriggerArray(JsonElement array)
    {
        List<PositionTriggerRule> list = new();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            PositionTriggerRule rule = new PositionTriggerRule
            {
                ID = GetString(element, "ID", GetString(element, "Id", GetString(element, "id", string.Empty))),
                Enabled = GetBool(element, "Enabled", true),
                TriggerAreaMode = GetString(element, "TriggerAreaMode", GetString(element, "AreaMode", GetString(element, "Mode", "Radius"))),
                Radius = GetFloat(element, "Radius", 3.0f),
                LocalIndex = GetInt(element, "LocalIndex", GetInt(element, "ZoneLocalIndex", -1)),
                Count = GetInt(element, "Count", GetInt(element, "AreaIndex", -1)),
                Layer = GetString(element, "Layer", string.Empty),
                DimensionIndex = GetInt(element, "DimensionIndex", -1),
                TriggerMode = GetString(element, "TriggerMode", "AnyPlayerEnter"),
                UsePlayerCountEvents = GetBool(element, "UsePlayerCountEvents", GetBool(element, "EnablePlayerCountEvents", GetBool(element, "UsePlayerCountEventGroups", GetBool(element, "EnablePlayerCountEventGroups", false)))),
                UseTriggerCycleEvents = GetBool(element, "UseTriggerCycleEvents", GetBool(element, "UsePositionCycleEvents", GetBool(element, "UseTriggerGroupEvents", false))),
                TriggerCycleCount = Math.Max(1, GetInt(element, "TriggerCycleCount", GetInt(element, "PositionCycleCount", GetInt(element, "TriggerGroupCount", 3)))),
                Cooldown = GetFloat(element, "Cooldown", 0.0f),
                RequireInExpedition = GetBool(element, "RequireInExpedition", true),
                RequireAlivePlayers = GetBool(element, "RequireAlivePlayers", true),
                IncludeBots = GetBool(element, "IncludeBots", true),
                DebugVisible = GetBool(element, "DebugVisible", true),
                DebugColor = GetString(element, "DebugColor", string.Empty)
            };

            if (element.TryGetProperty("Position", out JsonElement pos) && pos.ValueKind == JsonValueKind.Object)
            {
                rule.Position = new PositionData
                {
                    x = GetFloat(pos, "x", GetFloat(pos, "X", 0f)),
                    y = GetFloat(pos, "y", GetFloat(pos, "Y", 0f)),
                    z = GetFloat(pos, "z", GetFloat(pos, "Z", 0f))
                };
            }
            else
            {
                bool hasFlatPosition =
                    element.TryGetProperty("x", out _) || element.TryGetProperty("X", out _)
                    || element.TryGetProperty("y", out _) || element.TryGetProperty("Y", out _)
                    || element.TryGetProperty("z", out _) || element.TryGetProperty("Z", out _);

                if (hasFlatPosition)
                {
                    rule.Position = new PositionData
                    {
                        x = GetFloat(element, "x", GetFloat(element, "X", 0f)),
                        y = GetFloat(element, "y", GetFloat(element, "Y", 0f)),
                        z = GetFloat(element, "z", GetFloat(element, "Z", 0f))
                    };
                }
            }

            if (element.TryGetProperty("Events", out JsonElement events) && events.ValueKind == JsonValueKind.Array)
            {
                rule.Events = events.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            if (element.TryGetProperty("WardenEvents", out JsonElement wardenEvents) && wardenEvents.ValueKind == JsonValueKind.Array)
            {
                rule.WardenEvents = wardenEvents.EnumerateArray().Select(e => e.Clone()).ToList();
            }

            rule.OnePlayerEvents = ReadFirstArrayProperty(element,
                "OnePlayerEvents", "EventsOnOnePlayer", "OnePlayerWardenEvents", "WardenEventsOnOnePlayer", "Player1Events", "Events1P", "Events_1P");
            rule.TwoPlayerEvents = ReadFirstArrayProperty(element,
                "TwoPlayerEvents", "EventsOnTwoPlayers", "TwoPlayerWardenEvents", "WardenEventsOnTwoPlayers", "Player2Events", "Events2P", "Events_2P");
            rule.ThreePlayerEvents = ReadFirstArrayProperty(element,
                "ThreePlayerEvents", "EventsOnThreePlayers", "ThreePlayerWardenEvents", "WardenEventsOnThreePlayers", "Player3Events", "Events3P", "Events_3P");
            rule.FourPlayerEvents = ReadFirstArrayProperty(element,
                "FourPlayerEvents", "EventsOnFourPlayers", "FourPlayerWardenEvents", "WardenEventsOnFourPlayers", "Player4Events", "Events4P", "Events_4P");
            ReadPlayerCountEventsObject(element, rule);
            rule.TriggerCycleEvents = ReadFirstArrayProperty(element,
                "TriggerCycleEvents", "PositionCycleEvents", "TriggerGroupEvents", "CycleEvents");

            if (string.IsNullOrWhiteSpace(rule.ID))
            {
                Runtime.Log?.LogError($"CTE config error: trigger is missing required ID. File may be skipped partly. Category={rule.GetType().Name}");
                continue;
            }

            if (!TryApplyCooldownPolicy(element, "Position", rule.ID, rule.TriggerMode, IsContinuousPositionTriggerMode(rule.TriggerMode), out float normalizedPositionCooldown))
            {
                continue;
            }
            rule.Cooldown = normalizedPositionCooldown;

            list.Add(rule);
        }
        return list;
    }

    private static List<JsonElement> ReadFirstArrayProperty(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray().Select(e => e.Clone()).ToList();
            }
        }
        return new List<JsonElement>();
    }

    private static void ReadPlayerCountEventsObject(JsonElement obj, PositionTriggerRule rule)
    {
        if (!obj.TryGetProperty("PlayerCountEvents", out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<JsonElement> events = property.Value.EnumerateArray().Select(e => e.Clone()).ToList();
            string key = property.Name.Trim().ToLowerInvariant();
            if (key == "1" || key == "one" || key == "oneplayer") rule.OnePlayerEvents = events;
            else if (key == "2" || key == "two" || key == "twoplayers") rule.TwoPlayerEvents = events;
            else if (key == "3" || key == "three" || key == "threeplayers") rule.ThreePlayerEvents = events;
            else if (key == "4" || key == "four" || key == "fourplayers") rule.FourPlayerEvents = events;
        }
    }

    private static void ReadPlayerCountEventsObject(JsonElement obj, ScanTriggerRule rule)
    {
        if (!obj.TryGetProperty("PlayerCountEvents", out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<JsonElement> events = property.Value.EnumerateArray().Select(e => e.Clone()).ToList();
            string key = property.Name.Trim().ToLowerInvariant();
            if (key == "1" || key == "one" || key == "oneplayer") rule.OnePlayerEvents = events;
            else if (key == "2" || key == "two" || key == "twoplayers") rule.TwoPlayerEvents = events;
            else if (key == "3" || key == "three" || key == "threeplayers") rule.ThreePlayerEvents = events;
            else if (key == "4" || key == "four" || key == "fourplayers") rule.FourPlayerEvents = events;
        }
    }

    private static List<JsonElement> ReadSelectorList(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(e => e.Clone()).ToList();
        }

        if (value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.Object)
        {
            return new List<JsonElement> { value.Clone() };
        }

        return new List<JsonElement>();
    }

    private static DebugOptions ReadDebugOptions(JsonElement root)
    {
        DebugOptions debug = new DebugOptions();

        debug.Enabled = GetBool(root, "EnableDebugScanMarkers", GetBool(root, "DebugScanMarkers", GetBool(root, "DebugEnabled", false)));
        debug.ShowScanMarkers = GetBool(root, "ShowDebugScanMarkers", true);
        debug.ShowNames = GetBool(root, "DebugShowNames", true);
        debug.MarkerColor = GetString(root, "DebugMarkerColor", debug.MarkerColor);
        debug.LabelColor = GetString(root, "DebugLabelColor", debug.LabelColor);
        debug.MarkerAlpha = GetFloat(root, "DebugMarkerAlpha", debug.MarkerAlpha);
        debug.HeightOffset = GetFloat(root, "DebugHeightOffset", debug.HeightOffset);
        debug.MarkerHeight = GetFloat(root, "DebugMarkerHeight", debug.MarkerHeight);
        debug.LabelHeightOffset = GetFloat(root, "DebugLabelHeightOffset", debug.LabelHeightOffset);
        debug.RadiusScale = GetFloat(root, "DebugRadiusScale", debug.RadiusScale);
        debug.MinimumRadius = GetFloat(root, "DebugMinimumRadius", debug.MinimumRadius);
        debug.RefreshInterval = GetFloat(root, "DebugRefreshInterval", debug.RefreshInterval);
        debug.DumpRuntimeIndexes = GetBool(root, "DumpRuntimeIndexes", debug.DumpRuntimeIndexes);

        if (root.TryGetProperty("Debug", out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
        {
            debug.Enabled = GetBool(obj, "Enabled", debug.Enabled);
            debug.ShowScanMarkers = GetBool(obj, "ShowScanMarkers", debug.ShowScanMarkers);
            debug.ShowNames = GetBool(obj, "ShowNames", debug.ShowNames);
            debug.MarkerColor = GetString(obj, "MarkerColor", debug.MarkerColor);
            debug.LabelColor = GetString(obj, "LabelColor", debug.LabelColor);
            debug.MarkerAlpha = GetFloat(obj, "MarkerAlpha", debug.MarkerAlpha);
            debug.HeightOffset = GetFloat(obj, "HeightOffset", debug.HeightOffset);
            debug.MarkerHeight = GetFloat(obj, "MarkerHeight", debug.MarkerHeight);
            debug.LabelHeightOffset = GetFloat(obj, "LabelHeightOffset", debug.LabelHeightOffset);
            debug.RadiusScale = GetFloat(obj, "RadiusScale", debug.RadiusScale);
            debug.MinimumRadius = GetFloat(obj, "MinimumRadius", debug.MinimumRadius);
            debug.RefreshInterval = GetFloat(obj, "RefreshInterval", debug.RefreshInterval);
            debug.DumpRuntimeIndexes = GetBool(obj, "DumpRuntimeIndexes", debug.DumpRuntimeIndexes);
        }

        debug.MarkerAlpha = Mathf.Clamp01(debug.MarkerAlpha);
        debug.RadiusScale = Math.Max(0.01f, debug.RadiusScale);
        debug.MinimumRadius = Math.Max(0.01f, debug.MinimumRadius);
        debug.MarkerHeight = Math.Max(0.001f, debug.MarkerHeight);
        debug.RefreshInterval = Math.Max(0.2f, debug.RefreshInterval);
        return debug;
    }

    private static bool GetBool(JsonElement obj, string name, bool defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b)) return b;
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

    private static string GetString(JsonElement obj, string name, string defaultValue)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? defaultValue;
            return v.ToString();
        }
        return defaultValue;
    }

    private static string CreateChineseTemplateJson()
    {
        return """
// CoordinateTriggerEvents 1.1.0 中文配置模板
// 生成位置：<自定义Rundown数据块文件夹>/Custom/CoordinateTriggerEvents/Template_CN.json
// 模板默认 Enabled=false，不会影响关卡。复制本文件并改名为关卡配置后，把 Enabled 改为 true。
// 插件允许 // 注释和尾随逗号。支持多个 JSON 文件；只会启用 MainLevelLayoutIDs 匹配当前关卡的配置。
{
  "Enabled": false,
  "MainLevelLayoutIDs":0,
  "Debug": {//调试模式，供模组开发者查看触发器的实际位置
    "Enabled": false,
    "ShowScanMarkers": true,
    "ShowNames": true,
    "MarkerColor": "#00BFFF",
    "LabelColor": "#FFFFFF",
    "MarkerAlpha": 0.35,
    "HeightOffset": 0.05,
    "LabelHeightOffset": 1.0
  },
  "PositionTriggers": [
    {
      "ID": "radius_player_count_example", //当前触发器的唯一id
      "TriggerAreaMode": "Radius",
      //Radius：按一个世界坐标点和半径生成触发范围。

      "TriggerMode": "AnyPlayerEnter",
      //AnyPlayerEnter  任意玩家进入触发范围触发事件

      //AnyPlayerInside 触发范围内有任意玩家根据"Cooldown": 1.0,重复触发

      //AllPlayersEnter 所有符合条件的玩家进入范围后触发

      //AllPlayersInside 所有符合条件的玩家根据"Cooldown": 1.0,重复触发

      //AnyPlayerExit 任意玩家从范围内离开范围时触发

      //AllPlayersExit 所有玩家从范围内离开时触发

      "Enabled": true,
      "Position": {
        "x": 0,
        "y": 0,
        "z": 0
      },
      "Radius": 5.0, //触发器大小
      "Cooldown": 1.0, //内置冷却cd
      "RequireAlivePlayers": true, //玩家倒地不计算为人数
      "DebugVisible": true, //开启调试模式
      "Events": [], //事件列表
      "UsePlayerCountEvents": false,
      // true 时只执行 PlayerCountEvents 中对应人数的事件组；false 时执行 Events内的事件组
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      // 每成功触发一次+1Count；累计 TriggerCycleCount 组后额外触发事件
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "whole_zone_localindex_0",
      "TriggerAreaMode": "OverrideBigZone",
      //OverrideBigZone 生成一个覆盖指定ZONE的触发器
      "TriggerMode": "AnyPlayerEnter",
      "Enabled": true,
      "DimensionIndex": 0,
      "Layer": 0,
      "LocalIndex": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "DebugVisible": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "whole_area_count_0",
      "TriggerAreaMode": "OverrideArea",
      //OverrideArea  生成一个覆盖指定Zone Area区域的触发器
      "TriggerMode": "AnyPlayerEnter",
      "Enabled": true,
      "DimensionIndex": 0,
      "Layer":0,
      "LocalIndex": 0,
      "Count": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "DebugVisible": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    }
  ],
  "ScanTriggers": [
    {
      "ID": "scan_1_activated_player_count",
      "TriggerMode": "OnScanActivated",
      //OnScanActivated 玩家激活扫描点时触发事件
      //OnPlayerExitScan 玩家退出扫描点时触发事件
      "Enabled": true,
      // Index 使用 ScanPositionOverride / ECC 在 BepInEx 日志中输出的 PuzzleOverrideIndex，索引从 1 开始。
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_player_exit_event",
      "TriggerMode": "OnPlayerExitScan",
      "Enabled": true,
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_enter_once",
      "TriggerMode": "OnAllPlayersEnterScan",
      "Enabled": true,
      // 全员进入扫描点时触发一次。
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_inside_repeat",
      "TriggerMode": "OnAllPlayersInsideScan",
      "Enabled": true,
      // 全员持续在扫描点内时按 Cooldown 重复触发。
      "Index": 0,
      "Cooldown": 5.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_exit_once",
      "TriggerMode": "OnAllPlayersExitScan",
      "Enabled": true,
      // 全员曾进入扫描点后全部退出时触发一次。
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_exited_repeat",
      "TriggerMode": "OnAllPlayersExitedScan",
      "Enabled": true,
      // 全员曾进入扫描点并全部退出后按 Cooldown 重复触发；初始无人不会触发。
      "Index": 0,
      "Cooldown": 5.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    }
  ],
  "InteractTriggers": [
    {
      "ID": "bigpickup_1_pickup_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupPickup",
      //OnBigPickupPickup 拾取大物品触发事件
      //OnBigPickupDrop 放下大物品触发事件
      "Enabled": true,
      // Index 使用 ScanPositionOverride / ECC 在 BepInEx 日志中输出的 BigPickup Item Index，索引从 1 开始。
      "Index": 0,
      "Cooldown": 1.0,
      "Events": [],
      "UsePickupDropCycleEvents": false,
      "PickupDropCycleCount": 0,
      "PickupDropCycleEvents": []
    },
    {
      "ID": "bigpickup_1_drop_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupDrop",
      "Enabled": true,
      "Index": 0,
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "bigpickup_1_held_repeat_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupHeld",
      "Enabled": true,
      "Index": 0,
      // 玩家拿着该大物品期间按 Cooldown 重复触发，类似 AnyPlayerInside。
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "bigpickup_1_placed_repeat_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupPlaced",
      "Enabled": true,
      "Index": 0,
      // 玩家放下该大物品后，只要大物品保持放置状态，就按 Cooldown 重复触发。
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_use_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUse",
      //OnTerminalUse 使用终端触发事件
      //OnTerminalUnused 退出终端触发事件
      "Enabled": true,
      // TerminalSelector 与 AWO TSL 一致：[TERMINAL_DimensionIndex_LayerIndex_ZoneLocalIndex_TerminalIndexInZone]
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_unused_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUnused",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_using_repeat_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUsing",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      // 玩家正在使用该终端期间按 Cooldown 重复触发，类似 AnyPlayerInside。
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_exited_repeat_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalExited",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      // 必须玩家使用过一次该终端并退出后，才会按 Cooldown 重复触发；初始未使用状态不会触发。
      "Cooldown": 5.0,
      "Events": []
    }
  ]
}
""";
    }

    private static string CreateEnglishTemplateJson()
    {
        return """
// CoordinateTriggerEvents 1.1.0 English configuration template
// Generated path: <Custom Rundown datablock folder>/Custom/CoordinateTriggerEvents/Template_EN.json
// The template defaults to Enabled=false and will not affect levels. Copy this file, rename it as a level config, then set Enabled to true.
// The plugin allows // comments and trailing commas. Multiple JSON files are supported; only configs whose MainLevelLayoutIDs match the current level are enabled.
{
  "Enabled": false,
  "MainLevelLayoutIDs":0,
  "Debug": {//Debug mode, for mod developers to view the actual positions of triggers
    "Enabled": false,
    "ShowScanMarkers": true,
    "ShowNames": true,
    "MarkerColor": "#00BFFF",
    "LabelColor": "#FFFFFF",
    "MarkerAlpha": 0.35,
    "HeightOffset": 0.05,
    "LabelHeightOffset": 1.0
  },
  "PositionTriggers": [
    {
      "ID": "radius_player_count_example", //Unique ID of the current trigger
      "TriggerAreaMode": "Radius",
      //Radius: generates a trigger area from a world position and radius.

      "TriggerMode": "AnyPlayerEnter",
      //AnyPlayerEnter  Triggers the event when any player enters the trigger area

      //AnyPlayerInside Repeatedly triggers while any player is inside the trigger area according to "Cooldown": 1.0,

      //AllPlayersEnter Triggers after all eligible players enter the area

      //AllPlayersInside Repeatedly triggers while all eligible players are inside according to "Cooldown": 1.0,

      //AnyPlayerExit Triggers when any player leaves the area from inside

      //AllPlayersExit Triggers when all players leave the area from inside

      "Enabled": true,
      "Position": {
        "x": 0,
        "y": 0,
        "z": 0
      },
      "Radius": 5.0, //Trigger size
      "Cooldown": 1.0, //Built-in cooldown
      "RequireAlivePlayers": true, //Downed players are not counted as player count
      "DebugVisible": true, //Enable debug mode
      "Events": [], //Event list
      "UsePlayerCountEvents": false,
      // When true, only executes the event group matching the player count in PlayerCountEvents; when false, executes the event group inside Events
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      // Adds 1 Count for every successful trigger; after accumulating TriggerCycleCount groups, triggers additional events
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "whole_zone_localindex_0",
      "TriggerAreaMode": "OverrideBigZone",
      //OverrideBigZone generates a trigger that covers the specified ZONE
      "TriggerMode": "AnyPlayerEnter",
      "Enabled": true,
      "DimensionIndex": 0,
      "Layer": 0,
      "LocalIndex": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "DebugVisible": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "whole_area_count_0",
      "TriggerAreaMode": "OverrideArea",
      //OverrideArea generates a trigger that covers the specified Zone Area
      "TriggerMode": "AnyPlayerEnter",
      "Enabled": true,
      "DimensionIndex": 0,
      "Layer":0,
      "LocalIndex": 0,
      "Count": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "DebugVisible": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    }
  ],
  "ScanTriggers": [
    {
      "ID": "scan_1_activated_player_count",
      "TriggerMode": "OnScanActivated",
      //OnScanActivated Triggers the event when players activate the scan
      //OnPlayerExitScan Triggers the event when a player exits the scan
      "Enabled": true,
      // Index uses the PuzzleOverrideIndex printed by ScanPositionOverride / ECC in the BepInEx log. The index starts from 1.
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_player_exit_event",
      "TriggerMode": "OnPlayerExitScan",
      "Enabled": true,
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_enter_once",
      "TriggerMode": "OnAllPlayersEnterScan",
      "Enabled": true,
      // Triggers once when all players enter the scan.
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_inside_repeat",
      "TriggerMode": "OnAllPlayersInsideScan",
      "Enabled": true,
      // Repeatedly triggers by Cooldown while all players continuously stay inside the scan.
      "Index": 0,
      "Cooldown": 5.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_exit_once",
      "TriggerMode": "OnAllPlayersExitScan",
      "Enabled": true,
      // Triggers once after all players have entered the scan and then all leave it.
      "Index": 0,
      "Cooldown": 1.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    },
    {
      "ID": "scan_1_all_players_exited_repeat",
      "TriggerMode": "OnAllPlayersExitedScan",
      "Enabled": true,
      // Repeatedly triggers by Cooldown after all players have entered the scan and then all leave it; it does not trigger when the scan starts empty.
      "Index": 0,
      "Cooldown": 5.0,
      "RequireAlivePlayers": true,
      "Events": [],
      "UsePlayerCountEvents": false,
      "PlayerCountEvents": {
        "1": [],
        "2": [],
        "3": [],
        "4": []
      },
      "UseTriggerCycleEvents": false,
      "TriggerCycleCount": 0,
      "TriggerCycleEvents": []
    }
  ],
  "InteractTriggers": [
    {
      "ID": "bigpickup_1_pickup_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupPickup",
      //OnBigPickupPickup Triggers the event when a big pickup is picked up
      //OnBigPickupDrop Triggers the event when a big pickup is dropped
      "Enabled": true,
      // Index uses the BigPickup Item Index printed by ScanPositionOverride / ECC in the BepInEx log. The index starts from 1.
      "Index": 0,
      "Cooldown": 1.0,
      "Events": [],
      "UsePickupDropCycleEvents": false,
      "PickupDropCycleCount": 0,
      "PickupDropCycleEvents": []
    },
    {
      "ID": "bigpickup_1_drop_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupDrop",
      "Enabled": true,
      "Index": 0,
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "bigpickup_1_held_repeat_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupHeld",
      "Enabled": true,
      "Index": 0,
      // Repeatedly triggers by Cooldown while the player is carrying this big pickup, similar to AnyPlayerInside.
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "bigpickup_1_placed_repeat_event",
      "TargetType": "BigPickup",
      "TriggerMode": "OnBigPickupPlaced",
      "Enabled": true,
      "Index": 0,
      // After the player drops this big pickup, repeatedly triggers by Cooldown while the big pickup remains placed.
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_use_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUse",
      //OnTerminalUse Triggers the event when using a terminal
      //OnTerminalUnused Triggers the event when leaving a terminal
      "Enabled": true,
      // TerminalSelector is the same as AWO TSL: [TERMINAL_DimensionIndex_LayerIndex_ZoneLocalIndex_TerminalIndexInZone]
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_unused_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUnused",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      "Cooldown": 1.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_using_repeat_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalUsing",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      // Repeatedly triggers by Cooldown while the player is using this terminal, similar to AnyPlayerInside.
      "Cooldown": 5.0,
      "Events": []
    },
    {
      "ID": "terminal_tsl_exited_repeat_event",
      "TargetType": "Terminal",
      "TriggerMode": "OnTerminalExited",
      "Enabled": true,
      "TerminalSelector": "[TERMINAL_0_0_0_0]",
      // The player must have used and then exited this terminal once before this repeatedly triggers by Cooldown; it will not trigger in the initial unused state.
      "Cooldown": 5.0,
      "Events": []
    }
  ]
}
""";
    }



}

internal static class Runtime
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
    private static readonly Dictionary<string, ZoneLookupCacheEntry> ZoneLookupCache = new(StringComparer.OrdinalIgnoreCase);

    // 1.0.6：缓存当前关卡已启用触发器，避免 Runtime.Tick / 持续触发器每 0.2 秒重复遍历所有配置文件并分配 List。
    private static readonly object ActiveTriggerCacheLock = new();
    private static readonly List<(ConfigDocument Config, PositionTriggerRule Trigger)> ActivePositionTriggerCache = new();
    private static readonly List<(ConfigDocument Config, ScanTriggerRule Trigger)> ActiveScanTriggerCache = new();
    private static readonly List<(ConfigDocument Config, InteractTriggerRule Trigger)> ActiveInteractTriggerCache = new();
    private static bool ActiveTriggerCacheDirty = true;
    private static uint ActiveTriggerCacheLayoutId = uint.MaxValue;
    private static string ActiveTriggerCacheLayoutName = string.Empty;

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
            }

            ActiveTriggerCacheLayoutId = layoutId;
            ActiveTriggerCacheLayoutName = layoutName;
            ActiveTriggerCacheDirty = false;
        }
    }

    internal static void OnExpeditionStarted()
    {
        States.Clear();
        LocalizedTextRoots.Clear();
        ZoneLookupCache.Clear();
        LastLogTimesByMessage.Clear();
        PendingConfiguredEvents.Clear();
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

    private static bool ShouldDumpRuntimeBindings()
    {
        // 只有显式启用 Debug.DumpRuntimeIndexes 时才进行运行时索引 dump；
        // 普通 Debug Marker 不再触发任何控制台索引输出。
        return ConfigManager.ShouldDumpRuntimeIndexes();
    }

    private sealed class PendingConfiguredEvent
    {
        public JsonElement EventElement;
        public string OwnerLabel = string.Empty;
        public float DueTime;
    }

    private static readonly Queue<PendingConfiguredEvent> PendingConfiguredEvents = new();
    private const int MaxQueuedConfiguredEventsPerTick = 3;

    private static int QueueConfiguredEventList(IEnumerable<JsonElement> events, string ownerLabel, float delaySeconds = 0.05f)
    {
        int count = 0;
        float due = Time.realtimeSinceStartup + Math.Max(0.0f, delaySeconds);
        foreach (JsonElement eventElement in events)
        {
            PendingConfiguredEvents.Enqueue(new PendingConfiguredEvent
            {
                EventElement = eventElement.Clone(),
                OwnerLabel = ownerLabel,
                DueTime = due
            });
            count++;
        }
        return count;
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
            TryExecuteConfiguredEvent(pending.EventElement, pending.OwnerLabel);
            processed++;
        }
    }

    internal static void FireInteractTrigger(string sourceKind, string eventName, Component? source, PlayerAgent? player, string sourceName, string stateKeySuffix)
    {
        int before = InteractTriggerManager.FiredDispatchCount;
        foreach ((ConfigDocument config, InteractTriggerRule trigger) in GetActiveInteractTriggers())
        {
            FireInteractTrigger(config, trigger, sourceKind, eventName, source, player, sourceName, stateKeySuffix);
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

            int count = ExecuteEventList(trigger.PickupDropCycleEvents, $"BigPickup pickup/drop cycle trigger '{trigger.ID}' cycles={completedCycles}");
            LogVerbose($"BigPickup pickup/drop cycle trigger '{trigger.ID}' fired. Cycles={completedCycles}, Required={required}, Source={sourceName}, ExecutedEvents={count}");
        }
    }

    private static void FireInteractTrigger(ConfigDocument config, InteractTriggerRule trigger, string sourceKind, string eventName, Component? source, PlayerAgent? player, string sourceName, string stateKeySuffix)
    {
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition)
        {
            return;
        }

        if (!InteractTriggerMatches(trigger, sourceKind, eventName, source))
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

        IEnumerable<JsonElement> interactEvents = trigger.Events.Concat(trigger.WardenEvents);
        bool isTerminalEdgeEvent = NormalizeTargetType(sourceKind) == "terminal"
            && (NormalizeInteractionTriggerMode(eventName) == "onterminaluse" || NormalizeInteractionTriggerMode(eventName) == "onterminalunused");

        if (isTerminalEdgeEvent)
        {
            // 1.0.7：触发条件、Cooldown 与事件列表保持不变，只把终端使用/退出后的实际 WardenEvent 拆到后续 Tick 限流执行。
            // 避免 SecDoorTerminal / EOS / AWO / TerminalQueryAPI 同时存在时，在终端 UI 进入/退出帧同步执行整组事件造成卡顿。
            count = QueueConfiguredEventList(interactEvents, $"Interact trigger '{trigger.ID}'", 0.05f);
        }
        else
        {
            foreach (JsonElement eventElement in interactEvents)
            {
                if (TryExecuteConfiguredEvent(eventElement, $"Interact trigger '{trigger.ID}'"))
                {
                    count++;
                }
            }
        }

        InteractTriggerManager.FiredDispatchCount++;
        LogVerbose($"Interact trigger '{trigger.ID}' fired. Event={eventName}, TargetType={sourceKind}, Source={sourceName}, {(isTerminalEdgeEvent ? "QueuedEvents" : "ExecutedEvents")}={count}");
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

    internal static void FireScanTrigger(ConfigDocument config, ScanTriggerRule trigger, string eventName, string sourceName, int puzzleIndex, int playersInScan, string stateKeySuffix)
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

        foreach (JsonElement eventElement in scanEvents)
        {
            if (TryExecuteConfiguredEvent(eventElement, $"Scan trigger '{trigger.ID}'"))
            {
                count++;
            }
        }

        HandleScanTriggerCycle(config, trigger, eventName, puzzleIndex);

        LogVerbose($"Scan trigger '{trigger.ID}' fired. Event={eventName}, Index={puzzleIndex}, PlayersInScan={playersInScan}, UsePlayerCountEvents={trigger.UsePlayerCountEvents}, Source={sourceName}, ExecutedEvents={count}");
    }

    private static void HandleScanTriggerCycle(ConfigDocument config, ScanTriggerRule trigger, string eventName, int puzzleIndex)
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
        int count = ExecuteEventList(trigger.TriggerCycleEvents, $"Scan trigger '{trigger.ID}' cycle Event={eventName} Index={puzzleIndex}");
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

    private static void EvaluateTrigger(ConfigDocument config, PositionTriggerRule trigger)
    {
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition)
        {
            return;
        }

        // PositionTriggers 不再限制 Host 端执行。
        // 任意玩家客户端只要检测到符合 TriggerAreaMode 的玩家进入/人数变化，就允许执行对应事件。
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
    private static void FireTrigger(PositionTriggerRule trigger, TriggerState state)
    {
        state.Fired = true;
        state.LastFireTime = Time.realtimeSinceStartup;
        int count = ExecuteEventList(trigger.Events.Concat(trigger.WardenEvents), $"Coordinate trigger '{trigger.ID}'");
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
        int count = ExecuteEventList(events, $"Coordinate trigger '{trigger.ID}' playerCount={insidePlayerCount}");
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

        int count = ExecuteEventList(trigger.TriggerCycleEvents, $"{ownerLabel} cycle={state.CompletedCycles}");
        LogVerbose($"Position trigger cycle events fired. ID={trigger.ID}, Cycles={state.CompletedCycles}, Required={required}, ExecutedEvents={count}");
    }

    private static int ExecuteEventList(IEnumerable<JsonElement> events, string ownerLabel)
    {
        int count = 0;
        foreach (JsonElement eventElement in events)
        {
            if (TryExecuteConfiguredEvent(eventElement, ownerLabel))
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

    private static bool TryExecuteConfiguredEvent(JsonElement eventElement, string ownerLabel)
    {
        try
        {
            if (TryHandleTriggerControlEvent(eventElement, ownerLabel))
            {
                return true;
            }

            // 0.8.9：WardenIntel 不再作为独立事件提前拦截。
            // 原版/AWO 逻辑中 WardenIntel 是 WardenObjectiveEventData 字段，
            // 必须随完整 WardenEvent 一起进入 WardenObjectiveManager.CheckAndExecuteEventsOnTrigger，
            // 这样普通原版事件、AWO 扩展事件、Legacy 事件都能同时携带 WardenIntel。

            if (TryBuildWardenEvent(eventElement, out WardenObjectiveEventData? eventData) && eventData != null)
            {
                try
                {
                    ExecuteWardenEvent(eventData);
                    return true;
                }
                catch (Exception ex)
                {
                    Log?.LogError($"{ownerLabel} failed to execute event '{eventData.Type}': {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.LogError($"{ownerLabel} failed to execute configured event: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    private static bool TryHandleTriggerControlEvent(JsonElement element, string ownerLabel)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string type = GetString(element, "Type", GetString(element, "EventType", string.Empty));
        string normalized = type.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        if (normalized != "settriggerenabled" && normalized != "seteventtriggerenabled" && normalized != "settriggerstate" && normalized != "enabletrigger" && normalized != "disabletrigger")
        {
            return false;
        }

        bool enable = normalized == "enabletrigger" ? true : normalized == "disabletrigger" ? false : GetBool(element, "Enabled", GetBool(element, "Enable", true));
        string targetID = GetString(element, "TargetID", GetString(element, "TriggerID", GetString(element, "ID", string.Empty)));
        string category = GetString(element, "Category", GetString(element, "TargetType", GetString(element, "TriggerCategory", "Any")));
        if (string.IsNullOrWhiteSpace(targetID))
        {
            Log?.LogWarning($"{ownerLabel} trigger-control event has no TargetID/TriggerID.");
            return true;
        }

        int changed = SetTriggerEnabled(targetID, category, enable);
        LogVerbose($"{ownerLabel} trigger-control event set Enabled={enable} for TargetID='{targetID}', Category='{category}', Changed={changed}.");
        return true;
    }

    private static int SetTriggerEnabled(string targetID, string category, bool enabled)
    {
        int changed = 0;
        string normalizedCategory = NormalizeTargetType(category);
        bool matchAnyCategory = string.IsNullOrWhiteSpace(category) || normalizedCategory == "any" || string.Equals(category, "all", StringComparison.OrdinalIgnoreCase);

        bool IDMatches(string id) => targetID == "*" || string.Equals(id, targetID, StringComparison.OrdinalIgnoreCase);

        foreach (ConfigDocument config in ConfigManager.Configs)
        {
            if ((matchAnyCategory || normalizedCategory == "position" || normalizedCategory == "coordinate") && config.PositionTriggers != null)
            {
                foreach (PositionTriggerRule trigger in config.PositionTriggers)
                {
                    if (IDMatches(trigger.ID) && trigger.Enabled != enabled)
                    {
                        trigger.Enabled = enabled;
                        changed++;
                    }
                }
            }

            if ((matchAnyCategory || normalizedCategory == "scan" || normalizedCategory == "bioscan") && config.ScanTriggers != null)
            {
                foreach (ScanTriggerRule trigger in config.ScanTriggers)
                {
                    if (IDMatches(trigger.ID) && trigger.Enabled != enabled)
                    {
                        trigger.Enabled = enabled;
                        changed++;
                    }
                }
            }

            if ((matchAnyCategory || normalizedCategory == "interact" || normalizedCategory == "interaction" || normalizedCategory == "terminal" || normalizedCategory == "bigpickup") && config.InteractTriggers != null)
            {
                foreach (InteractTriggerRule trigger in config.InteractTriggers)
                {
                    if (IDMatches(trigger.ID) && trigger.Enabled != enabled)
                    {
                        trigger.Enabled = enabled;
                        changed++;
                    }
                }
            }
        }

        if (changed > 0)
        {
            MarkActiveTriggerCacheDirty();
        }

        return changed;
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

    private static void ExecuteWardenEvent(WardenObjectiveEventData eventData)
    {
        try
        {
            // 0.8.9：采用 AWO/原版兼容的执行入口。
            // WardenIntel 不是独立的 eWardenObjectiveEventType，而是 WardenObjectiveEventData 上的字段。
            // WardenObjectiveManager.CheckAndExecuteEventsOnTrigger 会先处理 WardenIntel 字段，
            // 然后再执行 Type 对应的原版/AWO/Legacy 事件。
            // 这比 WorldEventManager.ExecuteEvent 更接近官图与 AWO 的事件链。
            WardenObjectiveManager.CheckAndExecuteEventsOnTrigger(eventData, eventData.Trigger, true, eventData.Delay);
            return;
        }
        catch (Exception managerEx)
        {
            try
            {
                // 兜底：部分事件如果不依赖 WardenIntel，仍可尝试直接交给 WorldEventManager。
                // 注意：这个 fallback 可能不会显示 WardenIntel，因此只在 Manager 路径失败后使用。
                WorldEventManager.ExecuteEvent(eventData, eventData.Delay);
                LogThrottled($"Fallback WorldEventManager.ExecuteEvent used for '{eventData.Type}' after WardenObjectiveManager failed: {managerEx.Message}");
            }
            catch (Exception worldEventEx)
            {
                LogThrottled($"WardenEvent execution failed for '{eventData.Type}': Manager={managerEx.GetType().Name}: {managerEx.Message}; WorldEvent={worldEventEx.GetType().Name}: {worldEventEx.Message}");
            }
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

    private static bool TryBuildWardenEvent(JsonElement element, out WardenObjectiveEventData? data)
    {
        data = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // 0.8.9：AWO 的事件链可以在任意 WardenObjectiveEventData 上携带 WardenIntel。
        // 关键点是不要手动 new LocalizedText；应优先让 Il2CppJsonNet 按游戏/插件原本的
        // WardenObjectiveEventData 反序列化规则来填充 WardenIntel 字段。
        // 之前只有疑似 AWO 事件才走 Il2CppJsonNet，导致 { "WardenIntel": 1345 }
        // 落入手写 LocalizedText 路径并在 IL2CPP 中 NullReference。
        if ((element.TryGetProperty("WardenIntel", out _) || IsLikelyAwoEvent(element)) && TryBuildWardenEventViaAwoJson(element, out data))
        {
            return true;
        }

        return TryBuildNativeWardenEvent(element, out data);
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
        string typeText = GetString(element, "Type", string.Empty);
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return false;
        }

        if (int.TryParse(typeText, out int numericType) && numericType >= 10000)
        {
            return true;
        }

        if (!TryParseEnum(typeText, out eWardenObjectiveEventType _))
        {
            return true;
        }

        foreach (string field in new[]
        {
            "SpecialBool", "SpecialNumber", "SpecialText", "SubObjective", "Fog", "Reactor", "Countdown",
            "Countup", "CleanupEnemies", "SpawnHibernates", "SpawnScouts", "AddTerminalCommand", "AddCommand",
            "HideTerminalCommand", "HideCommand", "UnhideTerminalCommand", "UnhideCommand", "GiveResource",
            "ActiveEnemyWave", "NestedEvent", "StartEventLoop", "EventLoop", "TeleportPlayer", "InfectPlayer",
            "DamagePlayer", "RevivePlayer", "AdjustTimer", "NavMarker", "CameraShake", "Portal", "SuccessScreen",
            "MultiProgression", "WaveRoarSound", "CustomHudText", "CustomHud", "SpecialHudTimer", "SpecialHud",
            "PlayerDialogue", "SetTerminalLog", "TerminalLog", "ObjectiveItems", "DimensionData", "EnvironmentData"
        })
        {
            if (element.TryGetProperty(field, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildWardenEventViaAwoJson(JsonElement element, out WardenObjectiveEventData? data)
    {
        data = null;
        try
        {
            // Do not require a concrete AWO type here. Legacy.dll and forks may expose the same
            // Il2CppJsonNet deserialization path without using AWO.Modules.WEE.WardenEventExt.
            Type? jsonConvertType = FindLoadedType("Il2CppJsonNet.JsonConvert");
            if (jsonConvertType == null)
            {
                return false;
            }

            MethodInfo? method = jsonConvertType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "DeserializeObject" && m.IsGenericMethodDefinition)
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                });

            if (method == null)
            {
                return false;
            }

            string json = NormalizeWardenIntelOnlyEventJson(element);
            object? parsed = method.MakeGenericMethod(typeof(WardenObjectiveEventData)).Invoke(null, new object?[] { json });
            if (parsed is WardenObjectiveEventData e)
            {
                data = e;
                return true;
            }
        }
        catch (Exception ex)
        {
            LogThrottled($"AWO Json build failed for event '{GetString(element, "Type", string.Empty)}': {ex.GetType().Name}: {ex.Message}");
        }
        return false;
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
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersInsideScan", GetScanSourceName(scan), puzzleIndex, Math.Min(Math.Max(currentPlayers, 1), 4), suffix);
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
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersExitedScan", GetScanSourceName(scan), puzzleIndex, eventPlayerCount, suffix);
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
                Runtime.FireScanTrigger(config, trigger, "OnScanActivated", GetScanSourceName(scan), puzzleIndex, playersInScan, suffix);
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
                        Runtime.FireScanTrigger(config, trigger, "OnScanActivated", GetScanSourceName(scan), puzzleIndex, currentPlayers, activationSuffix);
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
                Runtime.FireScanTrigger(config, trigger, eventName, GetScanSourceName(scan), puzzleIndex, previousPlayers, suffix);
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
                        Runtime.FireScanTrigger(config, trigger, "OnAllPlayersEnterScan", GetScanSourceName(scan), puzzleIndex, Math.Min(Math.Max(currentPlayers, 1), 4), suffix);
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
                    Runtime.FireScanTrigger(config, trigger, "OnAllPlayersExitScan", GetScanSourceName(scan), puzzleIndex, eventPlayerCount, suffix);
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



internal static class InteractTriggerManager
{
    internal static readonly Dictionary<string, TriggerState> RuleStates = new(StringComparer.OrdinalIgnoreCase);
    internal static int FiredDispatchCount;
    private static readonly Dictionary<string, float> LastRawEventTimes = new(StringComparer.OrdinalIgnoreCase);
    // 终端在 GTFO 中会同时经过多个交互方法。这里记录每台终端的“正在使用”状态，避免一次使用/退出被多个 Harmony Patch 重复触发。
    private static readonly Dictionary<string, bool> TerminalUseStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> LastTerminalTransitionTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<PendingTerminalEvent> PendingTerminalEvents = new();
    private static readonly HashSet<string> PendingTerminalEventKeys = new(StringComparer.OrdinalIgnoreCase);
    // 终端持续状态重复触发：只有玩家真实使用过终端后才允许重复触发，避免关卡初始“未使用”状态刷事件。
    private static readonly HashSet<string> TerminalUsingRepeatEligibleKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TerminalExitedRepeatEligibleKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TerminalEverUsedKeys = new(StringComparer.OrdinalIgnoreCase);
    // 1.0.6：持续触发只处理真实使用过的终端，避免每次 Tick FindObjectsOfType 全图扫描。
    private static readonly Dictionary<string, Component> TerminalRepeatObjects = new(StringComparer.OrdinalIgnoreCase);
    // 1.0.6：持续触发按触发器 Cooldown 到期后才进入匹配/执行路径，避免每 0.2 秒对同一目标重复调用匹配逻辑。
    private static readonly Dictionary<string, float> TerminalRepeatNextDueTimes = new(StringComparer.OrdinalIgnoreCase);
    // 大物品状态：只在“放置 -> 拾取”和“拾取 -> 放下”状态变化时触发，避免生成/回放状态误触发。
    private sealed class BigPickupCycleState
    {
        public bool WaitingForDrop;
        public int CompletedCycles;
    }

    private static readonly Dictionary<string, bool> BigPickupHeldStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> LastBigPickupTransitionTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BigPickupCycleState> BigPickupCycleStates = new(StringComparer.OrdinalIgnoreCase);
    // 大物品持续状态重复触发只在玩家真实拾取/放下产生过状态边沿后启用，避免关卡初始放置状态直接刷事件。
    private static readonly HashSet<string> BigPickupRepeatEligibleKeys = new(StringComparer.OrdinalIgnoreCase);
    // 1.0.6：持续触发只处理产生过真实拾取/放下边沿的大物品，避免每次 Tick FindObjectsOfType 全图扫描。
    private static readonly Dictionary<string, CarryItemPickup_Core> BigPickupRepeatObjects = new(StringComparer.OrdinalIgnoreCase);
    // 1.0.6：持续触发按触发器 Cooldown 到期后才进入匹配/执行路径，避免每 0.2 秒对同一目标重复调用匹配逻辑。
    private static readonly Dictionary<string, float> BigPickupRepeatNextDueTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RuntimeIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> NextIndexByType = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dumpedTargetIndexes;
    private static float _lastDumpTryTime;

    private sealed class PendingTerminalEvent
    {
        public Component Terminal = null!;
        public PlayerAgent? Player;
        public string EventName = string.Empty;
        public string Source = string.Empty;
        public float DueTime;
        public string Key = string.Empty;
    }

    internal static void Reset()
    {
        RuleStates.Clear();
        FiredDispatchCount = 0;
        LastRawEventTimes.Clear();
        TerminalUseStates.Clear();
        LastTerminalTransitionTimes.Clear();
        PendingTerminalEvents.Clear();
        PendingTerminalEventKeys.Clear();
        TerminalUsingRepeatEligibleKeys.Clear();
        TerminalExitedRepeatEligibleKeys.Clear();
        TerminalEverUsedKeys.Clear();
        TerminalRepeatObjects.Clear();
        TerminalRepeatNextDueTimes.Clear();
        Runtime.ClearTerminalSelectorCache();
        BigPickupHeldStates.Clear();
        LastBigPickupTransitionTimes.Clear();
        BigPickupCycleStates.Clear();
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
        int terminals = 0;
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

        try
        {
            foreach (LG_ComputerTerminal terminal in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>())
            {
                if (terminal == null) continue;
                terminals++;
                int index = GetRuntimeIndex("terminal", terminal);
                string serialText = SafeNestedStringMember(terminal, "m_serial", "Serial");
                Runtime.CacheTerminalSelectorsForTerminal(terminal, index);
                Runtime.LogVerbose($"CTE Terminal Index={index} TSL='{Runtime.GetTerminalTslSelectorText(terminal)}' TSLFallback='[TERMINAL_{index}]' Name='{SafeObjectName(terminal)}' SerialNumber={SafeIntMember(terminal, "m_serialNumber", "SerialNumber")} SerialText='{serialText}' ItemKey='{SafeStringMember(terminal, "ItemKey", "_ItemKey_k__BackingField")}' Position={SafePosition(terminal)}");
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"Terminal index dump failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (pickups + terminals > 0)
        {
            _dumpedTargetIndexes = true;
            Runtime.LogVerbose($"CTE target index dump complete. BigPickups={pickups}, Terminals={terminals}. Use BigPickup SPOIndex (ScanPosOverride/ECC Item Index, starts at 1) for big item triggers; use TerminalSerial/TSL for terminal triggers.");
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

    private static string SafeNestedStringMember(object obj, params string[] names)
    {
        object? member = null;
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                member = p?.GetValue(obj);
                if (member == null)
                {
                    FieldInfo? f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    member = f?.GetValue(obj);
                }
                if (member != null) break;
            }
            catch { }
        }
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

    internal static void ProcessTerminalRepeatEvents()
    {
        if (!GameStateManager.IsInExpedition) return;

        bool hasRepeatTrigger = false;
        foreach ((ConfigDocument _config, InteractTriggerRule trigger) in Runtime.GetActiveInteractTriggers())
        {
            if (Runtime.NormalizeTargetType(trigger.TargetType) != "terminal") continue;
            string mode = Runtime.NormalizeInteractionTriggerMode(trigger.TriggerMode);
            if (mode == "onterminalusing" || mode == "onterminalexited")
            {
                hasRepeatTrigger = true;
                break;
            }
        }
        if (!hasRepeatTrigger || TerminalRepeatObjects.Count == 0) return;

        foreach (KeyValuePair<string, Component> pair in TerminalRepeatObjects.ToArray())
        {
            string key = pair.Key;
            Component terminal = pair.Value;
            if (terminal == null)
            {
                TerminalRepeatObjects.Remove(key);
                TerminalUseStates.Remove(key);
                TerminalUsingRepeatEligibleKeys.Remove(key);
                TerminalExitedRepeatEligibleKeys.Remove(key);
                TerminalEverUsedKeys.Remove(key);
                continue;
            }

            bool isUsing = TerminalUseStates.TryGetValue(key, out bool currentUsing) && currentUsing;

            if (isUsing && TerminalUsingRepeatEligibleKeys.Contains(key))
            {
                string eventName = "OnTerminalUsing";
                string normalizedMode = Runtime.NormalizeInteractionTriggerMode(eventName);
                if (!TryGetMinimumInteractRepeatCooldown("terminal", normalizedMode, out float minCooldown)) continue;
                string suffix = key + "::repeat::" + eventName;
                if (!IsRepeatDue(TerminalRepeatNextDueTimes, suffix, minCooldown)) continue;
                Runtime.FireInteractTrigger("Terminal", eventName, terminal, null, "TerminalRepeat", suffix);
            }
            else if (!isUsing && TerminalExitedRepeatEligibleKeys.Contains(key))
            {
                string eventName = "OnTerminalExited";
                string normalizedMode = Runtime.NormalizeInteractionTriggerMode(eventName);
                if (!TryGetMinimumInteractRepeatCooldown("terminal", normalizedMode, out float minCooldown)) continue;
                string suffix = key + "::repeat::" + eventName;
                if (!IsRepeatDue(TerminalRepeatNextDueTimes, suffix, minCooldown)) continue;
                Runtime.FireInteractTrigger("Terminal", eventName, terminal, null, "TerminalRepeat", suffix);
            }
        }
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
                continue;
            }

            if (!BigPickupRepeatEligibleKeys.Contains(key)) continue;
            if (!BigPickupHeldStates.TryGetValue(key, out bool isHeld)) continue;

            string eventName = isHeld ? "OnBigPickupHeld" : "OnBigPickupPlaced";
            string normalizedMode = Runtime.NormalizeInteractionTriggerMode(eventName);
            if (!TryGetMinimumInteractRepeatCooldown("bigpickup", normalizedMode, out float minCooldown)) continue;
            string suffix = key + "::repeat::" + eventName;
            if (!IsRepeatDue(BigPickupRepeatNextDueTimes, suffix, minCooldown)) continue;
            Runtime.FireInteractTrigger("BigPickup", eventName, item, null, "BigPickupRepeat", suffix);
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

        string eventName = nowHeld ? "OnBigPickupPickup" : "OnBigPickupDrop";
        int beforeDispatch = FiredDispatchCount;
        int runtimeIndex = GetRuntimeIndex("BigPickup", item);
        int spoIndex = -1;
        string spoSource = "Missing:ScanPosOverride.BigPickupItemIndex";
        SpoIndexResolver.TryGetBigPickupSpoIndex(item, out spoIndex, out spoSource);
        Runtime.LogVerbose($"Detected BigPickup state change. Event={eventName}, Source={source}, SPOIndex={spoIndex}, CTERuntimeIndex={runtimeIndex}, SPOSource='{spoSource}', Name='{SafeObjectName(item)}'");
        Fire("BigPickup", eventName, item, player, source, useStableStateKey: true);
        if (FiredDispatchCount == beforeDispatch)
        {
            Runtime.LogVerbose($"BigPickup state changed but no InteractTrigger matched. SPOIndex={spoIndex}, CTERuntimeIndex={runtimeIndex}, Serial={SafeIntMember(item, "m_serialNumber", "SerialNumber")}, ItemKey='{SafeStringMember(item, "m_itemKey")}', PublicName='{SafeStringMember(item, "PublicName")}', Source={source}");
        }

        HandleBigPickupPickupDropCycle(item, player, source, nowHeld);
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
            BigPickupHeldStates[key] = nowHeld;
            return false;
        }

        if (BigPickupHeldStates.TryGetValue(key, out bool wasHeld))
        {
            if (wasHeld == nowHeld)
            {
                return false;
            }
        }
        else
        {
            // 初次观察到“放置在关卡中”通常是生成/同步回放，不应触发放下事件。
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

    internal static void OnTerminalUse(Component? terminal, PlayerAgent? player, string source)
    {
        if (terminal == null) return;
        if (!TryAcceptTerminalTransition(terminal, player, true, source, out Component canonicalTerminal)) return;
        EnqueueTerminalEvent(canonicalTerminal, player, "OnTerminalUse", source);
    }

    internal static void OnTerminalUnused(Component? terminal, PlayerAgent? player, string source)
    {
        if (terminal == null) return;
        if (!TryAcceptTerminalTransition(terminal, player, false, source, out Component canonicalTerminal)) return;
        EnqueueTerminalEvent(canonicalTerminal, player, "OnTerminalUnused", source);
    }

    private static void EnqueueTerminalEvent(Component terminal, PlayerAgent? player, string eventName, string source)
    {
        string key = BuildStableComponentKey("terminal", terminal) + ":" + eventName;
        if (PendingTerminalEventKeys.Contains(key)) return;
        PendingTerminalEventKeys.Add(key);
        PendingTerminalEvents.Enqueue(new PendingTerminalEvent
        {
            Terminal = terminal,
            Player = player,
            EventName = eventName,
            Source = source,
            DueTime = Time.realtimeSinceStartup + 0.10f,
            Key = key
        });
        Runtime.LogVerbose($"Queued terminal event {eventName} from {source} for {BuildStableComponentKey("terminal", terminal)}");
    }

    internal static void ProcessPendingTerminalEvents()
    {
        if (PendingTerminalEvents.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        int processed = 0;
        while (PendingTerminalEvents.Count > 0 && processed < 1)
        {
            PendingTerminalEvent pending = PendingTerminalEvents.Peek();
            if (pending.DueTime > now) break;
            PendingTerminalEvents.Dequeue();
            PendingTerminalEventKeys.Remove(pending.Key);
            Fire("Terminal", pending.EventName, pending.Terminal, pending.Player, pending.Source, useStableStateKey: true);
            processed++;
        }
    }

    // 终端交互会触发 EnterFPSView / OnInteract / TriggerInteractionAction 等多个方法。
    // 只允许“未使用 -> 使用中”和“使用中 -> 未使用”两个状态转换进入事件系统，修复使用终端瞬间卡顿和重复触发。
    private static bool TryAcceptTerminalTransition(Component source, PlayerAgent? player, bool entering, string sourceLabel, out Component canonicalTerminal)
    {
        canonicalTerminal = ResolveCanonicalTerminal(source) ?? source;
        string terminalKey = BuildStableComponentKey("terminal", canonicalTerminal);
        // 终端是单占用对象，用终端本身作为状态 key。
        // 不把 PlayerAgent 放进 key，避免进入时有玩家、退出时玩家字段为空导致退出事件无法与进入事件配对。
        string key = terminalKey;
        TerminalRepeatObjects[key] = canonicalTerminal;
        float now = Time.realtimeSinceStartup;

        if (TerminalUseStates.TryGetValue(key, out bool isUsing))
        {
            if (entering && isUsing)
            {
                Runtime.LogVerbose($"Ignored duplicate terminal use transition from {sourceLabel} for {terminalKey}");
                return false;
            }
            if (!entering && !isUsing)
            {
                Runtime.LogVerbose($"Ignored duplicate terminal unused transition from {sourceLabel} for {terminalKey}");
                return false;
            }
        }
        else if (!entering)
        {
            // 没有进入记录时的 unused 往往来自关卡同步/初始化，不应触发退出终端事件。
            TerminalUseStates[key] = false;
            return false;
        }

        string transitionKey = key + ":" + (entering ? "use" : "unused");
        if (LastTerminalTransitionTimes.TryGetValue(transitionKey, out float last) && now - last < 0.35f)
        {
            return false;
        }

        TerminalUseStates[key] = entering;
        LastTerminalTransitionTimes[transitionKey] = now;

        if (entering)
        {
            TerminalEverUsedKeys.Add(key);
            TerminalUsingRepeatEligibleKeys.Add(key);
            TerminalExitedRepeatEligibleKeys.Remove(key);
        }
        else if (TerminalEverUsedKeys.Contains(key))
        {
            TerminalUsingRepeatEligibleKeys.Remove(key);
            TerminalExitedRepeatEligibleKeys.Add(key);
        }

        return true;
    }

    private static Component? ResolveCanonicalTerminal(Component source)
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

    private static string BuildStableComponentKey(string targetType, Component source)
    {
        try { return targetType + ":" + source.GetInstanceID(); }
        catch { return targetType + ":" + source.GetHashCode(); }
    }

    private static void Fire(string targetType, string eventName, Component source, PlayerAgent? player, string sourceLabel, bool useStableStateKey = false)
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
            Runtime.FireInteractTrigger(targetType, eventName, source, player, sourceName, suffix);
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

[HarmonyPatch(typeof(Interact_ComputerTerminal), "TriggerInteractionAction")]
internal static class Interact_ComputerTerminal_TriggerInteractionAction_CTEPatch
{
    private static void Postfix(Interact_ComputerTerminal __instance, PlayerAgent source)
    {
        // 0.8.5：不再从普通交互回调触发终端事件，避免与 EnterFPSView/OnStateChange 重复导致卡顿。
    }
}

[HarmonyPatch(typeof(Interact_Terminal), "OnSelectedChange")]
internal static class Interact_Terminal_OnSelectedChange_CTEPatch
{
    private static void Postfix(Interact_Terminal __instance, bool selected, PlayerAgent agent, bool forceUpdate)
    {
        // 0.8.5：选择/取消选择只代表准星悬停，不代表真正进入/退出终端使用状态。
        // 保留 Patch 空壳以避免旧版 Harmony 类型差异，但不执行事件。
    }
}


[HarmonyPatch(typeof(LG_ComputerTerminal), "OnInteract")]
internal static class LG_ComputerTerminal_OnInteract_CTEPatch
{
    private static void Postfix(LG_ComputerTerminal __instance, PlayerAgent interactionSource, bool __result)
    {
        // 0.8.5：OnInteract 会在 EnterFPSView 前后重复命中，终端事件改由状态变化/EnterFPSView 驱动。
    }
}


[HarmonyPatch(typeof(LG_ComputerTerminal), "OnStateChange")]
internal static class LG_ComputerTerminal_OnStateChange_CTEPatch
{
    private static void Postfix(LG_ComputerTerminal __instance, pComputerTerminalState oldState, pComputerTerminalState newState, bool isRecall)
    {
        if (isRecall) return;
        bool hasPlayer = false;
        try { hasPlayer = __instance.m_hasInteractingPlayer; } catch { }
        PlayerAgent? player = TryGetTerminalPlayer(__instance);
        if (hasPlayer)
        {
            InteractTriggerManager.OnTerminalUse(__instance, player, "LG_ComputerTerminal.OnStateChange:hasPlayer");
        }
        else
        {
            InteractTriggerManager.OnTerminalUnused(__instance, player, "LG_ComputerTerminal.OnStateChange:noPlayer");
        }
    }

    private static PlayerAgent? TryGetTerminalPlayer(LG_ComputerTerminal terminal)
    {
        try { PlayerAgent? player = terminal.m_localInteractionSource; if (player != null) return player; } catch { }
        try { PlayerAgent? player = terminal.m_syncedInteractionSource; if (player != null) return player; } catch { }
        return null;
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminal), "EnterFPSView")]
internal static class LG_ComputerTerminal_EnterFPSView_CTEPatch
{
    private static void Postfix(LG_ComputerTerminal __instance)
    {
        // 0.8.7：OnStateChange 在部分版本中不会可靠携带玩家进入状态；
        // EnterFPSView 作为真实进入终端视角的状态边沿，仍经过 TryAcceptTerminalTransition 去重。
        InteractTriggerManager.OnTerminalUse(__instance, TryGetTerminalPlayer(__instance), "LG_ComputerTerminal.EnterFPSView");
    }

    private static PlayerAgent? TryGetTerminalPlayer(LG_ComputerTerminal terminal)
    {
        try
        {
            PlayerAgent? player = terminal.m_localInteractionSource;
            if (player != null) return player;
        }
        catch { }

        try
        {
            PlayerAgent? player = terminal.m_syncedInteractionSource;
            if (player != null) return player;
        }
        catch { }

        return null;
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminal), "ExitFPSView")]
internal static class LG_ComputerTerminal_ExitFPSView_CTEPatch
{
    private static void Prefix(LG_ComputerTerminal __instance)
    {
        // 0.8.7：ExitFPSView 是真实退出终端视角的状态边沿，仍经过 TryAcceptTerminalTransition 去重。
        InteractTriggerManager.OnTerminalUnused(__instance, TryGetTerminalPlayer(__instance), "LG_ComputerTerminal.ExitFPSView");
    }

    private static PlayerAgent? TryGetTerminalPlayer(LG_ComputerTerminal terminal)
    {
        try
        {
            PlayerAgent? player = terminal.m_localInteractionSource;
            if (player != null) return player;
        }
        catch { }

        try
        {
            PlayerAgent? player = terminal.m_syncedInteractionSource;
            if (player != null) return player;
        }
        catch { }

        return null;
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminal), "DoExitFPSView")]
internal static class LG_ComputerTerminal_DoExitFPSView_CTEPatch
{
    private static void Prefix(LG_ComputerTerminal __instance)
    {
        // 0.8.7：DoExitFPSView 作为退出 fallback，仍经过 TryAcceptTerminalTransition 去重。
        InteractTriggerManager.OnTerminalUnused(__instance, TryGetTerminalPlayer(__instance), "LG_ComputerTerminal.DoExitFPSView");
    }

    private static PlayerAgent? TryGetTerminalPlayer(LG_ComputerTerminal terminal)
    {
        try
        {
            PlayerAgent? player = terminal.m_localInteractionSource;
            if (player != null) return player;
        }
        catch { }

        try
        {
            PlayerAgent? player = terminal.m_syncedInteractionSource;
            if (player != null) return player;
        }
        catch { }

        return null;
    }
}


internal static class DebugMarkerManager
{
    private sealed class MarkerEntry
    {
        public GameObject Root = null!;
        public Transform? LabelTransform;
        public bool Resolved;
    }

    private static readonly Dictionary<string, MarkerEntry> Markers = new(StringComparer.OrdinalIgnoreCase);
    private static float _lastRefreshTime;
    private static string _lastSignature = string.Empty;

    internal static void UpdateMarkers(List<(ConfigDocument Config, PositionTriggerRule Trigger)> activeTriggers)
    {
        try
        {
            bool anyDebug = activeTriggers.Any(t => t.Config.Debug.Enabled && t.Config.Debug.ShowScanMarkers);
            if (!anyDebug)
            {
                Clear();
                return;
            }

            float refreshInterval = Math.Max(1.0f, activeTriggers.Where(t => t.Config.Debug.Enabled).Select(t => t.Config.Debug.RefreshInterval).DefaultIfEmpty(2.0f).Min());
            string signature = BuildSignature(activeTriggers);
            if (signature == _lastSignature && Time.realtimeSinceStartup - _lastRefreshTime < refreshInterval)
            {
                FaceLabelsToCamera();
                return;
            }

            _lastSignature = signature;
            _lastRefreshTime = Time.realtimeSinceStartup;

            HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
            foreach ((ConfigDocument config, PositionTriggerRule trigger) in activeTriggers)
            {
                if (!config.Debug.Enabled || !config.Debug.ShowScanMarkers || !trigger.DebugVisible)
                {
                    continue;
                }

                string key = config.FilePath + "::" + trigger.ID;
                wanted.Add(key);
                if (Markers.TryGetValue(key, out MarkerEntry? existing))
                {
                    if (!existing.Resolved)
                    {
                        DestroyMarker(key);
                        MarkerEntry? marker = CreateMarker(config, trigger);
                        if (marker != null) Markers[key] = marker;
                    }
                }
                else
                {
                    MarkerEntry? marker = CreateMarker(config, trigger);
                    if (marker != null) Markers[key] = marker;
                }
            }

            foreach (string key in Markers.Keys.ToList())
            {
                if (!wanted.Contains(key))
                {
                    DestroyMarker(key);
                }
            }

            FaceLabelsToCamera();
        }
        catch (Exception ex)
        {
            Runtime.Log?.LogWarning($"Debug marker update failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void Clear()
    {
        foreach (string key in Markers.Keys.ToList())
        {
            DestroyMarker(key);
        }
        _lastSignature = string.Empty;
    }

    private static string BuildSignature(List<(ConfigDocument Config, PositionTriggerRule Trigger)> activeTriggers)
    {
        return string.Join("|", activeTriggers
            .Where(t => t.Config.Debug.Enabled && t.Config.Debug.ShowScanMarkers && t.Trigger.DebugVisible)
            .Select(t => $"{t.Config.FilePath}:{t.Trigger.ID}:{t.Trigger.TriggerAreaMode}:{t.Trigger.LocalIndex}:{t.Trigger.Count}:{FormatPositionSignature(t.Trigger.Position)}:{t.Trigger.Radius:F2}:{t.Config.Debug.MarkerColor}:{t.Trigger.DebugColor}:{t.Config.Debug.MarkerAlpha:F2}"));
    }

    private static string FormatPositionSignature(PositionData? position)
    {
        return position == null ? "<none>" : $"{position.x:F2},{position.y:F2},{position.z:F2}";
    }

    private static MarkerEntry? CreateMarker(ConfigDocument config, PositionTriggerRule trigger)
    {
        Bounds debugBounds;
        string debugSource;
        bool resolved = Runtime.TryGetPositionTriggerDebugBounds(trigger, out debugBounds, out debugSource);
        if (!resolved)
        {
            if (!Runtime.IsTransientLookupFailure(debugSource))
            {
                Runtime.LogThrottled($"Debug marker for trigger '{trigger.ID}' is waiting for real bounds: {debugSource}. No hidden placeholder marker will be created.");
            }
            return null;
        }

        Vector3 center = debugBounds.center;
        string mode = (trigger.TriggerAreaMode ?? "Radius").Trim().ToLowerInvariant();
        bool isAreaMode = mode == "overridebigzone" || mode == "bigzone" || mode == "zone" || mode == "overridearea" || mode == "area";
        float radius = Math.Max(config.Debug.MinimumRadius, Math.Max(debugBounds.extents.x, debugBounds.extents.z) * config.Debug.RadiusScale);

        string colorText = string.IsNullOrWhiteSpace(trigger.DebugColor)
            ? (isAreaMode ? "#A020F0" : config.Debug.MarkerColor)
            : trigger.DebugColor;
        Color markerColor = ParseColor(colorText, new Color(0.63f, 0.12f, 0.94f, config.Debug.MarkerAlpha));
        markerColor.a = config.Debug.MarkerAlpha;

        if (isAreaMode && (center.sqrMagnitude < 0.05f || Mathf.Abs(center.x) >= 9000f || Mathf.Abs(center.y) >= 9000f || Mathf.Abs(center.z) >= 9000f))
        {
            Runtime.LogThrottled($"Debug marker for trigger '{trigger.ID}' skipped: resolved {mode} center is invalid ({center.x:F2},{center.y:F2},{center.z:F2}).");
            return null;
        }

        GameObject root = new GameObject($"CTE_DebugScanMarker_{SanitizeName(trigger.ID)}");
        root.transform.position = center + Vector3.up * config.Debug.HeightOffset;

        MarkerEntry entry = new MarkerEntry { Root = root, Resolved = resolved };
        if (!isAreaMode)
        {
            CreateRadiusRing(root, radius, markerColor, config.Debug);
        }

        // OverrideBigZone / OverrideArea 的 Debug 现在只显示触发器 ID 文本，
        // 不再绘制紫点、外框或范围大小，避免遮挡视野并避免误解实际范围。
        if (config.Debug.ShowNames || isAreaMode)
        {
            GameObject label = new GameObject("Label");
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = Vector3.up * config.Debug.LabelHeightOffset;
            TextMesh text = label.AddComponent<TextMesh>();
            text.text = trigger.ID;
            text.characterSize = isAreaMode ? 0.35f : 0.25f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = ParseColor(config.Debug.LabelColor, Color.white);
            entry.LabelTransform = label.transform;
        }

        if (isAreaMode)
        {
            Runtime.LogVerbose($"Created debug ID-only marker for trigger '{trigger.ID}' Mode={trigger.TriggerAreaMode} Source={debugSource} Center={center} Size=({debugBounds.size.x:F1},{debugBounds.size.y:F1},{debugBounds.size.z:F1})");
        }
        else
        {
            Runtime.LogVerbose($"Created debug marker for trigger '{trigger.ID}' Mode={trigger.TriggerAreaMode} Source={debugSource} Center={center} DisplayRadius={radius}");
        }
        return entry;
    }


    private static Material? CreateMarkerMaterial(Color markerColor)
    {
        Shader? shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Transparent");
        if (shader == null) return null;
        Material mat = new Material(shader);
        mat.color = markerColor;
        return mat;
    }

    private static void ConfigureLine(LineRenderer line, Color markerColor, DebugOptions debug, bool loop, int pointCount, float widthScale = 1.0f)
    {
        line.useWorldSpace = false;
        line.loop = loop;
        line.positionCount = pointCount;
        line.widthMultiplier = Math.Max(0.02f, debug.MarkerHeight * widthScale);
        line.startColor = markerColor;
        line.endColor = markerColor;
        Material? mat = CreateMarkerMaterial(markerColor);
        if (mat != null) line.material = mat;
    }

    private static void CreateRadiusRing(GameObject root, float radius, Color markerColor, DebugOptions debug)
    {
        GameObject ring = new GameObject("RadiusRing");
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = Vector3.zero;
        LineRenderer line = ring.AddComponent<LineRenderer>();
        ConfigureLine(line, markerColor, debug, loop: true, pointCount: 64);
        for (int i = 0; i < line.positionCount; i++)
        {
            float a = ((float)i / line.positionCount) * Mathf.PI * 2f;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
        }
    }

    private static void CreateBoundsOutline(GameObject root, Bounds bounds, Color markerColor, DebugOptions debug)
    {
        GameObject outline = new GameObject("AreaBoundsOutline");
        outline.transform.SetParent(root.transform, false);
        outline.transform.localPosition = Vector3.zero;
        LineRenderer line = outline.AddComponent<LineRenderer>();
        ConfigureLine(line, markerColor, debug, loop: true, pointCount: 4, widthScale: 1.5f);
        float minX = bounds.min.x - bounds.center.x;
        float maxX = bounds.max.x - bounds.center.x;
        float minZ = bounds.min.z - bounds.center.z;
        float maxZ = bounds.max.z - bounds.center.z;
        line.SetPosition(0, new Vector3(minX, 0f, minZ));
        line.SetPosition(1, new Vector3(maxX, 0f, minZ));
        line.SetPosition(2, new Vector3(maxX, 0f, maxZ));
        line.SetPosition(3, new Vector3(minX, 0f, maxZ));
    }

    private static void CreateBoundsDotGrid(GameObject root, Bounds bounds, Color markerColor, DebugOptions debug)
    {
        float sizeX = Math.Max(0.5f, bounds.size.x);
        float sizeZ = Math.Max(0.5f, bounds.size.z);
        float area = Math.Max(1f, sizeX * sizeZ);
        float spacing = Mathf.Clamp(Mathf.Sqrt(area / 72f), 4f, 18f);
        int xCount = Mathf.Clamp(Mathf.FloorToInt(sizeX / spacing) + 1, 2, 16);
        int zCount = Mathf.Clamp(Mathf.FloorToInt(sizeZ / spacing) + 1, 2, 16);
        while (xCount * zCount > 96)
        {
            if (xCount >= zCount && xCount > 2) xCount--;
            else if (zCount > 2) zCount--;
            else break;
        }

        float minX = bounds.min.x - bounds.center.x;
        float maxX = bounds.max.x - bounds.center.x;
        float minZ = bounds.min.z - bounds.center.z;
        float maxZ = bounds.max.z - bounds.center.z;
        float dotRadius = Mathf.Clamp(Mathf.Min(sizeX, sizeZ) * 0.0125f, 0.25f, 0.85f);
        int dotIndex = 0;
        for (int ix = 0; ix < xCount; ix++)
        {
            float tx = xCount <= 1 ? 0.5f : (float)ix / (xCount - 1);
            float x = Mathf.Lerp(minX, maxX, tx);
            for (int iz = 0; iz < zCount; iz++)
            {
                float tz = zCount <= 1 ? 0.5f : (float)iz / (zCount - 1);
                float z = Mathf.Lerp(minZ, maxZ, tz);
                GameObject dot = new GameObject($"PurpleDot_{dotIndex++:00}");
                dot.transform.SetParent(root.transform, false);
                dot.transform.localPosition = new Vector3(x, 0.03f, z);
                LineRenderer line = dot.AddComponent<LineRenderer>();
                ConfigureLine(line, markerColor, debug, loop: true, pointCount: 12, widthScale: 2.0f);
                for (int i = 0; i < line.positionCount; i++)
                {
                    float a = ((float)i / line.positionCount) * Mathf.PI * 2f;
                    line.SetPosition(i, new Vector3(Mathf.Cos(a) * dotRadius, 0f, Mathf.Sin(a) * dotRadius));
                }
            }
        }
    }

    private static void DestroyMarker(string key)
    {
        if (Markers.TryGetValue(key, out MarkerEntry? marker) && marker.Root != null)
        {
            UnityEngine.Object.Destroy(marker.Root);
        }
        Markers.Remove(key);
    }

    private static void FaceLabelsToCamera()
    {
        Camera? cam = Camera.main;
        if (cam == null) return;
        Quaternion rotation = cam.transform.rotation;
        foreach (MarkerEntry entry in Markers.Values)
        {
            if (entry.LabelTransform != null)
            {
                entry.LabelTransform.rotation = rotation;
            }
        }
    }

    private static Color ParseColor(string text, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(text) && ColorUtility.TryParseHtmlString(text, out Color color))
        {
            return color;
        }
        return fallback;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}

[HarmonyPatch(typeof(WorldEventManager), nameof(WorldEventManager.Update))]
internal static class WorldEventManager_Update_Patch
{
    private static void Postfix()
    {
        Runtime.Tick();
    }
}
