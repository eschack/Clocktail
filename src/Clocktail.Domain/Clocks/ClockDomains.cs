using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>
/// The driver's clock-domain ids (topology entry +4) that can be offset: XBAR=1, SYS=2, Video=0x15.
/// Graphics (0) and memory (4) also appear in the same table with the Pstates20 ranges; they are read here, never
/// written, because Pstates20 already owns them and two paths to one offset may stack.
/// </summary>
public enum ClockDomainId : uint
{
    Graphics = 0,
    Xbar = 1,
    Sys = 2,
    Memory = 4,
    Video = 0x15,
}

/// <summary>
/// Layout of the clock-domain topology (ClockClkDomainsGetInfo, 0x86ac r4) and control (Get/SetControl, 0x61a4 r2).
/// Verified against the RTX 5090 (617.42) read-only dump of 2026-10-17.
/// </summary>
public static class ClockDomainsLayout
{
    public const int MaxEntries = 32;
    public const int TopologyMask = 8, TopologyBase = 0xac, TopologyEntry = 0x430;
    public const int ControlMask = 8, ControlBase = 0x124, ControlEntry = 0x304;

    public static int TopologyAt(int index) => TopologyBase + index * TopologyEntry;
    public static int ControlAt(int index) => ControlBase + index * ControlEntry;

    /// <summary>Which field layout a control entry type uses; null for types that are refused.</summary>
    public static int? OffsetField(uint type) => type switch
    {
        0xf => 0x114,
        1 or (>= 3 and <= 10) => 0x10c,
        _ => null,
    };

    /// <summary>
    /// The clock offset in kHz, then two voltage-demand slots in µV (NVVDD, MSVDD), then two slots that
    /// must be zero before a write.
    /// </summary>
    public static int OffsetAt(int index, uint type) => ControlAt(index) + (OffsetField(type) ?? throw new InvalidDataException($"clock control type {type} has no known layout"));

    /// <summary>The gateway's value bound: only guards against garbage (about ±32.7 GHz), the topology gives the real range.</summary>
    public const int GatewayMinKhz = -0x1f40000, GatewayMaxKhz = 0x1f3fc18;
}

/// <summary>One topology entry: what the driver says a clock domain is and how far it may be offset (MHz).</summary>
public sealed record ClockTopologyEntry(
    int Index,
    uint Type,
    uint DomainId,
    int MinOffsetMhz,
    int MaxOffsetMhz,
    /// <summary>+0x78 of the entry (absolute +0x124 of index 0): 1 for graphics, 2 for every other domain on the 5090. The
    /// vdemand probe reads it as a rail number (1 = NVVDD, 2 = MSVDD).</summary>
    uint Rail,
    /// <summary>The four capability bytes (+0x80, +0x81, +0xc4, +0xc5) compared to prove the identity did not change.</summary>
    uint Caps)
{
    public bool Adjustable => MaxOffsetMhz > MinOffsetMhz;

    /// <summary>The identity tuple compared between reads.</summary>
    public string Identity => string.Create(CultureInfo.InvariantCulture, $"idx{Index}/type{Type:x}/dom{DomainId:x}/rail{Rail}/range{MinOffsetMhz}..{MaxOffsetMhz}/caps{Caps:x8}");
}

/// <summary>One control entry as the single-entry query returns it.</summary>
public sealed record ClockControlEntry(int Index, uint Type, int OffsetKhz, int VdemandNvvddUv, int VdemandMsvddUv, int Reserved0, int Reserved1);

public static class ClockDomainsParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    public static IReadOnlyList<ClockTopologyEntry> ParseTopology(byte[] topo)
    {
        var shape = Slots.Info(Slot.ClockClkDomainsGetInfo).Shape!.Value;
        if (topo.Length != shape.Size || U(topo, 0) != shape.Word) throw new InvalidDataException("The clock-domain topology returned an unexpected version.");
        uint mask = U(topo, ClockDomainsLayout.TopologyMask);
        var list = new List<ClockTopologyEntry>();
        for (int i = 0; i < ClockDomainsLayout.MaxEntries; i++)
        {
            if ((mask >> i & 1) == 0) continue;
            int e = ClockDomainsLayout.TopologyAt(i);
            uint type = U(topo, e);
            // The range is two int16 MHz: +0x40 for type 0xf, +0x3e for the other known types.
            int r = e + (type == 0xf ? 0x40 : 0x3e);
            int min = BinaryPrimitives.ReadInt16LittleEndian(topo.AsSpan(r)), max = BinaryPrimitives.ReadInt16LittleEndian(topo.AsSpan(r + 2));
            uint caps = (uint)(topo[e + 0x80] | topo[e + 0x81] << 8 | topo[e + 0xc4] << 16 | topo[e + 0xc5] << 24);
            list.Add(new ClockTopologyEntry(i, type, U(topo, e + 4), min, max, U(topo, e + 0x78), caps));
        }
        return list;
    }

    /// <summary>
    /// The one entry for a domain id, or the reason there is none: a duplicate id is ambiguous,
    /// an inverted range is invalid, an unknown layout type is unsupported.
    /// </summary>
    public static ClockTopologyEntry Find(IReadOnlyList<ClockTopologyEntry> topology, uint domainId)
    {
        var hits = topology.Where(t => t.DomainId == domainId).ToList();
        if (hits.Count == 0) throw new InvalidDataException($"the driver reports no clock domain 0x{domainId:x}");
        if (hits.Count > 1) throw new InvalidDataException($"clock domain 0x{domainId:x} is ambiguous (entries {string.Join(", ", hits.Select(h => h.Index))})");
        var t = hits[0];
        if (t.MinOffsetMhz > t.MaxOffsetMhz) throw new InvalidDataException($"clock domain 0x{domainId:x} reports an inverted range");
        if (ClockDomainsLayout.OffsetField(t.Type) == null) throw new InvalidDataException($"clock domain 0x{domainId:x} has an unsupported layout type {t.Type}");
        return t;
    }

    /// <summary>Parse the answer to a single-entry control query for <paramref name="index"/>.</summary>
    public static ClockControlEntry ParseControl(byte[] control, int index)
    {
        var shape = Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value;
        if (control.Length != shape.Size || U(control, 0) != shape.Word) throw new InvalidDataException("The clock-domain control read returned an unexpected version.");
        if (U(control, ClockDomainsLayout.ControlMask) != 1u << index) throw new InvalidDataException($"The clock-domain control read did not echo entry {index}.");
        int e = ClockDomainsLayout.ControlAt(index);
        uint type = U(control, e);
        int f = ClockDomainsLayout.OffsetAt(index, type);
        return new ClockControlEntry(index, type, I(control, f), I(control, f + 4), I(control, f + 8), I(control, f + 0xc), I(control, f + 0x10));
    }

    /// <summary>The query buffer for one entry: the control GET only fills the entries its mask selects.</summary>
    public static void PrepareQuery(byte[] buf, int index) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(ClockDomainsLayout.ControlMask), 1u << index);
}

/// <summary>The offset rules for one XBAR/SYS/Video entry, as a plan over a fresh control snapshot.</summary>
public static class ClockDomainRules
{
    public static Mutation Plan(ClockTopologyEntry topo, byte[] control, int offsetKhz)
    {
        ClockControlEntry c;
        try { c = ClockDomainsParser.ParseControl(control, topo.Index); }
        catch (InvalidDataException e) { return new Mutation.Abort(e.Message); }
        if (c.Type != topo.Type) return new Mutation.Abort($"clock control entry {topo.Index} reports type {c.Type}, the topology says {topo.Type}: the identity changed");
        if (!topo.Adjustable) return new Mutation.Abort($"the driver reports no offset range for clock domain 0x{topo.DomainId:x}");
        long min = topo.MinOffsetMhz * 1000L, max = topo.MaxOffsetMhz * 1000L;
        if (offsetKhz < min || offsetKhz > max)
            return new Mutation.Abort($"the requested offset {offsetKhz / 1000.0:0.###} MHz exceeds the driver range [{topo.MinOffsetMhz}, {topo.MaxOffsetMhz}] MHz");
        if (offsetKhz < ClockDomainsLayout.GatewayMinKhz || offsetKhz > ClockDomainsLayout.GatewayMaxKhz)
            return new Mutation.Abort("the requested offset cannot be represented");
        if (c.Reserved0 != 0 || c.Reserved1 != 0)
            return new Mutation.Abort("an unexpected rail offset is active in a slot this program does not know; it will not overwrite it");
        if (c.OffsetKhz == offsetKhz) return new Mutation.Apply([]);
        return Mutation.Of(Edit.Int32(ClockDomainsLayout.OffsetAt(topo.Index, topo.Type), offsetKhz));
    }
}

public sealed record ClockDomainApplyResult(
    ClockDomainId Domain,
    int Index,
    int RequestedKhz,
    int? BeforeKhz,
    int? AfterKhz,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// An XBAR/SYS/Video clock offset is one owned cell: one control entry, located by its domain id in the topology,
/// read and written through a single-entry mask, its offset field as the cell, <see cref="ClockDomainRules"/> as the plan.
/// </summary>
public sealed class ClockDomainOffsets(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "clockdomain";

    /// <summary>The domains written here. Graphics and memory belong to Pstates20.</summary>
    public static bool Writable(ClockDomainId d) => d is ClockDomainId.Xbar or ClockDomainId.Sys or ClockDomainId.Video;

    public static string Key(ClockDomainId d) => d.ToString().ToLowerInvariant();

    public static TransactionSpec Spec(int index) => new(Slot.ClockClkDomainsGetControl, Slot.ClockClkDomainsSetControl,
        PrepareRead: b => ClockDomainsParser.PrepareQuery(b, index));

    /// <summary>The cell is keyed by domain, not by entry index: the index is where the driver keeps it today.</summary>
    public static OwnedCell Cell(ClockDomainId domain, ClockTopologyEntry topo) => new(
        Kind, Key(domain), "kHz", $"clock-domain 0x{(uint)domain:x} offset, kHz ({topo.Identity})", Spec(topo.Index),
        raw =>
        {
            var c = ClockDomainsParser.ParseControl(raw, topo.Index);
            return c.Type == topo.Type ? c.OffsetKhz.ToString(CultureInfo.InvariantCulture) : null;
        });

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(ClockTopologyEntry topo, int offsetKhz) =>
        raw => ClockDomainRules.Plan(topo, raw.ToArray(), offsetKhz);

    /// <summary>
    /// The write-path registry's entry: the cell for <paramref name="key"/> (xbar | sys | video) and the plan that sets
    /// it to <paramref name="value"/>, the cell's canonical kHz string. Serves a write (value = requested) and recovery
    /// (value = the journal's original) alike; the plan is null when the value is not an integer.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        var domain = Enum.GetValues<ClockDomainId>().Where(Writable).Cast<ClockDomainId?>().FirstOrDefault(d => Key(d!.Value) == key)
            ?? throw new ArgumentException($"'{key}' is not a clock domain written here (xbar, sys, video)");
        var topo = ClockDomainsParser.Find(ReadTopology(hw, gpu), (uint)domain);
        var plan = int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var khz) ? Plan(topo, khz) : null;
        return (Cell(domain, topo), plan);
    }

    public IReadOnlyList<ClockTopologyEntry> Topology(GpuSession gpu) => ReadTopology(hw, gpu);

    public static IReadOnlyList<ClockTopologyEntry> ReadTopology(NvClient hw, GpuSession gpu)
    {
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsGetInfo);
        var r = hw.Call(gpu, Slot.ClockClkDomainsGetInfo, b);
        if (!r.IsOk) throw new InvalidOperationException($"clock-domain topology read failed: {r}");
        return ClockDomainsParser.ParseTopology(b);
    }

    public ClockControlEntry? ReadControl(GpuSession gpu, ClockTopologyEntry topo)
    {
        var b = Spec(topo.Index).ReadBuffer();
        var r = hw.Call(gpu, Slot.ClockClkDomainsGetControl, b);
        if (!r.IsOk) return null;
        try { return ClockDomainsParser.ParseControl(b, topo.Index); }
        catch (InvalidDataException) { return null; }
    }

    public ClockDomainApplyResult Apply(GpuSession gpu, ClockDomainId domain, int offsetKhz, bool takeOver = false)
    {
        if (!Writable(domain)) throw new InvalidOperationException($"{domain} is offset through Pstates20 (--core-offset/--memory-offset), not here");
        var before = Topology(gpu);
        var topo = ClockDomainsParser.Find(before, (uint)domain);
        var r = new OwnedWriter(hw, journal, sentinel, host).Apply(gpu, Cell(domain, topo), Plan(topo, offsetKhz), takeOver);

        // The topology is re-read and a moved identity tuple is reported after the fact, because the transaction
        // itself already verified the entry it wrote byte for byte.
        var after = ClockDomainsParser.Find(Topology(gpu), (uint)domain);
        string ownership = after.Identity == topo.Identity ? r.Ownership : $"{r.Ownership}; WARNING topology identity changed: {topo.Identity} -> {after.Identity}";
        return new ClockDomainApplyResult(domain, topo.Index, offsetKhz, Int(r.Before), Int(r.After), r.Tx, ownership, r.SentinelCleared);
    }

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
