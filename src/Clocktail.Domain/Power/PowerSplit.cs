using System.Buffers.Binary;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Power;

/// <summary>
/// The current clock of every domain the driver reports, from <see cref="Slot.GetAllClocks"/> at size 0x484 rev 2 (the
/// only size rev 2 accepts on 617.42; rev 1 takes 0x104). Entries of 8 bytes from +4, {kHz, flags}, flags bit 0 =
/// present, indexed by the internal clock-domain id the offset paths use: 0 graphics, 1 XBAR, 2 SYS, 4 memory,
/// 0x15 video. Checked live on the RTX 5090: graphics, memory and video match
/// nvidia-smi to the MHz. Entries past the first 32 carry other data and are not read.
/// </summary>
public static class AllClocksLayout
{
    public const int Size = 0x484, Rev = 2, Base = 4, Entry = 8, Count = 32;
    public const int Graphics = 0, Xbar = 1, Sys = 2, Memory = 4, Video = 0x15;

    public static IReadOnlyDictionary<int, uint> Parse(ReadOnlySpan<byte> b)
    {
        var khz = new Dictionary<int, uint>();
        for (int i = 0; i < Count && Base + (i + 1) * Entry <= b.Length; i++)
        {
            var e = b.Slice(Base + i * Entry, Entry);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) & 1) != 0) khz[i] = BinaryPrimitives.ReadUInt32LittleEndian(e);
        }
        return khz;
    }
}

/// <summary>
/// <see cref="Slot.ClientPowerTopologyGetStatus"/> at 0x48 rev 1: count at +4, entries of 0x10 from +8 {id at +0, value
/// at +8}. Both entries are board power in thousandths of a percent of the default power limit (575 W on a 5090):
/// measured under nvgwls at three loads (2026-10-20), entry 0 read 104,000-104,700 at 600 W, 85,000-86,200 at 493 W and
/// 44,800-45,200 at 259 W (600 / 493 / 259 W of 575 W = 104.3 / 85.7 / 45.0 %); entry 1 the same with a wider spread
/// (102,500-105,700 at 600 W), a shorter window by the look of it. Neither is the GPU's share. The call takes 5-17 ms,
/// more than a 1 Hz sampler should spend, so it is read only when asked for.
/// </summary>
public static class PowerTopologyLayout
{
    public const int Size = 0x48, Rev = 1, CountOffset = 4, Base = 8, Entry = 0x10;

    /// <summary>An entry's value in watts, given the default power limit it is a share of.</summary>
    public static double Watts(uint raw, double defaultW) => raw / 100_000.0 * defaultW;

    public static IReadOnlyList<(uint Id, uint Raw)> Parse(ReadOnlySpan<byte> b)
    {
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(b[CountOffset..]);
        var list = new List<(uint, uint)>();
        for (int i = 0; i < n && Base + (i + 1) * Entry <= b.Length; i++)
        {
            var e = b.Slice(Base + i * Entry, Entry);
            list.Add((BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[8..])));
        }
        return list;
    }
}

/// <summary>Read-only samples for the power split: every call here is a GET, takes no write window and no lock.</summary>
public static class PowerSplitReader
{
    public static IReadOnlyDictionary<int, uint> ReadClocks(NvClient hw, GpuSession gpu)
    {
        var b = new byte[AllClocksLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(AllClocksLayout.Size, AllClocksLayout.Rev).Word);
        var r = hw.CallRaw(gpu, Slot.GetAllClocks, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return AllClocksLayout.Parse(b);
    }

    /// <summary>The working voltage of each rail in <paramref name="rails"/>, from one status read with its mask.</summary>
    public static IReadOnlyDictionary<Rail, uint> ReadVoltages(NvClient hw, GpuSession gpu, RailsSnapshot rails)
    {
        var status = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(status.AsSpan(4), rails.Mask);
        var r = hw.Call(gpu, Slot.VoltVoltRailsGetStatus, status);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return rails.Rails.ToDictionary(x => x.Rail, x => BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(RailsLayout.StatusBase + x.Ordinal * RailsLayout.StatusEntry)));
    }

    public static IReadOnlyList<(uint Id, uint Raw)> ReadTopology(NvClient hw, GpuSession gpu)
    {
        var b = new byte[PowerTopologyLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(PowerTopologyLayout.Size, PowerTopologyLayout.Rev).Word);
        var r = hw.CallRaw(gpu, Slot.ClientPowerTopologyGetStatus, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return PowerTopologyLayout.Parse(b);
    }
}
