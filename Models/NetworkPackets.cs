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

[StructLayout(LayoutKind.Sequential)]
internal struct CteTerminalTriggerPacket
{
    public int MessageId;
    public int RuntimeIndex;
    public int PlayerSlot;
    public byte EventKind;
    public ushort SelectorByteCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Runtime.MaxTerminalSyncSelectorBytes)]
    public byte[] SelectorPayload;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HudInteractTriggerRequestPacket
{
    public int MessageId;
    public int PlayerSlot;
    public byte StageKind;
    public ushort TriggerIdByteCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Runtime.MaxHudInteractTriggerIdBytes)]
    public byte[] TriggerIdPayload;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HudInteractTriggerConfirmPacket
{
    public int MessageId;
    public int PlayerSlot;
    public byte StageKind;
    public ushort TriggerIdByteCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Runtime.MaxHudInteractTriggerIdBytes)]
    public byte[] TriggerIdPayload;
}
