using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

public enum ClockDomain : uint { Core = 0, Memory = 4 }

public sealed record ClockOffset(
    int PstateIndex,
    uint PstateId,
    int ClockIndex,
    uint DomainId,
    uint TypeId,
    bool Editable,
    int DeltaKhz,
    int MinKhz,
    int MaxKhz,
    /// <summary>data +0x18: the resulting frequency (single) or the range minimum (type 1). Status, not control:
    /// the driver moves it with the offset (live 617.42: +15 MHz shifted it, readback differed at clock +0x18).</summary>
    int FreqKhz,
    /// <summary>data +0x1c: the range maximum for type 1 entries, 0 for single-frequency entries.</summary>
    int FreqMaxKhz)
{
    /// <summary>Offset of this entry's delta value inside the structure.</summary>
    public int DeltaOffset => Pstates20Layout.Clock(PstateIndex, ClockIndex) + 0xc;
}

public sealed record Pstates20Snapshot(bool Editable, uint NumPstates, uint NumClocks, uint NumBaseVoltages, IReadOnlyList<ClockOffset> Clocks, byte[] Raw)
{
    /// <summary>The P0 entry for a domain, which is the only one ever written.</summary>
    public ClockOffset? P0(ClockDomain d) => Clocks.FirstOrDefault(c => c.PstateId == 0 && c.DomainId == (uint)d);
}

/// <summary>NV_GPU_PERF_PSTATES20_INFO_V2: the public layout, 0x1cf8 bytes. The sizes below reproduce it exactly.</summary>
public static class Pstates20Layout
{
    public const int Header = 0x14;          // version, editable, numPstates, numClocks, numBaseVoltages
    public const int PstateStride = 0x1c8;   // id, editable, 8 clocks x 0x2c, 4 base voltages x 0x18
    public const int ClockStride = 0x2c;     // domainId, typeId, editable, delta{value,min,max}, data[0x14]
    public const int MaxPstates = 16, MaxClocks = 8;

    /// <summary>The clock entry's data union: single.freq_kHz or range{min,max,domainId,minVolt,maxVolt}.</summary>
    public const int Data = 0x18, DataLength = 0x14;

    public static int Pstate(int p) => Header + p * PstateStride;
    public static int Clock(int p, int c) => Pstate(p) + 8 + c * ClockStride;

    /// <summary>
    /// Bytes the driver changes on its own when an offset lands: every clock entry's data union, which reports the
    /// resulting frequency. The control fields (delta, range, flags) stay under exact comparison. Without this the
    /// first live +15 MHz write was judged "rejected" although the delta had landed, and its correct restore
    /// "unverified" for the same reason.
    /// </summary>
    public static readonly IReadOnlyList<ByteRange> StatusRanges =
        (from p in Enumerable.Range(0, MaxPstates) from c in Enumerable.Range(0, MaxClocks) select new ByteRange(Clock(p, c) + Data, DataLength)).ToList();

    /// <summary>The one transaction spec every Pstates20 write uses.</summary>
    public static readonly TransactionSpec Spec = new(Slot.GetPstates20, Slot.SetPstates20, Volatile: StatusRanges);
}

public static class Pstates20Parser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    public static Pstates20Snapshot Parse(byte[] raw)
    {
        var shape = Slots.Info(Slot.GetPstates20).Shape!.Value;
        if (raw.Length != shape.Size || U(raw, 0) != shape.Word)
            throw new InvalidDataException("The P-state read returned an unexpected version or size.");
        uint numP = U(raw, 8), numC = U(raw, 0xc), numV = U(raw, 0x10);
        if (numP > Pstates20Layout.MaxPstates || numC > Pstates20Layout.MaxClocks)
            throw new InvalidDataException($"The P-state table claims {numP} P-states and {numC} clocks, beyond the structure.");
        var clocks = new List<ClockOffset>();
        for (int p = 0; p < numP; p++)
        {
            int pb = Pstates20Layout.Pstate(p);
            uint id = U(raw, pb);
            for (int c = 0; c < numC; c++)
            {
                int cb = Pstates20Layout.Clock(p, c);
                clocks.Add(new ClockOffset(p, id, c, U(raw, cb), U(raw, cb + 4), (U(raw, cb + 8) & 1) != 0, I(raw, cb + 0xc), I(raw, cb + 0x10), I(raw, cb + 0x14),
                    I(raw, cb + Pstates20Layout.Data), I(raw, cb + Pstates20Layout.Data + 4)));
            }
        }
        return new Pstates20Snapshot((U(raw, 4) & 1) != 0, numP, numC, numV, clocks, raw);
    }

    /// <summary>The minimal request: one P-state (P0), one clock entry, the delta in kHz.</summary>
    public static byte[] BuildRequest(ClockDomain domain, int deltaKhz)
    {
        var b = NvClient.NewBuffer(Slot.SetPstates20);
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        W(0x8, 1); W(0xc, 1);              // numPstates, numClocks
        W(0x14, 0); W(0x18, 1);            // pstateId = P0, editable
        W(0x1c, (uint)domain); W(0x24, 1); // domainId, editable
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x28), deltaKhz);
        return b;
    }
}

/// <summary>Rules for a core/memory offset change: P0 only, the driver's own [min, max] range.</summary>
public static class Pstates20Rules
{
    public static Mutation Plan(Pstates20Snapshot snap, ClockDomain domain, int deltaKhz)
    {
        if (!snap.Editable) return new Mutation.Abort("the driver reports the P-state table as not editable");
        var entry = snap.P0(domain);
        if (entry == null) return new Mutation.Abort($"P0 has no {domain} clock entry");
        if (!entry.Editable) return new Mutation.Abort($"the {domain} clock offset is not editable on this GPU");
        if (deltaKhz < entry.MinKhz || deltaKhz > entry.MaxKhz)
            return new Mutation.Abort($"the requested {domain} offset {deltaKhz / 1000.0:0.###} MHz exceeds the driver range [{entry.MinKhz / 1000.0:0.###}, {entry.MaxKhz / 1000.0:0.###}] MHz");
        if (deltaKhz == entry.DeltaKhz) return new Mutation.Apply([]);
        return new Mutation.Request(
            Pstates20Parser.BuildRequest(domain, deltaKhz),
            [Edit.Int32(entry.DeltaOffset, deltaKhz)],
            Pstates20Parser.BuildRequest(domain, entry.DeltaKhz));
    }
}

public static class Pstates20Reader
{
    public static Pstates20Snapshot Read(NvClient hw, GpuSession gpu)
    {
        var raw = NvClient.NewBuffer(Slot.GetPstates20);
        var r = hw.Call(gpu, Slot.GetPstates20, raw);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return Pstates20Parser.Parse(raw);
    }
}
