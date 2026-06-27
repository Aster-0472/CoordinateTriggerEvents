using System;
using System.Text.Json;

namespace CoordinateTriggerEvents;

internal static partial class Runtime
{
    private const int TriggerControlEventType = 700;

    private static bool IsTriggerControlEvent(JsonElement element)
    {
        return TryGetTriggerControlEventType(element, out int type) && type == TriggerControlEventType;
    }

    private static bool TryHandleTriggerControlEvent(JsonElement element, string ownerLabel)
    {
        if (!IsTriggerControlEvent(element))
        {
            return false;
        }

        bool enable = GetBool(element, "Enabled", GetBool(element, "Enable", true));
        string targetID = GetString(element, "TargetID", GetString(element, "TriggerID", GetString(element, "ID", string.Empty)));
        string category = GetString(element, "Category", GetString(element, "TargetType", GetString(element, "TriggerCategory", "Any")));
        if (string.IsNullOrWhiteSpace(targetID))
        {
            Log?.LogWarning($"{ownerLabel} trigger-control event Type={TriggerControlEventType} has no TargetID/TriggerID.");
            return true;
        }

        int changed = ApplyTriggerControlState(targetID, category, enable);
        LogVerbose($"{ownerLabel} trigger-control event Type={TriggerControlEventType} set Enabled={enable} for TargetID='{targetID}', Category='{category}', Changed={changed}.");
        return true;
    }

    private static bool TryGetTriggerControlEventType(JsonElement element, out int type)
    {
        type = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetTriggerControlEventTypeProperty(element, "Type", out type)
            || TryGetTriggerControlEventTypeProperty(element, "EventType", out type);
    }

    private static bool TryGetTriggerControlEventTypeProperty(JsonElement element, string name, out int type)
    {
        type = 0;
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out type))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out type))
        {
            return true;
        }

        return false;
    }

    private static int ApplyTriggerControlState(string targetID, string category, bool enabled)
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

            if ((matchAnyCategory || normalizedCategory == "hudinteract" || normalizedCategory == "hudinteraction") && config.HudInteractTriggers != null)
            {
                foreach (HudInteractTriggerRule trigger in config.HudInteractTriggers)
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
}
