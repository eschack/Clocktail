using Clocktail.Core.Native;
using Clocktail.Hw;

namespace Clocktail.Core.Gpu;

/// <summary>Raw, read-only probes of the private VOLT interfaces, used while the layouts are being verified.</summary>
public static class VoltProbe
{
    public const uint IdVoltageInfo = 0x2C73AFDC;     // "Voltage information read"  v2, 0x184c
    public const uint IdVoltageControl = 0xA3070DB0;  // "Voltage control read"      v2, 0xac8
    public const uint IdVoltageStatus = 0x5D0634EE;   // "Voltage status read"       v2, 0x1620

    public sealed record RawResult(string Name, uint Id, int Status, uint Version, byte[]? Data);

    public static RawResult Info(NvApi api, nint gpu)
    {
        var data = new byte[0x184c];
        BitConverter.TryWriteBytes(data, GuardedCall.Version(0x184c, 2));
        int rc = GuardedCall.Invoke("voltage_info", api.Query(IdVoltageInfo), gpu, data);
        return new RawResult("voltage_info", IdVoltageInfo, rc, GuardedCall.Version(0x184c, 2), rc == 0 ? data : null);
    }

    public static RawResult Control(NvApi api, nint gpu, uint railMask)
    {
        var data = new byte[0xac8];
        BitConverter.TryWriteBytes(data, GuardedCall.Version(0xac8, 2));
        BitConverter.TryWriteBytes(data.AsSpan(4), railMask);
        int rc = GuardedCall.Invoke("voltage_control", api.Query(IdVoltageControl), gpu, data);
        return new RawResult("voltage_control", IdVoltageControl, rc, GuardedCall.Version(0xac8, 2), rc == 0 ? data : null);
    }

    public static RawResult Status(NvApi api, nint gpu, uint railMask)
    {
        var data = new byte[0x1620];
        BitConverter.TryWriteBytes(data, GuardedCall.Version(0x1620, 2));
        BitConverter.TryWriteBytes(data.AsSpan(4), railMask);
        int rc = GuardedCall.Invoke("voltage_status", api.Query(IdVoltageStatus), gpu, data);
        return new RawResult("voltage_status", IdVoltageStatus, rc, GuardedCall.Version(0x1620, 2), rc == 0 ? data : null);
    }

    /// <summary>Non-zero dwords as "offset: value" pairs, which is the compact form used to compare dumps.</summary>
    public static IEnumerable<string> NonZeroDwords(byte[] data)
    {
        for (int o = 0; o + 4 <= data.Length; o += 4)
        {
            uint v = BitConverter.ToUInt32(data, o);
            if (v != 0) yield return $"+0x{o:x}: 0x{v:x8} ({(int)v})";
        }
    }
}
