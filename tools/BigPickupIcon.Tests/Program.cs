using System.Reflection;
using CoordinateTriggerEvents;
using UnityEngine;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

var marker = new NavMarker { Pointer = 10, IsVisible = true, m_currentState = NavMarkerState.Visible };
var item = new CarryItemPickup_Core();
var placer = new PlaceNavMarkerOnGO { Pointer = 20, ParentItem = item, m_marker = marker };
item.m_navMarkerPlacer = placer;
marker.m_trackingObj = new GameObject { ParentItem = item };
var rule = new InteractTriggerRule { TargetType = "BigPickup", WaypointIcon = "Ammo" };
Runtime.Rules.Add((new ConfigDocument(), rule));
NavMarkerOption original = NavMarkerOption.Waypoint | NavMarkerOption.Distance | NavMarkerOption.Title | NavMarkerOption.Ping;
NavMarkerOption expected = NavMarkerOption.Ammo | NavMarkerOption.Distance | NavMarkerOption.Title | NavMarkerOption.Ping;
NavMarkerOption Apply(NavMarkerOption options)
{
    BigPickupWaypointIcon.Replace(marker, ref options);
    return options;
}
Check(Apply(original) == expected, "Only icon bits may change");
Check(Runtime.IgnoreTriggerMode, "Matching must not depend on trigger event mode");
foreach (NavMarkerOption value in Enum.GetValues<NavMarkerOption>())
{
    string name = value.ToString();
    bool supported = BigPickupWaypointIcon.TryParse(name, out NavMarkerOption parsed);
    if (!supported) continue;
    Check(parsed == value, "Enum name parse " + name);
    Check(BigPickupWaypointIcon.TryParse(name.ToLowerInvariant(), out parsed) && parsed == value, "Case-insensitive parse " + name);
    Check(BigPickupWaypointIcon.TryParse(((ulong)value).ToString(), out parsed) && parsed == value, "Numeric parse " + name);
    rule.WaypointIcon = name;
    foreach (NavMarkerOption options in Enum.GetValues<NavMarkerOption>())
    {
        NavMarkerOption changed = Apply(options);
        NavMarkerOption wanted = (options & NavMarkerOption.Waypoint) == 0 ? options : (options & ~NavMarkerOption.Waypoint) | value;
        Check(changed == wanted, "Actual implementation bitmask " + name + "/" + options);
        Check((changed & NavMarkerOption.Distance) == (options & NavMarkerOption.Distance), "Distance bit " + name);
    }
}
foreach (string invalid in new[] { "", " ", "unknown", "Distance", "Title", "Player", "PlayerInCompass", "Empty", "0", "-1", "18446744073709551616", "AmmoDistance", "AmmoTitleDistance", "Ammo, Health", "8", "9223372036854775808" })
{
    Check(!BigPickupWaypointIcon.TryParse(invalid, out _), "Invalid icon must be rejected: " + invalid);
    rule.WaypointIcon = invalid;
    Check(Apply(original) == original, "Invalid icon must preserve native options: " + invalid);
}
rule.WaypointIcon = "Ammo";
rule.TargetType = "Any";
Check(Apply(original) == original, "Other target kinds untouched");
rule.TargetType = "BigPickup";
rule.Matches = false;
Check(Apply(original) == original, "Nonmatching object untouched");
rule.Matches = true;
GameStateManager.IsInExpedition = false;
Check(Apply(original) == original, "Expedition guard");
rule.RequireInExpedition = false;
Check(Apply(original) == expected, "Explicit expedition guard override");
GameStateManager.IsInExpedition = true;
rule.RequireInExpedition = true;
marker.Pointer = 11;
var otherMarker = new NavMarker { Pointer = 10, m_trackingObj = marker.m_trackingObj };
NavMarkerOption otherOptions = original;
BigPickupWaypointIcon.Replace(otherMarker, ref otherOptions);
Check(otherOptions == original, "Unowned marker untouched");
marker.Pointer = 10;
marker.m_trackingObj.ParentItem = null;
Check(Apply(original) == original, "Other object's waypoint untouched");
marker.m_trackingObj.ParentItem = item;
marker.m_stateOptions = new[] { NavMarkerOption.Empty, original, original, NavMarkerOption.EdgeArrow | NavMarkerOption.Distance };
BigPickupWaypointIcon.Refresh(placer);
Check(marker.LastOptions == expected && marker.Calls == 1, "Creation/show refresh");
Check(marker.m_stateOptions[1] == original, "Native state array must remain unchanged");
marker.m_currentState = NavMarkerState.OnEdge;
BigPickupWaypointIcon.Refresh(placer);
Check(marker.LastOptions == (NavMarkerOption.EdgeArrow | NavMarkerOption.Distance), "Offscreen arrow preserved");
marker.m_currentState = (NavMarkerState)99;
int calls = marker.Calls;
BigPickupWaypointIcon.Refresh(placer);
Check(marker.Calls == calls, "Out-of-range state is safe");
marker.m_currentState = NavMarkerState.Visible;
marker.IsVisible = false;
BigPickupWaypointIcon.Refresh(placer);
Check(marker.Calls == calls, "Hidden markers stay hidden");
marker.IsVisible = true;
placer.Pointer = 21;
item.m_navMarkerPlacer = new PlaceNavMarkerOnGO { Pointer = 20 };
BigPickupWaypointIcon.Refresh(placer);
Check(marker.Calls == calls, "Unowned placer untouched");
item.m_navMarkerPlacer = placer;
marker.ThrowOnAccess = true;
object[] arguments = { marker, original };
typeof(BigPickupWaypointIcon_SetOptions_Patch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
Check((NavMarkerOption)arguments[1] == original, "Patch exceptions preserve native arguments");
Check(Runtime.Logs > 0, "Patch exceptions logged");
marker.ThrowOnAccess = false;
Runtime.Rules.Clear();
Check(Apply(original) == original, "No configuration is exact legacy behavior");
int placerLookups = placer.ParentLookupCount;
int trackingLookups = marker.m_trackingObj.ParentLookupCount;
BigPickupWaypointIcon.Refresh(placer);
Check(placer.ParentLookupCount == placerLookups, "No configured icon skips placer parent lookup");
BigPickupWaypointIcon.Replace(marker, ref original);
Check(marker.m_trackingObj.ParentLookupCount == trackingLookups, "No configured icon skips tracking parent lookup");
rule.WaypointIcon = "Ammo";
Runtime.Rules.Add((new ConfigDocument(), rule));
placer.ThrowOnParentLookup = true;
int loggedBeforeLookupFailure = Runtime.Logs;
BigPickupWaypointIcon.Refresh(placer);
Check(Runtime.Logs == loggedBeforeLookupFailure, "Expected unrelated placer lookup failure is suppressed");
placer.ThrowOnParentLookup = false;
marker.m_trackingObj.ThrowOnParentLookup = true;
BigPickupWaypointIcon.Replace(marker, ref original);
Check(Runtime.Logs == loggedBeforeLookupFailure, "Expected unrelated tracking lookup failure is suppressed");
Console.WriteLine($"PASS: {checks} assertions against linked production BigPickupWaypointIcon.cs; Unity/Harmony APIs are test doubles, not an in-game test.");

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string method) { }
    }
}

namespace UnityEngine
{
    public sealed class GameObject
    {
        public CarryItemPickup_Core? ParentItem;
        public bool ThrowOnParentLookup;
        public int ParentLookupCount;
        public T GetComponentInParent<T>() where T : class
        {
            ParentLookupCount++;
            if (ThrowOnParentLookup) throw new InvalidOperationException("Simulated IL2CPP parent lookup failure");
            return (ParentItem as T)!;
        }
    }
}

public sealed class CarryItemPickup_Core
{
    public PlaceNavMarkerOnGO? m_navMarkerPlacer;
}

public sealed class PlaceNavMarkerOnGO
{
    public nint Pointer;
    public CarryItemPickup_Core? ParentItem;
    public bool ThrowOnParentLookup;
    public int ParentLookupCount;
    public NavMarker m_marker = null!;
    public T GetComponentInParent<T>() where T : class
    {
        ParentLookupCount++;
        if (ThrowOnParentLookup) throw new InvalidOperationException("Simulated IL2CPP parent lookup failure");
        return (ParentItem as T)!;
    }
    public void PlaceMarker(GameObject trackingObject) { }
    public void SetMarkerVisible(bool visible) { }
}

public sealed class NavMarker
{
    public nint Pointer;
    public bool IsVisible;
    public bool ThrowOnAccess;
    public GameObject? Tracking;
    public GameObject m_trackingObj
    {
        get => ThrowOnAccess ? throw new InvalidOperationException("Simulated destroyed object") : Tracking!;
        set => Tracking = value;
    }
    public NavMarkerState m_currentState;
    public NavMarkerOption[]? m_stateOptions;
    public NavMarkerOption LastOptions;
    public int Calls;
    public void SetOptions(NavMarkerOption options)
    {
        BigPickupWaypointIcon.Replace(this, ref options);
        LastOptions = options;
        Calls++;
    }
}

public static class GameStateManager
{
    public static bool IsInExpedition = true;
}

namespace CoordinateTriggerEvents
{
    internal sealed class ConfigDocument { }
    internal sealed class InteractTriggerRule
    {
        public string TargetType = "BigPickup";
        public string WaypointIcon = "";
        public bool Enabled = true;
        public bool RequireInExpedition = true;
        public bool Matches = true;
    }
    internal static partial class Runtime
    {
        internal static readonly List<(ConfigDocument Config, InteractTriggerRule Trigger)> Rules = new();
        internal static bool IgnoreTriggerMode;
        internal static int Logs;
        internal static List<(ConfigDocument Config, InteractTriggerRule Trigger)> GetActiveInteractTriggers() => Rules;
        internal static string NormalizeTargetType(string value) => value.ToLowerInvariant();
        private static bool InteractTriggerMatches(InteractTriggerRule trigger, string sourceKind, string eventName, CarryItemPickup_Core source, bool ignoreTriggerMode)
        {
            IgnoreTriggerMode = ignoreTriggerMode;
            return trigger.Matches;
        }
        internal static void LogThrottled(string message) => Logs++;
    }
}
