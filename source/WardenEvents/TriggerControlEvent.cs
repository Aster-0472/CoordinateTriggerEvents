using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GameData;

namespace CoordinateTriggerEvents;

internal static partial class Runtime
{
    private const int TriggerControlEventType = 700;
    private const string TriggerControlEventName = "CTETriggerControl";
    private static bool TriggerControlWardenEventRegistered;

    internal static void SetupTriggerControlWardenEvent()
    {
        if (TriggerControlWardenEventRegistered)
        {
            return;
        }

        try
        {
            Type? managerType = FindLoadedType("ExtraObjectiveSetup.ExtendedWardenEvents.EOSWardenEventManager") ?? FindLoadedTypeByName("EOSWardenEventManager");
            if (managerType == null)
            {
                LogVerbose($"Type {TriggerControlEventType} WardenEvent registration skipped because EOSWardenEventManager is not loaded.");
                return;
            }

            object? manager = managerType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (manager == null)
            {
                LogVerbose($"Type {TriggerControlEventType} WardenEvent registration skipped because EOSWardenEventManager.Current is null.");
                return;
            }

            MethodInfo? addDefinition = managerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "AddEventDefinition", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 3
                        && parameters[0].ParameterType == typeof(string)
                        && (parameters[1].ParameterType == typeof(uint) || parameters[1].ParameterType == typeof(int));
                });
            if (addDefinition == null)
            {
                Log?.LogWarning($"Type {TriggerControlEventType} WardenEvent registration failed: EOSWardenEventManager.AddEventDefinition was not found.");
                return;
            }

            MethodInfo? handlerMethod = typeof(Runtime).GetMethod(nameof(HandleTriggerControlWardenEvent), BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null)
            {
                Log?.LogWarning($"Type {TriggerControlEventType} WardenEvent registration failed: handler method was not found.");
                return;
            }

            ParameterInfo[] addParameters = addDefinition.GetParameters();
            object eventID = addParameters[1].ParameterType == typeof(int) ? TriggerControlEventType : (uint)TriggerControlEventType;
            Delegate handler = Delegate.CreateDelegate(addParameters[2].ParameterType, handlerMethod);
            object? result = addDefinition.Invoke(manager, new object[] { TriggerControlEventName, eventID, handler });
            TriggerControlWardenEventRegistered = result is not bool success || success;
            if (TriggerControlWardenEventRegistered)
            {
                LogVerbose($"Registered Type {TriggerControlEventType} WardenEvent '{TriggerControlEventName}' with EOSWardenEventManager.");
            }
        }
        catch (TargetInvocationException ex)
        {
            LogThrottled($"Type {TriggerControlEventType} WardenEvent registration failed: {DescribeException(ex)}");
        }
        catch (Exception ex)
        {
            LogThrottled($"Type {TriggerControlEventType} WardenEvent registration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
        string targetID = GetString(element, "TargetID", GetString(element, "TriggerID", GetString(element, "ID", GetString(element, "WorldEventObjectFilter", string.Empty))));
        string category = GetString(element, "Category", GetString(element, "TargetType", GetString(element, "TriggerCategory", "Any")));
        if (string.IsNullOrWhiteSpace(targetID))
        {
            Log?.LogWarning($"{ownerLabel} trigger-control event Type={TriggerControlEventType} has no TargetID/TriggerID.");
            TryHandleWardenIntelDirect(element, ownerLabel);
            return true;
        }

        int changed = ApplyTriggerControlState(targetID, category, enable);
        LogVerbose($"{ownerLabel} trigger-control event Type={TriggerControlEventType} set Enabled={enable} for TargetID='{targetID}', Category='{category}', Changed={changed}.");
        TryHandleWardenIntelDirect(element, ownerLabel);
        return true;
    }

    private static void HandleTriggerControlWardenEvent(WardenObjectiveEventData eventData)
    {
        try
        {
            bool enable = eventData.Enabled;
            string targetID = TryGetStringMember(eventData, "TargetID", "TriggerID", "ID", "WorldEventObjectFilter", "WorldEventObjectKey", "SpecialText", "Name");
            string category = TryGetStringMember(eventData, "Category", "TargetType", "TriggerCategory");
            if (string.IsNullOrWhiteSpace(category))
            {
                category = "Any";
            }

            if (string.IsNullOrWhiteSpace(targetID))
            {
                Log?.LogWarning($"WardenEvent Type={TriggerControlEventType} has no target ID. Use WorldEventObjectFilter/TargetID/TriggerID.");
                return;
            }

            int changed = ApplyTriggerControlState(targetID, category, enable);
            LogVerbose($"WardenEvent Type={TriggerControlEventType} set Enabled={enable} for TargetID='{targetID}', Category='{category}', Changed={changed}.");
        }
        catch (Exception ex)
        {
            LogThrottled($"WardenEvent Type={TriggerControlEventType} failed: {ex.GetType().Name}: {ex.Message}");
        }
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

            if ((matchAnyCategory || normalizedCategory == "interact" || normalizedCategory == "interaction" || normalizedCategory == "bigpickup") && config.InteractTriggers != null)
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


