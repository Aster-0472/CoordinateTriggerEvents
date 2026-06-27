using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameData;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace CoordinateTriggerEvents;

internal static class HudInteractTriggerManager
{
    private const byte StageComplete = 1;
    private const byte StageCancel = 2;
    private const byte StageProgressBase = 32;
    private const float CompletionProgressThreshold = 0.999f;
    private const float PendingCancelDelaySeconds = 0.05f;
    private static readonly Dictionary<string, float> LastFireTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CompletedNetworkStages = new(StringComparer.Ordinal);
    private static readonly HashSet<string> FiredLocalProgressStages = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, NativeInteractPoint> InteractPoints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, NativeInteractPoint> InteractPointsByInstanceId = new();
    private static readonly Dictionary<string, int> SelectedPlayerSlots = new(StringComparer.OrdinalIgnoreCase);
    private static int NextMessageId = 1;
    private static string ActiveSignature = string.Empty;

    internal static void Reset()
    {
        LastFireTimes.Clear();
        CompletedNetworkStages.Clear();
        FiredLocalProgressStages.Clear();
        SelectedPlayerSlots.Clear();
        DestroyInteractPoints();
        ActiveSignature = string.Empty;
    }

    internal static void Update()
    {
        if (!GameStateManager.IsInExpedition)
        {
            DestroyInteractPoints();
            ActiveSignature = string.Empty;
            return;
        }

        EnsureNativeInteractPoints();
        UpdatePendingCancels();
        UpdateDebugVisuals();
    }

    internal static void OnReceiveRequest(ulong sender, HudInteractTriggerRequestPacket packet)
    {
        try
        {
            if (!SNet.IsMaster)
            {
                return;
            }

            string triggerID = DecodeTriggerID(packet.TriggerIdPayload, packet.TriggerIdByteCount);
            if (!TryGetActiveTrigger(triggerID, out HudInteractTriggerRule? trigger) || trigger == null)
            {
                return;
            }

            if (!CanHostAccept(trigger, packet.PlayerSlot))
            {
                return;
            }

            HudInteractTriggerConfirmPacket confirm = new()
            {
                MessageId = packet.MessageId,
                PlayerSlot = packet.PlayerSlot,
                StageKind = packet.StageKind,
                TriggerIdByteCount = packet.TriggerIdByteCount,
                TriggerIdPayload = packet.TriggerIdPayload
            };

            ExecuteConfirmedStage(confirm);
            Runtime.BroadcastHudInteractConfirm(confirm);
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"CTE HUD interact request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void OnReceiveConfirm(ulong sender, HudInteractTriggerConfirmPacket packet)
    {
        ExecuteConfirmedStage(packet);
    }

    private static void EnsureNativeInteractPoints()
    {
        List<HudInteractTriggerRule> triggers = Runtime.GetActiveHudInteractTriggers()
            .Select(t => t.Trigger)
            .Where(t => t.Enabled && t.Position != null)
            .ToList();

        string signature = BuildSignature(triggers);
        if (string.Equals(signature, ActiveSignature, StringComparison.Ordinal))
        {
            return;
        }

        DestroyInteractPoints();
        ActiveSignature = signature;

        foreach (HudInteractTriggerRule trigger in triggers)
        {
            try
            {
                NativeInteractPoint? point = NativeInteractPoint.Create(trigger);
                if (point != null)
                {
                    InteractPoints[trigger.ID] = point;
                }
            }
            catch (Exception ex)
            {
                Runtime.LogThrottled($"CTE HUD interact '{trigger.ID}' build failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        int debugCount = InteractPoints.Values.Count(point => point.HasDebugVisual);
        Runtime.Log?.LogMessage($"CTE HUD interact build: Active={triggers.Count}, Built={InteractPoints.Count}, DebugVisible={debugCount}");
    }

    private static string BuildSignature(List<HudInteractTriggerRule> triggers)
    {
        StringBuilder sb = new();
        foreach (HudInteractTriggerRule trigger in triggers.OrderBy(t => t.ID, StringComparer.OrdinalIgnoreCase))
        {
            Vector3 pos = trigger.Position!.ToVector3();
            sb.Append(trigger.ID).Append('|')
                .Append(trigger.Enabled).Append('|')
                .Append(pos.x.ToString("0.###")).Append(',')
                .Append(pos.y.ToString("0.###")).Append(',')
                .Append(pos.z.ToString("0.###")).Append('|')
                .Append(trigger.Radius.ToString("0.###")).Append('|')
                .Append(trigger.HoldTime.ToString("0.###")).Append('|')
                .Append(trigger.HudText).Append('|')
                .Append(trigger.DebugVisible).Append('|')
                .Append(trigger.DebugColor).Append('|')
                .Append(trigger.DebugAlpha.ToString("0.###")).Append('|')
                .Append(trigger.DebugLabel).Append(';');
        }
        return sb.ToString();
    }

    private static void DestroyInteractPoints()
    {
        foreach (NativeInteractPoint point in InteractPoints.Values)
        {
            point.Destroy();
        }
        InteractPoints.Clear();
        InteractPointsByInstanceId.Clear();
        SelectedPlayerSlots.Clear();
    }

    internal static void OnNativeInteractorStateChanged(Interact_Timed interact, PlayerAgent player, bool state)
    {
        try
        {
            if (interact == null) return;
            if (InteractPointsByInstanceId.TryGetValue(interact.GetInstanceID(), out NativeInteractPoint? point))
            {
                point.OnNativeInteractorStateChanged(player, state);
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"CTE HUD interact state callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void OnNativeInteractorCompleted(Interact_Timed interact, PlayerAgent player)
    {
        try
        {
            if (interact == null) return;
            if (InteractPointsByInstanceId.TryGetValue(interact.GetInstanceID(), out NativeInteractPoint? point))
            {
                point.OnNativeInteractorCompleted(player);
            }
        }
        catch (Exception ex)
        {
            Runtime.LogThrottled($"CTE HUD interact completion callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void UpdateDebugVisuals()
    {
        foreach (NativeInteractPoint point in InteractPoints.Values)
        {
            point.UpdateDebugVisual();
        }
    }

    private static void UpdatePendingCancels()
    {
        foreach (NativeInteractPoint point in InteractPoints.Values)
        {
            point.UpdatePendingCancel();
        }
    }

    private static bool CanHostAccept(HudInteractTriggerRule trigger, int playerSlot)
    {
        if (!trigger.Enabled) return false;
        if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition) return false;
        if (trigger.Cooldown > 0f && LastFireTimes.TryGetValue(trigger.ID, out float last) && Time.realtimeSinceStartup - last < trigger.Cooldown)
        {
            return false;
        }

        if (!trigger.HostValidateDistance)
        {
            return true;
        }

        if (!TryGetPlayerBySlot(playerSlot, out PlayerAgent? player) || player == null)
        {
            return false;
        }

        if (trigger.RequireAlivePlayer && !player.Alive)
        {
            return false;
        }

        return true;
    }

    private static void ExecuteConfirmedStage(HudInteractTriggerConfirmPacket packet)
    {
        string triggerID = DecodeTriggerID(packet.TriggerIdPayload, packet.TriggerIdByteCount);
        string key = packet.MessageId + ":" + triggerID + ":" + packet.StageKind;
        if (!CompletedNetworkStages.Add(key))
        {
            return;
        }

        if (!TryGetActiveTrigger(triggerID, out HudInteractTriggerRule? trigger) || trigger == null)
        {
            return;
        }

        IEnumerable<JsonElement> events = SelectEvents(trigger, packet.StageKind);
        Vector3? sourcePosition = trigger.Position?.ToVector3();
        int count = Runtime.ExecuteEventList(events, $"HUD interact trigger '{trigger.ID}' stage={packet.StageKind}", sourcePosition);
        if (packet.StageKind == StageComplete && count >= 0)
        {
            LastFireTimes[trigger.ID] = Time.realtimeSinceStartup;
        }
    }

    private static IEnumerable<JsonElement> SelectEvents(HudInteractTriggerRule trigger, byte stageKind)
    {
        if (stageKind == StageComplete)
        {
            return trigger.Events.Concat(trigger.WardenEvents);
        }
        if (stageKind == StageCancel)
        {
            return trigger.CancelEvents;
        }
        if (stageKind >= StageProgressBase)
        {
            int index = stageKind - StageProgressBase;
            if (index >= 0 && index < trigger.ProgressEvents.Count)
            {
                return trigger.ProgressEvents[index].Events;
            }
        }
        return Enumerable.Empty<JsonElement>();
    }

    private static void TryRequestStage(string triggerID, byte stageKind, int playerSlot)
    {
        if (string.IsNullOrWhiteSpace(triggerID)) return;
        int messageId = NextMessageId++;
        if (NextMessageId == int.MaxValue) NextMessageId = 1;
        byte[] idBytes = Encoding.UTF8.GetBytes(triggerID);
        int count = Math.Min(idBytes.Length, Runtime.MaxHudInteractTriggerIdBytes);
        byte[] payload = new byte[Runtime.MaxHudInteractTriggerIdBytes];
        Array.Copy(idBytes, payload, count);

        HudInteractTriggerRequestPacket request = new()
        {
            MessageId = messageId,
            PlayerSlot = playerSlot,
            StageKind = stageKind,
            TriggerIdByteCount = (ushort)count,
            TriggerIdPayload = payload
        };

        if (SNet.IsMaster)
        {
            OnReceiveRequest(0, request);
        }
        else
        {
            Runtime.BroadcastHudInteractRequest(request);
        }
    }

    private static string DecodeTriggerID(byte[] payload, ushort byteCount)
    {
        try
        {
            if (payload == null || byteCount == 0 || payload.Length < byteCount) return string.Empty;
            return Encoding.UTF8.GetString(payload, 0, byteCount);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetActiveTrigger(string triggerID, out HudInteractTriggerRule? trigger)
    {
        foreach ((ConfigDocument _config, HudInteractTriggerRule candidate) in Runtime.GetActiveHudInteractTriggers())
        {
            if (string.Equals(candidate.ID, triggerID, StringComparison.OrdinalIgnoreCase))
            {
                trigger = candidate;
                return true;
            }
        }
        trigger = null;
        return false;
    }

    private static bool TryGetPlayerBySlot(int playerSlot, out PlayerAgent? player)
    {
        player = null;
        try
        {
            var list = PlayerManager.PlayerAgentsInLevel;
            for (int i = 0; i < list.Count; i++)
            {
                PlayerAgent? candidate = list[i];
                if (candidate != null && Runtime.GetPlayerSlotIndex(candidate) == playerSlot)
                {
                    player = candidate;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static void ClearLocalProgressForTrigger(string triggerID)
    {
        foreach (string key in FiredLocalProgressStages.Where(k => k.StartsWith(triggerID + ":progress:", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            FiredLocalProgressStages.Remove(key);
        }
    }

    private sealed class NativeInteractPoint
    {
        private readonly HudInteractTriggerRule _trigger;
        private readonly GameObject _gameObject;
        private readonly Interact_Timed _interact;
        private readonly TextMesh? _debugLabel;
        private bool _timerStarted;
        private bool _completionRequested;
        private bool _completionReached;
        private bool _pendingCancel;
        private int _pendingCancelSlot = -1;
        private float _pendingCancelTime;
        private float _lastProgress;

        internal bool HasDebugVisual { get; }

        private NativeInteractPoint(HudInteractTriggerRule trigger, GameObject gameObject, Interact_Timed interact, bool hasDebugVisual, TextMesh? debugLabel)
        {
            _trigger = trigger;
            _gameObject = gameObject;
            _interact = interact;
            HasDebugVisual = hasDebugVisual;
            _debugLabel = debugLabel;
        }

        internal static NativeInteractPoint? Create(HudInteractTriggerRule trigger)
        {
            if (trigger.Position == null)
            {
                return null;
            }

            GameObject go = new("CTE_HudInteract_" + SanitizeName(trigger.ID));
            go.transform.position = trigger.Position.ToVector3();
            int layer = LayerMask.NameToLayer("Interaction");
            go.layer = layer >= 0 ? layer : 14;

            SphereCollider collider = go.AddComponent<SphereCollider>();
            collider.isTrigger = false;
            collider.radius = Math.Max(0.1f, trigger.Radius);

            Interact_Timed interact = go.AddComponent<Interact_Timed>();
            interact.m_colliderToOwn = collider;
            interact.InteractionMessage = string.IsNullOrWhiteSpace(trigger.HudText) ? "Interact" : trigger.HudText;
            interact.InteractDuration = Math.Max(0.01f, trigger.HoldTime);
            interact.OnlyActiveWhenLookingStraightAt = false;
            interact.RequireCollisionCheck = false;
            interact.AllowTriggerWithCarryItem = true;
            interact.AbortOnDotOrDistanceDiff = false;
            interact.ExternalPlayerCanInteract += new Func<PlayerAgent, bool>(player => CanLocalInteract(trigger, player));

            TextMesh? debugLabel = null;
            bool hasDebugVisual = false;
            if (trigger.DebugVisible)
            {
                hasDebugVisual = CreateDebugVisual(go, trigger, out debugLabel);
            }

            NativeInteractPoint point = new(trigger, go, interact, hasDebugVisual, debugLabel);
            InteractPointsByInstanceId[interact.GetInstanceID()] = point;
            interact.OnInteractionSelected += new Action<PlayerAgent, bool>(point.OnSelected);
            interact.OnInteractionTriggered += new Action<PlayerAgent>(point.OnTriggered);
            interact.OnInteractionEvaluationAbort += new Action(point.OnAbort);
            interact.TimerUpdated += new Action<float>(point.OnTimerUpdated);
            interact.SetActive(true);
            return point;
        }

        internal void Destroy()
        {
            try
            {
                if (_interact != null)
                {
                    InteractPointsByInstanceId.Remove(_interact.GetInstanceID());
                    _interact.SetActive(false);
                }
                if (_gameObject != null)
                {
                    UnityEngine.Object.Destroy(_gameObject);
                }
            }
            catch { }
        }

        internal void UpdateDebugVisual()
        {
            try
            {
                if (_debugLabel == null || Camera.main == null)
                {
                    return;
                }

                Transform labelTransform = _debugLabel.transform;
                Vector3 direction = labelTransform.position - Camera.main.transform.position;
                if (direction.sqrMagnitude > 0.001f)
                {
                    labelTransform.rotation = Quaternion.LookRotation(direction);
                }
            }
            catch { }
        }

        private static bool CreateDebugVisual(GameObject parent, HudInteractTriggerRule trigger, out TextMesh? label)
        {
            label = null;
            try
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "CTE_HudInteract_Debug_" + SanitizeName(trigger.ID);
                sphere.transform.SetParent(parent.transform, false);
                float diameter = Math.Max(0.1f, trigger.Radius * 2.0f);
                sphere.transform.localScale = new Vector3(diameter, diameter, diameter);
                Collider? visualCollider = sphere.GetComponent<Collider>();
                if (visualCollider != null)
                {
                    UnityEngine.Object.Destroy(visualCollider);
                }

                Renderer? renderer = sphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    Shader shader = Shader.Find("Transparent/Diffuse") ?? Shader.Find("Sprites/Default");
                    renderer.material = new Material(shader);
                    renderer.material.color = ParseColor(trigger.DebugColor, trigger.DebugAlpha);
                }

                if (trigger.DebugLabel)
                {
                    GameObject labelGo = new("CTE_HudInteract_Label_" + SanitizeName(trigger.ID));
                    labelGo.transform.SetParent(parent.transform, false);
                    labelGo.transform.localPosition = Vector3.up * (Math.Max(0.1f, trigger.Radius) + 0.35f);
                    label = labelGo.AddComponent<TextMesh>();
                    label.text = trigger.ID;
                    label.anchor = TextAnchor.MiddleCenter;
                    label.alignment = TextAlignment.Center;
                    label.characterSize = 0.2f;
                    label.color = Color.white;
                }

                return true;
            }
            catch (Exception ex)
            {
                Runtime.LogThrottled($"CTE HUD interact debug visual failed for '{trigger.ID}': {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Color ParseColor(string colorText, float alpha)
        {
            Color color = new(0.0f, 0.75f, 1.0f, Mathf.Clamp01(alpha));
            if (!string.IsNullOrWhiteSpace(colorText) && ColorUtility.TryParseHtmlString(colorText, out Color parsed))
            {
                parsed.a = Mathf.Clamp01(alpha);
                return parsed;
            }
            return color;
        }

        private static bool CanLocalInteract(HudInteractTriggerRule trigger, PlayerAgent player)
        {
            if (!trigger.Enabled) return false;
            if (trigger.RequireInExpedition && !GameStateManager.IsInExpedition) return false;
            if (trigger.RequireAlivePlayer && (player == null || !player.Alive)) return false;
            if (trigger.Cooldown > 0f && LastFireTimes.TryGetValue(trigger.ID, out float last) && Time.realtimeSinceStartup - last < trigger.Cooldown)
            {
                return false;
            }
            return true;
        }

        private void OnSelected(PlayerAgent player, bool selected)
        {
            if (selected)
            {
                SelectedPlayerSlots[_trigger.ID] = Runtime.GetPlayerSlotIndex(player);
            }
            else if (!_timerStarted)
            {
                SelectedPlayerSlots.Remove(_trigger.ID);
            }
        }

        private void OnTimerUpdated(float progress)
        {
            MarkTimerStarted();

            _lastProgress = Mathf.Clamp01(progress);
            if (_lastProgress >= CompletionProgressThreshold)
            {
                _completionReached = true;
            }

            RequestReachedProgressEvents(_trigger, _lastProgress);
        }

        private void OnTriggered(PlayerAgent player)
        {
            Complete(player);
        }

        private void OnAbort()
        {
            QueueCancel(GetSelectedPlayerSlot(_trigger.ID));
        }

        internal void OnNativeInteractorStateChanged(PlayerAgent player, bool state)
        {
            if (state)
            {
                SelectedPlayerSlots[_trigger.ID] = Runtime.GetPlayerSlotIndex(player);
                MarkTimerStarted();
                return;
            }

            QueueCancel(Runtime.GetPlayerSlotIndex(player));
        }

        internal void OnNativeInteractorCompleted(PlayerAgent player)
        {
            Complete(player);
        }

        private void MarkTimerStarted()
        {
            if (_timerStarted)
            {
                return;
            }

            _timerStarted = true;
            _completionRequested = false;
            _completionReached = false;
            _pendingCancel = false;
            _pendingCancelSlot = -1;
            _pendingCancelTime = 0.0f;
            _lastProgress = 0.0f;
            ClearLocalProgressForTrigger(_trigger.ID);
        }

        private void Complete(PlayerAgent player)
        {
            if (_completionRequested)
            {
                return;
            }

            _completionRequested = true;
            _completionReached = true;
            if (!_timerStarted)
            {
                MarkTimerStarted();
                _completionRequested = true;
                _completionReached = true;
            }

            RequestReachedProgressEvents(_trigger, 1.0f);
            TryRequestStage(_trigger.ID, StageComplete, Runtime.GetPlayerSlotIndex(player));
            SelectedPlayerSlots.Remove(_trigger.ID);
            _timerStarted = false;
            _pendingCancel = false;
            _pendingCancelSlot = -1;
            _pendingCancelTime = 0.0f;
        }

        internal void UpdatePendingCancel()
        {
            if (!_pendingCancel || Time.realtimeSinceStartup < _pendingCancelTime)
            {
                return;
            }

            FireCancel(_pendingCancelSlot);
        }

        private void QueueCancel(int playerSlot)
        {
            if (!_timerStarted || _completionRequested || _completionReached || _lastProgress >= CompletionProgressThreshold)
            {
                return;
            }

            _pendingCancel = true;
            _pendingCancelSlot = playerSlot;
            _pendingCancelTime = Time.realtimeSinceStartup + PendingCancelDelaySeconds;
        }

        private void FireCancel(int playerSlot)
        {
            if (!_timerStarted || _completionRequested || _completionReached || _lastProgress >= CompletionProgressThreshold)
            {
                _pendingCancel = false;
                return;
            }

            TryRequestStage(_trigger.ID, StageCancel, playerSlot);
            SelectedPlayerSlots.Remove(_trigger.ID);
            _timerStarted = false;
            _completionRequested = false;
            _completionReached = false;
            _pendingCancel = false;
            _pendingCancelSlot = -1;
            _pendingCancelTime = 0.0f;
            _lastProgress = 0.0f;
        }

        internal static void RequestReachedProgressEvents(HudInteractTriggerRule trigger, float progress)
        {
            for (int i = 0; i < trigger.ProgressEvents.Count && i < 200; i++)
            {
                HudInteractProgressEventRule progressEvent = trigger.ProgressEvents[i];
                if (progressEvent.Progress < 0.0f)
                {
                    continue;
                }

                float requiredProgress = NormalizeProgressThreshold(progressEvent.Progress);
                if (progress + 0.0001f < requiredProgress)
                {
                    continue;
                }

                string localKey = trigger.ID + ":progress:" + i;
                if (FiredLocalProgressStages.Add(localKey))
                {
                    TryRequestStage(trigger.ID, (byte)(StageProgressBase + i), GetSelectedPlayerSlot(trigger.ID));
                }
            }
        }

        private static int GetSelectedPlayerSlot(string triggerID)
        {
            return SelectedPlayerSlots.TryGetValue(triggerID, out int slot) ? slot : -1;
        }

        private static float NormalizeProgressThreshold(float progress)
        {
            if (progress < 0.0f)
            {
                return progress;
            }

            return progress > 1.0f ? Mathf.Clamp01(progress / 100.0f) : Mathf.Clamp01(progress);
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unnamed";
            }

            StringBuilder sb = new(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString();
        }
    }
}

[HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.OnInteractorStateChanged))]
internal static class Interact_Timed_OnInteractorStateChanged_CTEPatch
{
    private static void Postfix(Interact_Timed __instance, PlayerAgent sourceAgent, bool state)
    {
        HudInteractTriggerManager.OnNativeInteractorStateChanged(__instance, sourceAgent, state);
    }
}

[HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.OnInteractorCompleted))]
internal static class Interact_Timed_OnInteractorCompleted_CTEPatch
{
    private static void Postfix(Interact_Timed __instance, PlayerAgent sourceAgent)
    {
        HudInteractTriggerManager.OnNativeInteractorCompleted(__instance, sourceAgent);
    }
}
