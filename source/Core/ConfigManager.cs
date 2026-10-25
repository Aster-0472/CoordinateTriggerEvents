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

            WriteFeatureTemplatePair(Path.Combine(folder, "PositionTriggers"), CreateChinesePositionTemplateJson(), CreateEnglishPositionTemplateJson(), log);
            WriteFeatureTemplatePair(Path.Combine(folder, "ScanTriggers"), CreateChineseScanTemplateJson(), CreateEnglishScanTemplateJson(), log);
            WriteFeatureTemplatePair(Path.Combine(folder, "InteractTriggers", "BigPickup"), CreateChineseBigPickupTemplateJson(), CreateEnglishBigPickupTemplateJson(), log);
            WriteFeatureTemplatePair(Path.Combine(folder, "HudInteractTriggers"), CreateChineseHudInteractTemplateJson(), CreateEnglishHudInteractTemplateJson(), log);
        }
        catch (Exception ex)
        {
            log?.LogWarning($"Failed to ensure CTE template configs: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void WriteFeatureTemplatePair(string folder, string chineseTemplate, string englishTemplate, ManualLogSource? log)
    {
        WriteTemplateIfMissing(Path.Combine(folder, TemplateChineseFileName), chineseTemplate, log);
        WriteTemplateIfMissing(Path.Combine(folder, TemplateEnglishFileName), englishTemplate, log);
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
        if (!ShouldRewriteTemplate(path, out string reason))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (reason == "missing")
        {
            Runtime.LogVerbose($"Created template config: {path}");
        }
        else
        {
            log?.LogWarning($"Replacing invalid or outdated template config: {path}");
        }
    }

    private static bool ShouldRewriteTemplate(string path, out string reason)
    {
        reason = "missing";
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            string existing = File.ReadAllText(path, Encoding.UTF8);
            if (!IsJsoncParseable(existing))
            {
                reason = "invalid";
                return true;
            }

            if (!existing.Contains($"CoordinateTriggerEvents {PluginInfo.VERSION}", StringComparison.Ordinal))
            {
                reason = "outdated";
                return true;
            }
        }
        catch
        {
            reason = "invalid";
            return true;
        }

        reason = "current";
        return false;
    }

    private static bool IsJsoncParseable(string content)
    {
        try
        {
            JsonDocumentOptions options = new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using JsonDocument _ = JsonDocument.Parse(content, options);
            return true;
        }
        catch
        {
            return false;
        }
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

        if (root.TryGetProperty("HudInteractTriggers", out JsonElement hudInteractTriggers) && hudInteractTriggers.ValueKind == JsonValueKind.Array)
        {
            result.HudInteractTriggers = ReadHudInteractTriggerArray(hudInteractTriggers);
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
        foreach (HudInteractTriggerRule trigger in doc.HudInteractTriggers) Check("HudInteract", trigger.ID);
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
        return mode == "anyplayerinside" || mode == "allplayersinside" || mode == "anyplayeroutside" || mode == "allplayersoutside";
    }

    private static bool IsContinuousScanTriggerMode(string triggerMode)
    {
        string mode = Runtime.NormalizeTriggerMode(triggerMode);
        return mode == "onallplayersinsidescan";
    }

    private static bool IsContinuousInteractTriggerMode(string targetType, string triggerMode)
    {
        string target = Runtime.NormalizeTargetType(targetType);
        string mode = Runtime.NormalizeInteractionTriggerMode(triggerMode);
        return target == "bigpickup" && (mode == "onbigpickupheld" || mode == "onbigpickupplaced");
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
                WaypointIcon = GetString(element, "WaypointIcon", string.Empty),
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
                WorldEventObjectFilter = GetString(element, "WorldEventObjectFilter", GetString(element, "Filter", GetString(element, "ObjectFilter", string.Empty))),
                DataBlockID = GetUInt(element, "DataBlockID", GetUInt(element, "ItemDataBlockID", GetUInt(element, "ItemID", 0u))),
                ItemID = GetUInt(element, "ItemID", GetUInt(element, "PickupID", 0u)),
                InternalName = GetString(element, "InternalName", GetString(element, "PrefabName", string.Empty)),
                Radius = GetFloat(element, "Radius", 2.0f),
                UseTriggerArea = GetBool(element, "UseTriggerArea", false),
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

            rule.InteractTriggerArea = rule.UseTriggerArea ? ReadInteractTriggerArea(element, rule.ID) : null;

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
            ReadPlayerTriggerEventsObject(element, rule);

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

    private static List<HudInteractTriggerRule> ReadHudInteractTriggerArray(JsonElement array)
    {
        List<HudInteractTriggerRule> list = new();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            HudInteractTriggerRule rule = new HudInteractTriggerRule
            {
                ID = GetString(element, "ID", GetString(element, "Id", GetString(element, "id", string.Empty))),
                Enabled = GetBool(element, "Enabled", true),
                Radius = GetFloat(element, "Radius", 2.0f),
                HudText = GetString(element, "HudText", GetString(element, "InteractionText", GetString(element, "Text", "Interact"))),
                ProgressText = GetString(element, "ProgressText", string.Empty),
                HoldTime = Math.Max(0.0f, GetFloat(element, "HoldTime", GetFloat(element, "InteractDuration", GetFloat(element, "Duration", 2.0f)))),
                Cooldown = Math.Max(0.0f, GetFloat(element, "Cooldown", 1.0f)),
                RequireInExpedition = GetBool(element, "RequireInExpedition", true),
                RequireAlivePlayer = GetBool(element, "RequireAlivePlayer", true),
                HostValidateDistance = GetBool(element, "HostValidateDistance", true),
                DebugVisible = GetBool(element, "DebugVisible", false),
                DebugColor = GetString(element, "DebugColor", "#00BFFF"),
                DebugAlpha = Mathf.Clamp01(GetFloat(element, "DebugAlpha", 0.35f)),
                DebugLabel = GetBool(element, "DebugLabel", true)
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

            rule.ProgressEvents = ReadHudInteractProgressEvents(element);
            rule.CancelEvents = ReadFirstArrayProperty(element, "CancelEvents", "EventsOnCancel");
            rule.Events = ReadFirstArrayProperty(element, "Events", "CompleteEvents", "EventsOnComplete");
            rule.WardenEvents = ReadFirstArrayProperty(element, "WardenEvents", "CompleteWardenEvents");

            if (string.IsNullOrWhiteSpace(rule.ID))
            {
                Runtime.Log?.LogError("CTE config error: HUD interact trigger is missing required ID.");
                continue;
            }

            if (rule.Position == null)
            {
                Runtime.Log?.LogError($"CTE config error: HUD interact trigger '{rule.ID}' is missing Position.");
                continue;
            }

            list.Add(rule);
        }
        return list;
    }

    private static List<HudInteractProgressEventRule> ReadHudInteractProgressEvents(JsonElement element)
    {
        List<HudInteractProgressEventRule> result = new();
        if (!element.TryGetProperty("ProgressEvents", out JsonElement progressEvents))
        {
            return result;
        }

        if (progressEvents.ValueKind == JsonValueKind.Object)
        {
            if (progressEvents.TryGetProperty("Progress", out _) || progressEvents.TryGetProperty("Events", out _) || progressEvents.TryGetProperty("WardenEvents", out _))
            {
                AddHudInteractProgressEvent(result, progressEvents);
            }
            else
            {
                foreach (JsonProperty property in progressEvents.EnumerateObject())
                {
                    if (!float.TryParse(property.Name, out float progress) || property.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    result.Add(new HudInteractProgressEventRule
                    {
                        Progress = NormalizeHudInteractProgress(progress),
                        Events = property.Value.EnumerateArray().Select(e => e.Clone()).ToList()
                    });
                }
            }
        }
        else if (progressEvents.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in progressEvents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                AddHudInteractProgressEvent(result, item);
            }
        }

        return result.OrderBy(e => e.Progress).ToList();
    }

    private static void AddHudInteractProgressEvent(List<HudInteractProgressEventRule> result, JsonElement item)
    {
        result.Add(new HudInteractProgressEventRule
        {
            Progress = NormalizeHudInteractProgress(GetFloat(item, "Progress", -1.0f)),
            Events = ReadFirstArrayProperty(item, "Events", "WardenEvents")
        });
    }

    private static float NormalizeHudInteractProgress(float progress)
    {
        return progress < 0.0f ? -1.0f : Math.Clamp(progress, 0.0f, 1.0f);
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

    private static PositionTriggerRule? ReadInteractTriggerArea(JsonElement obj, string ownerID)
    {
        if (!TryGetObjectProperty(obj, out JsonElement area,
            "TriggerArea", "InteractTriggerArea", "OverrideArea", "AreaFilter", "TriggerAreaFilter"))
        {
            return null;
        }

        PositionTriggerRule rule = new PositionTriggerRule
        {
            ID = string.IsNullOrWhiteSpace(ownerID) ? "InteractTriggerArea" : ownerID + "::InteractTriggerArea",
            Enabled = GetBool(area, "Enabled", true),
            TriggerAreaMode = GetInt(area, "Count", GetInt(area, "AreaIndex", -1)) < 0 ? "OverrideBigZone" : "OverrideArea",
            Radius = GetFloat(area, "Radius", 2.0f),
            LocalIndex = GetInt(area, "LocalIndex", GetInt(area, "ZoneLocalIndex", -1)),
            Count = GetInt(area, "Count", GetInt(area, "AreaIndex", -1)),
            Layer = GetString(area, "Layer", string.Empty),
            DimensionIndex = GetInt(area, "DimensionIndex", -1),
            RequireAlivePlayers = false,
            DebugVisible = false
        };

        if (area.TryGetProperty("Position", out JsonElement pos) && pos.ValueKind == JsonValueKind.Object)
        {
            rule.Position = new PositionData
            {
                x = GetFloat(pos, "x", GetFloat(pos, "X", 0f)),
                y = GetFloat(pos, "y", GetFloat(pos, "Y", 0f)),
                z = GetFloat(pos, "z", GetFloat(pos, "Z", 0f))
            };
        }
        else if (HasProperty(area, "x", "X", "y", "Y", "z", "Z"))
        {
            rule.Position = new PositionData
            {
                x = GetFloat(area, "x", GetFloat(area, "X", 0f)),
                y = GetFloat(area, "y", GetFloat(area, "Y", 0f)),
                z = GetFloat(area, "z", GetFloat(area, "Z", 0f))
            };
        }

        return rule.Enabled ? rule : null;
    }

    private static bool TryGetObjectProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void ReadPlayerTriggerEventsObject(JsonElement obj, InteractTriggerRule rule)
    {
        if (obj.TryGetProperty("PlayerTriggerEvents", out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<JsonElement> events = property.Value.EnumerateArray().Select(e => e.Clone()).ToList();
                string key = property.Name.Trim().ToLowerInvariant();
                if (key == "1" || key == "p0" || key == "player1" || key == "slot1") rule.OnePlayerTriggerEvents = events;
                else if (key == "2" || key == "p1" || key == "player2" || key == "slot2") rule.TwoPlayerTriggerEvents = events;
                else if (key == "3" || key == "p2" || key == "player3" || key == "slot3") rule.ThreePlayerTriggerEvents = events;
                else if (key == "4" || key == "p3" || key == "player4" || key == "slot4") rule.FourPlayerTriggerEvents = events;
            }
        }

        if (rule.OnePlayerTriggerEvents.Count == 0)
        {
            rule.OnePlayerTriggerEvents = ReadFirstArrayProperty(obj, "OnePlayerTriggerEvents", "Player1TriggerEvents", "P0TriggerEvents");
        }
        if (rule.TwoPlayerTriggerEvents.Count == 0)
        {
            rule.TwoPlayerTriggerEvents = ReadFirstArrayProperty(obj, "TwoPlayerTriggerEvents", "Player2TriggerEvents", "P1TriggerEvents");
        }
        if (rule.ThreePlayerTriggerEvents.Count == 0)
        {
            rule.ThreePlayerTriggerEvents = ReadFirstArrayProperty(obj, "ThreePlayerTriggerEvents", "Player3TriggerEvents", "P2TriggerEvents");
        }
        if (rule.FourPlayerTriggerEvents.Count == 0)
        {
            rule.FourPlayerTriggerEvents = ReadFirstArrayProperty(obj, "FourPlayerTriggerEvents", "Player4TriggerEvents", "P3TriggerEvents");
        }
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

    private static string CreateChinesePositionTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - 位置触发器默认模板。
  // Enabled：控制整个文件是否生效；false 表示本文件只是示例模板，不会加载其中的触发器。
  "Enabled": false,
  // MainLevelLayoutIDs：指定生效的关卡 LayoutPersistentID/名称；0 是占位值，实际使用时请改成目标关卡。
  "MainLevelLayoutIDs": 0,
  // Debug：位置触发器的可视化调试设置。
  "Debug": {
    // Enabled：true 时启用调试显示。
    "Enabled": true,
    // ShowScanMarkers：true 时显示触发范围标记。
    "ShowScanMarkers": true,
    // ShowNames：true 时在调试标记上显示触发器 ID。
    "ShowNames": true
  },
  // PositionTriggers：坐标/区域触发器列表。
  "PositionTriggers": [
    {
      // ID：触发器唯一 ID，可被 Type 700 的 WorldEventObjectFilter 用来开启/关闭。
      "ID": "position_radius_any_player_enter",
      // Enabled：控制此单个触发器是否启用。
      "Enabled": true,
      // TriggerAreaMode：触发区域模式，可填 Radius / OverrideBigZone / OverrideArea。
      "TriggerAreaMode": "Radius",
      // Position：Radius 模式下的世界坐标中心点。
      "Position": { "x": 0, "y": 0, "z": 0 },
      // Radius：Radius 模式下的触发半径，单位为游戏世界距离。
      "Radius": 5.0,
      // DimensionIndex：OverrideBigZone/OverrideArea 使用的维度索引；-1 表示不按此字段筛选。
      "DimensionIndex": -1,
      // Layer：OverrideBigZone/OverrideArea 使用的层级；空字符串表示不按此字段筛选。
      "Layer": "",
      // LocalIndex：OverrideBigZone/OverrideArea 使用的大区 LocalIndex；-1 表示不按此字段筛选。
      "LocalIndex": -1,
      // Count：OverrideArea 使用的小区索引；0=Area_a，1=Area_b，-1 表示该大区内所有小区。
      "Count": -1,
      // TriggerMode：触发模式，可填 AnyPlayerEnter / AnyPlayerInside / AnyPlayerExit / AnyPlayerOutside / AllPlayersEnter / AllPlayersInside / AllPlayersExit / AllPlayersOutside。
      // AnyPlayerOutside：触发器曾经被玩家进入后，只要任意有效玩家在触发范围外就按 Cooldown 持续触发。
      // AllPlayersOutside：触发器曾经被玩家进入后，所有有效玩家都在触发范围外才按 Cooldown 持续触发。
      "TriggerMode": "AnyPlayerEnter",
      // Cooldown：触发冷却时间，单位秒；Inside 持续触发类模式建议不低于 1。
      "Cooldown": 1.0,
      // UsePlayerCountEvents：true 时根据区域内玩家人数触发 PlayerCountEvents。
      "UsePlayerCountEvents": false,
      // PlayerCountEvents：按区域内玩家人数 1/2/3/4 分流执行的事件组。
      "PlayerCountEvents": {
        // 1：区域内有 1 名玩家时执行的事件组。
        "1": [],
        // 2：区域内有 2 名玩家时执行的事件组。
        "2": [],
        // 3：区域内有 3 名玩家时执行的事件组。
        "3": [],
        // 4：区域内有 4 名玩家时执行的事件组。
        "4": []
      },
      // UseTriggerCycleEvents：true 时按成功触发次数累计触发 TriggerCycleEvents。
      "UseTriggerCycleEvents": false,
      // TriggerCycleCount：累计多少次成功触发后额外执行 TriggerCycleEvents。
      "TriggerCycleCount": 3,
      // TriggerCycleEvents：达到 TriggerCycleCount 后额外执行的事件组。
      "TriggerCycleEvents": [],
      // Events：满足 TriggerMode 条件后执行的主事件组。
      "Events": [
        // Type：事件类型；700 是 CTE 自定义的触发器开关事件。
        { "Type": 700, "WorldEventObjectFilter": "position_radius_any_player_enter", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateEnglishPositionTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - PositionTriggers default template.
  // Enabled: controls whether this whole file is loaded; false makes this file a disabled example.
  "Enabled": false,
  // MainLevelLayoutIDs: target LayoutPersistentID/name; 0 is a placeholder for the actual expedition layout.
  "MainLevelLayoutIDs": 0,
  // Debug: visual debug settings for position triggers.
  "Debug": {
    // Enabled: true enables debug display.
    "Enabled": true,
    // ShowScanMarkers: true shows trigger range markers.
    "ShowScanMarkers": true,
    // ShowNames: true shows trigger IDs above debug markers.
    "ShowNames": true
  },
  // PositionTriggers: list of coordinate/area triggers.
  "PositionTriggers": [
    {
      // ID: unique trigger ID, also used by Type 700 WorldEventObjectFilter.
      "ID": "position_radius_any_player_enter",
      // Enabled: controls this single trigger.
      "Enabled": true,
      // TriggerAreaMode: area mode, allowed values are Radius / OverrideBigZone / OverrideArea.
      "TriggerAreaMode": "Radius",
      // Position: world-space center used by Radius mode.
      "Position": { "x": 0, "y": 0, "z": 0 },
      // Radius: trigger radius in world units for Radius mode.
      "Radius": 5.0,
      // DimensionIndex: dimension filter for OverrideBigZone/OverrideArea; -1 disables this filter.
      "DimensionIndex": -1,
      // Layer: layer filter for OverrideBigZone/OverrideArea; empty string disables this filter.
      "Layer": "",
      // LocalIndex: zone LocalIndex filter for OverrideBigZone/OverrideArea; -1 disables this filter.
      "LocalIndex": -1,
      // Count: area index for OverrideArea; 0=Area_a, 1=Area_b, -1 means every area in the zone.
      "Count": -1,
      // TriggerMode: allowed values are AnyPlayerEnter / AnyPlayerInside / AnyPlayerExit / AnyPlayerOutside / AllPlayersEnter / AllPlayersInside / AllPlayersExit / AllPlayersOutside.
      // AnyPlayerOutside: after this trigger has been entered once, continues while outside when any eligible player is outside the trigger area.
      // AllPlayersOutside: after this trigger has been entered once, continues while outside when all eligible players are outside the trigger area.
      "TriggerMode": "AnyPlayerEnter",
      // Cooldown: trigger cooldown in seconds; Inside repeat modes should generally be at least 1.
      "Cooldown": 1.0,
      // UsePlayerCountEvents: true enables PlayerCountEvents by player count inside the area.
      "UsePlayerCountEvents": false,
      // PlayerCountEvents: event groups selected by player count 1/2/3/4 inside the area.
      "PlayerCountEvents": {
        // 1: events for one player inside.
        "1": [],
        // 2: events for two players inside.
        "2": [],
        // 3: events for three players inside.
        "3": [],
        // 4: events for four players inside.
        "4": []
      },
      // UseTriggerCycleEvents: true fires TriggerCycleEvents after repeated successful triggers.
      "UseTriggerCycleEvents": false,
      // TriggerCycleCount: number of successful triggers required before TriggerCycleEvents fires.
      "TriggerCycleCount": 3,
      // TriggerCycleEvents: extra events fired when TriggerCycleCount is reached.
      "TriggerCycleEvents": [],
      // Events: main event group fired when TriggerMode conditions are met.
      "Events": [
        // Type: event type; 700 is CTE's custom trigger enable/disable event.
        { "Type": 700, "WorldEventObjectFilter": "position_radius_any_player_enter", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateChineseScanTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - 扫描点触发器默认模板。
  // Enabled：控制整个文件是否生效；false 表示本文件只是示例模板。
  "Enabled": false,
  // MainLevelLayoutIDs：指定生效的关卡 LayoutPersistentID/名称；0 是占位值。
  "MainLevelLayoutIDs": 0,
  // ScanTriggers：附加到安全扫描点上的触发器列表。
  "ScanTriggers": [
    {
      // ID：触发器唯一 ID，可被 Type 700 的 WorldEventObjectFilter 用来开启/关闭。
      "ID": "scan_activated",
      // Enabled：控制此单个扫描点触发器是否启用。
      "Enabled": true,
      // Index：扫描点索引，对应 SPO/本插件解析到的扫描点编号。
      "Index": 0,
      // TriggerMode：可填 OnScanActivated / OnPlayerExitScan / OnAllPlayersEnterScan / OnAllPlayersInsideScan / OnAllPlayersExitScan。
      "TriggerMode": "OnScanActivated",
      // Cooldown：触发冷却时间，单位秒；Inside 持续触发类模式建议不低于 1。
      "Cooldown": 1.0,
      // UsePlayerCountEvents：true 时根据扫描点内玩家人数触发 PlayerCountEvents。
      "UsePlayerCountEvents": false,
      // PlayerCountEvents：按扫描点内玩家人数 1/2/3/4 分流执行的事件组。
      "PlayerCountEvents": {
        // 1：扫描点内有 1 名玩家时执行的事件组。
        "1": [],
        // 2：扫描点内有 2 名玩家时执行的事件组。
        "2": [],
        // 3：扫描点内有 3 名玩家时执行的事件组。
        "3": [],
        // 4：扫描点内有 4 名玩家时执行的事件组。
        "4": []
      },
      // UseTriggerCycleEvents：true 时按成功触发次数累计触发 TriggerCycleEvents。
      "UseTriggerCycleEvents": false,
      // TriggerCycleCount：累计多少次成功触发后额外执行 TriggerCycleEvents。
      "TriggerCycleCount": 3,
      // TriggerCycleEvents：达到 TriggerCycleCount 后额外执行的事件组。
      "TriggerCycleEvents": [],
      // Events：满足 TriggerMode 条件后执行的主事件组。
      "Events": [
        // Type：事件类型；700 是 CTE 自定义的触发器开关事件。
        { "Type": 700, "WorldEventObjectFilter": "scan_activated", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateEnglishScanTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - ScanTriggers default template.
  // Enabled: controls whether this whole file is loaded; false makes this file a disabled example.
  "Enabled": false,
  // MainLevelLayoutIDs: target LayoutPersistentID/name; 0 is a placeholder.
  "MainLevelLayoutIDs": 0,
  // ScanTriggers: trigger list attached to security scans.
  "ScanTriggers": [
    {
      // ID: unique trigger ID, also used by Type 700 WorldEventObjectFilter.
      "ID": "scan_activated",
      // Enabled: controls this single scan trigger.
      "Enabled": true,
      // Index: scan index resolved by SPO/CTE runtime scan lookup.
      "Index": 0,
      // TriggerMode: allowed values are OnScanActivated / OnPlayerExitScan / OnAllPlayersEnterScan / OnAllPlayersInsideScan / OnAllPlayersExitScan.
      "TriggerMode": "OnScanActivated",
      // Cooldown: trigger cooldown in seconds; Inside repeat mode should generally be at least 1.
      "Cooldown": 1.0,
      // UsePlayerCountEvents: true enables PlayerCountEvents by player count inside the scan.
      "UsePlayerCountEvents": false,
      // PlayerCountEvents: event groups selected by player count 1/2/3/4 inside the scan.
      "PlayerCountEvents": {
        // 1: events for one player inside the scan.
        "1": [],
        // 2: events for two players inside the scan.
        "2": [],
        // 3: events for three players inside the scan.
        "3": [],
        // 4: events for four players inside the scan.
        "4": []
      },
      // UseTriggerCycleEvents: true fires TriggerCycleEvents after repeated successful triggers.
      "UseTriggerCycleEvents": false,
      // TriggerCycleCount: number of successful triggers required before TriggerCycleEvents fires.
      "TriggerCycleCount": 3,
      // TriggerCycleEvents: extra events fired when TriggerCycleCount is reached.
      "TriggerCycleEvents": [],
      // Events: main event group fired when TriggerMode conditions are met.
      "Events": [
        // Type: event type; 700 is CTE's custom trigger enable/disable event.
        { "Type": 700, "WorldEventObjectFilter": "scan_activated", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateChineseBigPickupTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - 大物品触发器默认模板。
  // Enabled：控制整个文件是否生效；false 表示本文件只是示例模板。
  "Enabled": false,
  // MainLevelLayoutIDs：指定生效的关卡 LayoutPersistentID/名称；0 是占位值。
  "MainLevelLayoutIDs": 0,
  // InteractTriggers：交互类触发器列表；此文件用于 BigPickup。
  "InteractTriggers": [
    {
      // ID：触发器唯一 ID，可被 Type 700 的 WorldEventObjectFilter 用来开启/关闭。
      "ID": "bigpickup_pickup",
      // Enabled：控制此单个大物品触发器是否启用。
      "Enabled": true,
      // TargetType：目标类型；大物品触发器固定使用 BigPickup。
      "WaypointIcon": "",
      "TargetType": "BigPickup",
      // TriggerMode：可填 OnBigPickupPickup / OnBigPickupHeld / OnBigPickupDrop / OnBigPickupPlaced。
      "TriggerMode": "OnBigPickupPickup",
      // Index：大物品索引；-1 表示不按索引筛选。
      "Index": 0,
      // Cooldown：触发冷却时间，单位秒；Held/Placed 持续类模式建议不低于 1。
      "Cooldown": 1.0,
      // UseTriggerArea：false 表示全局响应；true 表示 Events 和 PlayerTriggerEvents 只在 TriggerArea 内触发。
      "UseTriggerArea": true,
      // TriggerArea：限制大物品触发事件生效的区域。
      "TriggerArea": {
        // DimensionIndex：维度索引，通常主维度为 0。
        "DimensionIndex": 0,
        // Layer：层级索引，通常 MainLayer 为 0。
        "Layer": 0,
        // LocalIndex：大区 LocalIndex。
        "LocalIndex": 0,
        // Count：小区索引；0=Area_a，1=Area_b，-1 表示该大区内所有小区。
        "Count": -1
      },
      // PlayerTriggerEvents：按拾取/放下玩家槽位 1/2/3/4 分流执行的事件组。
      "PlayerTriggerEvents": {
        // 1：1 号槽位玩家触发时执行的事件组。
        "1": [],
        // 2：2 号槽位玩家触发时执行的事件组。
        "2": [],
        // 3：3 号槽位玩家触发时执行的事件组。
        "3": [],
        // 4：4 号槽位玩家触发时执行的事件组。
        "4": []
      },
      // UsePickupDropCycleEvents：true 时按“拾取->放下”完整循环累计触发 PickupDropCycleEvents。
      "UsePickupDropCycleEvents": false,
      // PickupDropCycleCount：累计多少个拾取/放下循环后额外执行 PickupDropCycleEvents。
      "PickupDropCycleCount": 1,
      // PickupDropCycleEvents：达到 PickupDropCycleCount 后额外执行的事件组。
      "PickupDropCycleEvents": [],
      // Events：满足 TriggerMode 条件后执行的通用事件组。
      "Events": [
        // Type：事件类型；700 是 CTE 自定义的触发器开关事件。
        { "Type": 700, "WorldEventObjectFilter": "bigpickup_held_repeat", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateEnglishBigPickupTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - BigPickup default template.
  // Enabled: controls whether this whole file is loaded; false makes this file a disabled example.
  "Enabled": false,
  // MainLevelLayoutIDs: target LayoutPersistentID/name; 0 is a placeholder.
  "MainLevelLayoutIDs": 0,
  // InteractTriggers: interaction trigger list; this file is for BigPickup.
  "InteractTriggers": [
    {
      // ID: unique trigger ID, also used by Type 700 WorldEventObjectFilter.
      "ID": "bigpickup_pickup",
      // Enabled: controls this single big-pickup trigger.
      "Enabled": true,
      // TargetType: target type; BigPickup triggers use BigPickup.
      "WaypointIcon": "",
      "TargetType": "BigPickup",
      // TriggerMode: allowed values are OnBigPickupPickup / OnBigPickupHeld / OnBigPickupDrop / OnBigPickupPlaced.
      "TriggerMode": "OnBigPickupPickup",
      // Index: big pickup index; -1 disables index filtering.
      "Index": 0,
      // Cooldown: trigger cooldown in seconds; Held/Placed repeat modes should generally be at least 1.
      "Cooldown": 1.0,
      // UseTriggerArea: false means global response; true means Events and PlayerTriggerEvents only fire inside TriggerArea.
      "UseTriggerArea": true,
      // TriggerArea: area restriction for big-pickup events.
      "TriggerArea": {
        // DimensionIndex: dimension index, usually 0 for the main dimension.
        "DimensionIndex": 0,
        // Layer: layer index, usually 0 for MainLayer.
        "Layer": 0,
        // LocalIndex: zone LocalIndex.
        "LocalIndex": 0,
        // Count: area index; 0=Area_a, 1=Area_b, -1 means every area in this zone.
        "Count": -1
      },
      // PlayerTriggerEvents: event groups selected by triggering player slot 1/2/3/4.
      "PlayerTriggerEvents": {
        // 1: events for player slot 1.
        "1": [],
        // 2: events for player slot 2.
        "2": [],
        // 3: events for player slot 3.
        "3": [],
        // 4: events for player slot 4.
        "4": []
      },
      // UsePickupDropCycleEvents: true fires PickupDropCycleEvents after full pickup->drop cycles.
      "UsePickupDropCycleEvents": false,
      // PickupDropCycleCount: number of pickup/drop cycles required before PickupDropCycleEvents fires.
      "PickupDropCycleCount": 1,
      // PickupDropCycleEvents: extra events fired when PickupDropCycleCount is reached.
      "PickupDropCycleEvents": [],
      // Events: shared event group fired when TriggerMode conditions are met.
      "Events": [
        // Type: event type; 700 is CTE's custom trigger enable/disable event.
        { "Type": 700, "WorldEventObjectFilter": "bigpickup_held_repeat", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateChineseHudInteractTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - HUD 交互触发器默认模板。
  // Enabled：控制整个文件是否生效；false 表示本文件只是示例模板。
  "Enabled": false,
  // MainLevelLayoutIDs：指定生效的关卡 LayoutPersistentID/名称；0 是占位值。
  "MainLevelLayoutIDs": 0,
  // HudInteractTriggers：地图坐标 HUD 交互触发点列表。
  "HudInteractTriggers": [
    {
      // ID：触发器唯一 ID，可被 Type 700 的 WorldEventObjectFilter 用来开启/关闭。
      "ID": "hud_interact_01",
      // Enabled：控制此单个 HUD 交互触发点是否启用。
      "Enabled": true,
      // Position：HUD 交互触发点的世界坐标。
      "Position": { "x": 0, "y": 0, "z": 0 },
      // Radius：原生交互点可选中的半径，影响玩家靠近后是否能看到/选中 HUD。
      "Radius": 0.5,
      // HudText：屏幕下方交互 HUD 文本；支持 Unity/TMP 富文本颜色标签，例如 <color=#00BFFF>Text</color>。
      "HudText": "<color=#00BFFF>Read data</color>",
      // HoldTime：按住交互键的读条总时长，单位秒。
      "HoldTime": 3.0,
      // Cooldown：完成读条后再次允许触发 Events 的冷却时间，单位秒。
      "Cooldown": 1.0,
      // DebugColor：调试可视化标记颜色，支持 #RRGGBB / #RRGGBBAA。
      "DebugColor": "#00BFFF",
      // DebugAlpha：调试可视化标记透明度，0=完全透明，1=不透明。
      "DebugAlpha": 0.35,
      // DebugLabel：true 时在调试标记上显示 ID 标签。
      "DebugLabel": true,
      // ProgressEvents：SPO 风格进度事件；Progress 可填 0-1 或 0-100，-1 表示禁用此占位示例。
      "ProgressEvents": {
        // Progress：触发进度点；0.5/50 表示 50%，1/100 表示完成。
        "Progress": -1,
        // Events：达到 Progress 后执行的事件组。
        "Events": []
      },
      // CancelEvents：玩家已开始按住交互键但中途放弃时执行；正常完成读条不会触发。
      "CancelEvents": [],
      // Events：读条完成后执行的事件组。
      "Events": [
        // Type：事件类型；700 是 CTE 自定义的触发器开关事件。
        { "Type": 700, "WorldEventObjectFilter": "hud_interact_01", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }

    private static string CreateEnglishHudInteractTemplateJson()
    {
        return """
{
  // CoordinateTriggerEvents 1.7.0 - HudInteractTriggers default template.
  // Enabled: controls whether this whole file is loaded; false makes this file a disabled example.
  "Enabled": false,
  // MainLevelLayoutIDs: target LayoutPersistentID/name; 0 is a placeholder.
  "MainLevelLayoutIDs": 0,
  // HudInteractTriggers: list of coordinate-based HUD interaction points.
  "HudInteractTriggers": [
    {
      // ID: unique trigger ID, also used by Type 700 WorldEventObjectFilter.
      "ID": "hud_interact_01",
      // Enabled: controls this single HUD interaction point.
      "Enabled": true,
      // Position: world-space position of this HUD interaction point.
      "Position": { "x": 0, "y": 0, "z": 0 },
      // Radius: selectable radius of the native interaction point, affecting whether nearby players can see/select the HUD.
      "Radius": 0.5,
      // HudText: bottom interaction HUD text; Unity/TMP rich text color tags are supported, for example <color=#00BFFF>Text</color>.
      "HudText": "<color=#00BFFF>Read data</color>",
      // HoldTime: total hold-interact progress duration, in seconds.
      "HoldTime": 3.0,
      // Cooldown: seconds before completed Events may fire again.
      "Cooldown": 1.0,
      // DebugColor: debug marker color, supports #RRGGBB / #RRGGBBAA.
      "DebugColor": "#00BFFF",
      // DebugAlpha: debug marker opacity, 0=transparent and 1=opaque.
      "DebugAlpha": 0.35,
      // DebugLabel: true shows the trigger ID label on the debug marker.
      "DebugLabel": true,
      // ProgressEvents: SPO-style progress event; Progress accepts 0-1 or 0-100, and -1 disables this placeholder example.
      "ProgressEvents": {
        // Progress: progress point; 0.5/50 means 50%, 1/100 means complete.
        "Progress": -1,
        // Events: event group fired when Progress is reached.
        "Events": []
      },
      // CancelEvents: fires when the player starts holding interact and then abandons it; normal completion does not fire this list.
      "CancelEvents": [],
      // Events: event group fired after the hold completes.
      "Events": [
        // Type: event type; 700 is CTE's custom trigger enable/disable event.
        { "Type": 700, "WorldEventObjectFilter": "hud_interact_01", "Enabled": false }
      ]
    }
  ]
}
""".Replace("CoordinateTriggerEvents 1.7.0", $"CoordinateTriggerEvents {PluginInfo.VERSION}");
    }




















}
























