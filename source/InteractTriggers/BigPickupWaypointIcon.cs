using System;
using HarmonyLib;
using UnityEngine;

namespace CoordinateTriggerEvents;

internal static partial class Runtime
{
    internal static NavMarkerOption GetBigPickupWaypointIcon(CarryItemPickup_Core item)
    {
        foreach ((ConfigDocument _, InteractTriggerRule trigger) in GetActiveInteractTriggers())
        {
            if (NormalizeTargetType(trigger.TargetType) != "bigpickup") continue;
            if (string.IsNullOrWhiteSpace(trigger.WaypointIcon)) continue;
            if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition) continue;
            if (!BigPickupWaypointIcon.TryParse(trigger.WaypointIcon, out NavMarkerOption icon)) continue;
            if (InteractTriggerMatches(trigger, "BigPickup", "OnBigPickupDrop", item, ignoreTriggerMode: true)) return icon;
        }
        return NavMarkerOption.Empty;
    }
}

internal static class BigPickupWaypointIcon
{
    private static bool HasConfiguredWaypointIcon()
    {
        foreach ((ConfigDocument _, InteractTriggerRule trigger) in Runtime.GetActiveInteractTriggers())
        {
            if (!trigger.Enabled || Runtime.NormalizeTargetType(trigger.TargetType) != "bigpickup") continue;
            if (string.IsNullOrWhiteSpace(trigger.WaypointIcon)) continue;
            if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition) continue;
            if (TryParse(trigger.WaypointIcon, out _)) return true;
        }
        return false;
    }

    internal static void Refresh(PlaceNavMarkerOnGO placer)
    {
        if (!HasConfiguredWaypointIcon()) return;
        if (!TryGetCarryItem(placer, out CarryItemPickup_Core item)) return;

        try
        {
            if (item.m_navMarkerPlacer == null || item.m_navMarkerPlacer.Pointer != placer.Pointer) return;
            NavMarker marker = placer.m_marker;
            if (marker == null || !marker.IsVisible || marker.m_stateOptions == null) return;
            int state = (int)marker.m_currentState;
            if (state < 0 || state >= marker.m_stateOptions.Length) return;
            if (Runtime.GetBigPickupWaypointIcon(item) == NavMarkerOption.Empty) return;
            marker.SetOptions(marker.m_stateOptions[state]);
        }
        catch (Exception exception)
        {
            Runtime.LogThrottled($"BigPickup WaypointIcon refresh skipped: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool TryGetCarryItem(PlaceNavMarkerOnGO placer, out CarryItemPickup_Core item)
    {
        item = null!;
        if (placer == null) return false;
        try
        {
            item = placer.GetComponentInParent<CarryItemPickup_Core>();
            return item != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetCarryItem(GameObject trackingObject, out CarryItemPickup_Core item)
    {
        item = null!;
        if (trackingObject == null) return false;
        try
        {
            item = trackingObject.GetComponentInParent<CarryItemPickup_Core>();
            return item != null;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParse(string value, out NavMarkerOption icon)
    {
        const NavMarkerOption supported = NavMarkerOption.Waypoint | NavMarkerOption.Enemy
            | NavMarkerOption.DroneDiscovery | NavMarkerOption.LookAt | NavMarkerOption.Ammo
            | NavMarkerOption.Health | NavMarkerOption.Loot | NavMarkerOption.Terminal
            | NavMarkerOption.Caution | NavMarkerOption.HSU | NavMarkerOption.Door
            | NavMarkerOption.Sign | NavMarkerOption.ResourceLocker | NavMarkerOption.ResourceBox
            | NavMarkerOption.Generator | NavMarkerOption.Disinfection | NavMarkerOption.CarryItem
            | NavMarkerOption.BulkheadMain | NavMarkerOption.BulkheadSecondary | NavMarkerOption.BulkheadThird
            | NavMarkerOption.Consumables | NavMarkerOption.ObjectivePickup | NavMarkerOption.ToolRefill
            | NavMarkerOption.SecurityDoor | NavMarkerOption.BulkheadDoor | NavMarkerOption.ApexDoor
            | NavMarkerOption.BloodDoor | NavMarkerOption.SecurityDoorCheckpoint
            | NavMarkerOption.BulkheadDoorCheckpoint | NavMarkerOption.ApexDoorCheckpoint
            | NavMarkerOption.BloodDoorCheckpoint | NavMarkerOption.BulkheadDC;
        icon = NavMarkerOption.Empty;
        if (!Enum.TryParse(value, true, out NavMarkerOption parsed)) return false;
        ulong bits = (ulong)parsed;
        if (bits == 0 || (bits & (bits - 1)) != 0 || (parsed & supported) != parsed) return false;
        icon = parsed;
        return true;
    }

    internal static void Replace(NavMarker marker, ref NavMarkerOption options)
    {
        if ((options & NavMarkerOption.Waypoint) == 0 || !HasConfiguredWaypointIcon()) return;
        if (marker == null) return;
        GameObject trackingObject = marker.m_trackingObj;
        if (!TryGetCarryItem(trackingObject, out CarryItemPickup_Core item)) return;
        if (item.m_navMarkerPlacer == null) return;
        NavMarker ownedMarker = item.m_navMarkerPlacer.m_marker;
        if (ownedMarker == null || ownedMarker.Pointer != marker.Pointer) return;
        NavMarkerOption icon = Runtime.GetBigPickupWaypointIcon(item);
        if (icon != NavMarkerOption.Empty) options = (options & ~NavMarkerOption.Waypoint) | icon;
    }
}

[HarmonyPatch(typeof(NavMarker), nameof(NavMarker.SetOptions))]
internal static class BigPickupWaypointIcon_SetOptions_Patch
{
    private static void Prefix(NavMarker __instance, ref NavMarkerOption options)
    {
        try
        {
            BigPickupWaypointIcon.Replace(__instance, ref options);
        }
        catch (Exception exception)
        {
            Runtime.LogThrottled($"BigPickup WaypointIcon skipped: {exception.GetType().Name}: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.PlaceMarker))]
internal static class BigPickupWaypointIcon_PlaceMarker_Patch
{
    private static void Postfix(PlaceNavMarkerOnGO __instance)
    {
        BigPickupWaypointIcon.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.SetMarkerVisible))]
internal static class BigPickupWaypointIcon_SetMarkerVisible_Patch
{
    private static void Postfix(PlaceNavMarkerOnGO __instance, bool visible)
    {
        if (visible) BigPickupWaypointIcon.Refresh(__instance);
    }
}
