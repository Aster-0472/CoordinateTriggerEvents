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
