using System.Runtime.InteropServices;

namespace CoordinateTriggerEvents;

[StructLayout(LayoutKind.Sequential)]
internal struct HudInteractStatePacket
{
    public ulong Epoch;
    public ulong Revision;
    public ulong RequestId;
    public uint LayoutId;
    public byte Kind;
    public byte Enabled;
    public ushort TriggerIdByteCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Runtime.MaxHudInteractTriggerIdBytes)]
    public byte[] TriggerIdPayload;
}
