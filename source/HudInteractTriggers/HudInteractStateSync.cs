using System;
using System.Collections.Generic;
using System.Text;
using GTFO.API;
using HarmonyLib;
using SNetwork;

namespace CoordinateTriggerEvents;

internal static class HudInteractStateSync
{
    private const string EventName = PluginInfo.GUID + ".hud_interact_state.v1";
    private const byte SnapshotRequest = 0;
    private const byte State = 1;
    private const byte HostReady = 2;
    private static readonly Dictionary<string, List<HudInteractTriggerRule>> Rules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (bool Enabled, ulong Revision)> States = new(StringComparer.OrdinalIgnoreCase);
    private static bool Registered;
    private static bool Active;
    private static uint LayoutId;
    private static ulong Epoch;
    private static ulong RequestId;
    private static ulong NextRevision;

    internal static void Setup()
    {
        if (Registered) return;
        try
        {
            NetworkAPI.RegisterEvent<HudInteractStatePacket>(EventName, Receive);
            Registered = true;
            LevelAPI.OnBuildStart += Reset;
            LevelAPI.OnLevelCleanup += Reset;
            EventAPI.OnCheckpointReloaded += RequestSnapshot;
        }
        catch (Exception exception)
        {
            Runtime.LogThrottled($"CTE HUD state registration failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static void Reset()
    {
        Active = false;
        Rules.Clear();
        States.Clear();
        LayoutId = 0;
        Epoch = 0;
        RequestId = 0;
        NextRevision = 0;
    }

    internal static void StartExpedition()
    {
        Reset();
        if (!Registered) return;
        LayoutId = Runtime.GetHudInteractStateLayoutId();
        Active = true;
        RebuildRules();
        if (Rules.Count == 0) return;
        if (SNet.IsMaster)
        {
            Epoch = NewNonce();
            InitializeHostStates();
            Send(CreatePacket(HostReady), null);
        }
        else
        {
            RequestSnapshot();
        }
    }

    internal static bool SetEnabled(HudInteractTriggerRule trigger, bool enabled)
    {
        if (Active && !SNet.IsMaster && Rules.ContainsKey(trigger.ID)) return false;
        if (trigger.Enabled == enabled) return false;
        trigger.Enabled = enabled;
        if (Active && SNet.IsMaster && Rules.TryGetValue(trigger.ID, out List<HudInteractTriggerRule>? rules) && rules.Contains(trigger))
        {
            States[trigger.ID] = (enabled, ++NextRevision);
            SendState(trigger.ID, 0, null);
        }
        return true;
    }

    internal static void ConfigurationChanged()
    {
        if (!Active) return;
        RebuildRules();
        if (SNet.IsMaster)
        {
            InitializeHostStates();
            foreach (string triggerId in Rules.Keys) SendState(triggerId, 0, null);
        }
        else
        {
            foreach (KeyValuePair<string, (bool Enabled, ulong Revision)> state in States)
            {
                ApplyState(state.Key, state.Value.Enabled);
            }
            RequestSnapshot();
        }
    }

    private static void RebuildRules()
    {
        Rules.Clear();
        foreach (HudInteractTriggerRule trigger in Runtime.GetHudInteractStateRules())
        {
            if (string.IsNullOrWhiteSpace(trigger.ID) || Encoding.UTF8.GetByteCount(trigger.ID) > Runtime.MaxHudInteractTriggerIdBytes)
            {
                Runtime.LogThrottled($"CTE HUD state ID is empty or exceeds {Runtime.MaxHudInteractTriggerIdBytes} UTF-8 bytes; state sync skipped.");
                continue;
            }
            if (!Rules.TryGetValue(trigger.ID, out List<HudInteractTriggerRule>? rules))
            {
                rules = new List<HudInteractTriggerRule>();
                Rules.Add(trigger.ID, rules);
            }
            rules.Add(trigger);
        }
    }

    private static void InitializeHostStates()
    {
        States.Clear();
        foreach (KeyValuePair<string, List<HudInteractTriggerRule>> entry in Rules)
        {
            States[entry.Key] = (entry.Value[0].Enabled, ++NextRevision);
        }
    }

    internal static void RequestSnapshot()
    {
        if (!Registered || !Active || SNet.IsMaster || !SNet.HasMaster || SNet.Master == null || Rules.Count == 0) return;
        RequestId = NewNonce();
        HudInteractStatePacket packet = CreatePacket(SnapshotRequest);
        packet.RequestId = RequestId;
        Send(packet, SNet.Master);
    }

    private static void Receive(ulong sender, HudInteractStatePacket packet)
    {
        try
        {
            if (!Active || packet.LayoutId != LayoutId) return;
            if (SNet.IsMaster)
            {
                if (packet.Kind != SnapshotRequest || packet.RequestId == 0 || !SNet.TryGetPlayer(sender, out SNet_Player player) || player == null) return;
                foreach (string snapshotTriggerId in Rules.Keys) SendState(snapshotTriggerId, packet.RequestId, player);
                return;
            }
            if (!SNet.HasMaster || SNet.Master == null || sender != SNet.Master.Lookup) return;
            if (packet.Kind == HostReady)
            {
                RequestSnapshot();
                return;
            }
            if (packet.Kind != State || packet.Epoch == 0 || packet.Revision == 0 || packet.Enabled > 1) return;
            if (packet.TriggerIdPayload == null || packet.TriggerIdByteCount == 0 || packet.TriggerIdByteCount > Runtime.MaxHudInteractTriggerIdBytes || packet.TriggerIdByteCount > packet.TriggerIdPayload.Length) return;
            string triggerId = Encoding.UTF8.GetString(packet.TriggerIdPayload, 0, packet.TriggerIdByteCount);
            if (!Rules.ContainsKey(triggerId)) return;
            if (packet.RequestId != 0)
            {
                if (packet.RequestId != RequestId) return;
                if (Epoch != packet.Epoch)
                {
                    Epoch = packet.Epoch;
                    States.Clear();
                }
            }
            else if (packet.Epoch != Epoch)
            {
                return;
            }
            if (States.TryGetValue(triggerId, out (bool Enabled, ulong Revision) previous) && packet.Revision <= previous.Revision) return;
            bool enabled = packet.Enabled != 0;
            States[triggerId] = (enabled, packet.Revision);
            ApplyState(triggerId, enabled);
        }
        catch (Exception exception)
        {
            Runtime.LogThrottled($"CTE HUD state receive failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ApplyState(string triggerId, bool enabled)
    {
        if (!Rules.TryGetValue(triggerId, out List<HudInteractTriggerRule>? rules)) return;
        bool changed = false;
        foreach (HudInteractTriggerRule trigger in rules)
        {
            if (trigger.Enabled == enabled) continue;
            trigger.Enabled = enabled;
            changed = true;
        }
        if (changed) Runtime.MarkActiveTriggerCacheDirty();
    }

    private static HudInteractStatePacket CreatePacket(byte kind)
    {
        return new HudInteractStatePacket
        {
            Kind = kind,
            LayoutId = LayoutId,
            Epoch = Epoch,
            TriggerIdPayload = new byte[Runtime.MaxHudInteractTriggerIdBytes]
        };
    }

    private static void SendState(string triggerId, ulong requestId, SNet_Player? target)
    {
        if (!States.TryGetValue(triggerId, out (bool Enabled, ulong Revision) state)) return;
        HudInteractStatePacket packet = CreatePacket(State);
        packet.Enabled = state.Enabled ? (byte)1 : (byte)0;
        packet.Revision = state.Revision;
        packet.RequestId = requestId;
        packet.TriggerIdByteCount = (ushort)Encoding.UTF8.GetBytes(triggerId, 0, triggerId.Length, packet.TriggerIdPayload, 0);
        Send(packet, target);
    }

    private static void Send(HudInteractStatePacket packet, SNet_Player? target)
    {
        try
        {
            if (target == null) NetworkAPI.InvokeEvent(EventName, packet, SNet_ChannelType.GameOrderCritical);
            else NetworkAPI.InvokeEvent(EventName, packet, target, SNet_ChannelType.GameOrderCritical);
        }
        catch (Exception exception)
        {
            Runtime.LogThrottled($"CTE HUD state send failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static ulong NewNonce()
    {
        ulong nonce;
        do { nonce = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); } while (nonce == 0);
        return nonce;
    }
}

internal static partial class Runtime
{
    internal static uint GetHudInteractStateLayoutId() => GetCurrentLevelLayoutId();

    internal static IEnumerable<HudInteractTriggerRule> GetHudInteractStateRules()
    {
        uint layoutId = GetCurrentLevelLayoutId();
        string layoutName = TryGetLevelLayoutName(layoutId);
        foreach (ConfigDocument config in ConfigManager.Configs)
        {
            if (!config.Enabled || !MatchesCurrentLevel(config, layoutId, layoutName, out _)) continue;
            foreach (HudInteractTriggerRule trigger in config.HudInteractTriggers) yield return trigger;
        }
    }
}

[HarmonyPatch(typeof(SNet_GlobalManager), nameof(SNet_GlobalManager.OnRecallComplete))]
internal static class HudInteractStateRecallPatch
{
    private static void Postfix()
    {
        HudInteractStateSync.RequestSnapshot();
    }
}
