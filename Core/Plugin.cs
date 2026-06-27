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

internal static class PluginInfo
{
    public const string GUID = "com.gtfo.coordinatetriggerevents";
    public const string NAME = "CoordinateTriggerEvents";
    public const string VERSION = "1.4.7";
}

[BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
[BepInDependency("dev.gtfomodding.gtfo-api", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("MTFO.Extension.PartialBlocks", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("GTFO.AWO", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Inas.ExtraObjectiveSetup", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("GTFO.InjectLib", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    private Harmony? _harmony;

    public override void Load()
    {
        Runtime.Log = Log;
        ConfigManager.LoadOrCreate(Log, true);
        Runtime.SetupTerminalTriggerSync();
        Runtime.SetupHudInteractTriggerSync();
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

