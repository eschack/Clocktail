using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Domain.Rails;

/// <summary>
/// The voltage each GPC measures at its own ADC, beside the NVVDD the rail is asked for. ClockAdcDevicesGetInfo gives
/// the device mask at +4; GetStatus, with that mask at +4, gives device i a u32 at 0x4c + i x 0x4c in µV, and its code byte
/// at +4 from there (the voltage is the code x 6.25 mV). On the RTX 5090 (GB202, driver 617.42) the mask is 0x1ffd:
/// device 0 alone (it reads 25-45 mV above NVVDD, not a GPC), devices 2-12 the 11 GPCs, and +0x48 reads 0xffffffff.
/// Only that layout is read, every value checked against its code; anything else is null rather than a guess. Measured
/// under nvgwls (2026-10-20): the 11-GPC mean 3-9 mV above NVVDD at 925-985 mV, the lowest GPC at most 7 mV below it.
/// </summary>
public static class GpcVoltages
{
    public const uint KnownMask = 0x1ffd;
    public const int Stride = 0x4c, Sentinel = 0x48;
    public const uint CodeUv = 6250;

    public sealed record Reading(IReadOnlyList<uint> GpcUv)
    {
        public double MeanMv => GpcUv.Average(x => (double)x) / 1000.0;
        public double MinMv => GpcUv.Min() / 1000.0;
        public double MaxMv => GpcUv.Max() / 1000.0;
    }

    /// <summary>The GPCs' voltages from a status read made with <paramref name="mask"/>; null unless every check holds.</summary>
    public static Reading? Parse(ReadOnlySpan<byte> status, uint mask)
    {
        if (mask != KnownMask || status.Length < Stride + 13 * Stride) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(status[4..]) != mask) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(status[Sentinel..]) != 0xffffffff) return null;
        var gpcs = new List<uint>();
        for (int i = 2; i <= 12; i++)
        {
            int o = Stride + i * Stride;
            uint uv = BinaryPrimitives.ReadUInt32LittleEndian(status[o..]);
            if (uv is < 500_000 or > 1_300_000 || uv != status[o + 4] * CodeUv) return null;
            gpcs.Add(uv);
        }
        return new Reading(gpcs);
    }

    /// <summary>The mask the driver reports (read once; it does not change).</summary>
    public static uint? Mask(NvClient hw, GpuSession gpu)
    {
        var info = NvClient.NewBuffer(Slot.ClockAdcDevicesGetInfo);
        return hw.Call(gpu, Slot.ClockAdcDevicesGetInfo, info).IsOk ? BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)) : null;
    }

    public static Reading? Read(NvClient hw, GpuSession gpu, uint mask)
    {
        if (mask != KnownMask) return null;
        var st = NvClient.NewBuffer(Slot.ClockAdcDevicesGetStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(st.AsSpan(4), mask);
        return hw.Call(gpu, Slot.ClockAdcDevicesGetStatus, st).IsOk ? Parse(st, mask) : null;
    }
}
