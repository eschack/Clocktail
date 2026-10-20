using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Hw;

namespace Clocktail.Domain.Fan;

/// <summary>
/// The three FanCoolers structures share one shape: a header, then 32 fixed-stride channel entries of which the
/// first <c>count</c> are live. The sizes are the arithmetic of that shape:
/// Info 0x2c + 32 × 0x30 = 0x62c, Status 0x28 + 32 × 0x34 = 0x6a8, Control 0x2c + 32 × 0x2c = 0x5ac.
/// Info and Status were confirmed by the live size probe on 617.42; Control follows from the same arithmetic and
/// from the setter's version check (0x105ac).
/// </summary>
public static class FanLayout
{
    public const int MaxChannels = 32;

    public const int InfoCount = 0x08, InfoHeader = 0x2c, InfoStride = 0x30;
    public const int StatusCount = 0x04, StatusHeader = 0x28, StatusStride = 0x34;
    public const int ControlCount = 0x08, ControlHeader = 0x2c, ControlStride = 0x2c;

    /// <summary>Status entry fields: +0 id, +4 tach-like reading, +8 min level, +0xc max level, +0x10 current level.</summary>
    public const int StatusReading = 4, StatusMin = 8, StatusMax = 0xc, StatusLevel = 0x10;
    /// <summary>Control entry fields: +0 id, +4 level, +8 mode (bit 0 = manual; the other bits are the driver's and are kept).</summary>
    public const int ControlLevel = 4, ControlMode = 8;
    public const uint ManualBit = 1;
    /// <summary>Levels above int32 are rejected; a level is never negative.</summary>
    public const uint LevelCeiling = 0x7fffffff;

    public static int InfoEntry(int i) => InfoHeader + i * InfoStride;
    public static int StatusEntry(int i) => StatusHeader + i * StatusStride;
    public static int ControlEntry(int i) => ControlHeader + i * ControlStride;
}

/// <summary>Who drives a channel: the firmware's own temperature policy, or a fixed level we set.</summary>
public readonly record struct FanState(bool Manual, uint Level)
{
    public static readonly FanState Auto = new(false, 0);

    /// <summary>
    /// The journal's canonical encoding. In auto mode the level field is the firmware's business, not the user's,
    /// so it is not part of the state: "auto" is the whole of it. A manual channel is its level.
    /// </summary>
    public override string ToString() => Manual ? $"manual:{Level.ToString(CultureInfo.InvariantCulture)}" : "auto";

    public static FanState Parse(string s)
    {
        if (s == "auto") return Auto;
        if (s.StartsWith("manual:", StringComparison.Ordinal) && uint.TryParse(s.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l <= FanLayout.LevelCeiling)
            return new FanState(true, l);
        throw new FormatException($"'{s}' is not a fan state (auto | manual:<level>)");
    }

    public static FanState FromControl(uint level, uint mode) => (mode & FanLayout.ManualBit) != 0 ? new FanState(true, level) : Auto;
}

/// <summary>One channel as the Control structure holds it. <see cref="Index"/> is the entry slot, <see cref="Id"/> the driver's id.</summary>
public sealed record FanControlEntry(int Index, uint Id, uint Level, uint Mode)
{
    public FanState State => FanState.FromControl(Level, Mode);
    public int Offset => FanLayout.ControlEntry(Index);
}

/// <summary>One channel as the Status structure reports it. [Min, Max] is the firmware's enforceable range, the only authority on what may be written.</summary>
public sealed record FanStatusEntry(int Index, uint Id, uint Reading, uint MinLevel, uint MaxLevel, uint Level);

public sealed record FanChannel(uint Id, FanStatusEntry Status, FanControlEntry Control);

public static class FanParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    private static void CheckShape(byte[] raw, Slot slot, string what)
    {
        var shape = Slots.Info(slot).Shape ?? throw new InvalidDataException($"{slot}: structure size is unknown.");
        if (raw.Length != shape.Size || U(raw, 0) != shape.Word)
            throw new InvalidDataException($"The fan {what} read returned an unexpected version or size.");
    }

    private static int Count(byte[] raw, int at, string what)
    {
        int n = raw[at];
        if (n > FanLayout.MaxChannels) throw new InvalidDataException($"The fan {what} reports {n} channels; at most {FanLayout.MaxChannels} exist.");
        return n;
    }

    private static void Unique(IEnumerable<uint> ids, string what)
    {
        var seen = new HashSet<uint>();
        foreach (var id in ids)
            if (!seen.Add(id)) throw new InvalidDataException($"The fan {what} lists channel {id} twice.");
    }

    public static IReadOnlyList<uint> ParseInfoIds(byte[] raw)
    {
        CheckShape(raw, Slot.ClientFanCoolersGetInfo, "info");
        int n = Count(raw, FanLayout.InfoCount, "info");
        var ids = Enumerable.Range(0, n).Select(i => U(raw, FanLayout.InfoEntry(i))).ToList();
        Unique(ids, "info");
        return ids;
    }

    public static IReadOnlyList<FanStatusEntry> ParseStatus(byte[] raw)
    {
        CheckShape(raw, Slot.ClientFanCoolersGetStatus, "status");
        int n = Count(raw, FanLayout.StatusCount, "status");
        var list = new List<FanStatusEntry>(n);
        for (int i = 0; i < n; i++)
        {
            int e = FanLayout.StatusEntry(i);
            var s = new FanStatusEntry(i, U(raw, e), U(raw, e + FanLayout.StatusReading), U(raw, e + FanLayout.StatusMin), U(raw, e + FanLayout.StatusMax), U(raw, e + FanLayout.StatusLevel));
            // An invalid duty range: a range that cannot be trusted makes every write on that channel unsafe.
            if (s.MaxLevel < s.MinLevel || s.MaxLevel > FanLayout.LevelCeiling)
                throw new InvalidDataException($"Fan channel {s.Id}: the driver range [{s.MinLevel}, {s.MaxLevel}] is not usable.");
            list.Add(s);
        }
        Unique(list.Select(s => s.Id), "status");
        return list;
    }

    public static IReadOnlyList<FanControlEntry> ParseControl(byte[] raw)
    {
        CheckShape(raw, Slot.ClientFanCoolersGetControl, "control");
        int n = Count(raw, FanLayout.ControlCount, "control");
        var list = new List<FanControlEntry>(n);
        for (int i = 0; i < n; i++)
        {
            int e = FanLayout.ControlEntry(i);
            var c = new FanControlEntry(i, U(raw, e), U(raw, e + FanLayout.ControlLevel), U(raw, e + FanLayout.ControlMode));
            if (c.Level > FanLayout.LevelCeiling) throw new InvalidDataException($"Fan channel {c.Id}: control level {c.Level} is out of range.");
            list.Add(c);
        }
        Unique(list.Select(c => c.Id), "control");
        return list;
    }

    /// <summary>The control entry for one channel id; null when the structure does not list it.</summary>
    public static FanControlEntry? ControlFor(byte[] raw, uint id) => ParseControl(raw).FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Status and Control must describe the same channels ; otherwise an id in one may not mean
    /// the same fan in the other, and a range from Status cannot be applied to a level in Control.
    /// </summary>
    public static IReadOnlyList<FanChannel> Join(IReadOnlyList<FanStatusEntry> status, IReadOnlyList<FanControlEntry> control)
    {
        var s = status.Select(x => x.Id).OrderBy(x => x).ToList();
        var c = control.Select(x => x.Id).OrderBy(x => x).ToList();
        if (!s.SequenceEqual(c))
            throw new InvalidDataException($"Fan status lists channels [{string.Join(",", s)}] but control lists [{string.Join(",", c)}].");
        return control.Select(ctl => new FanChannel(ctl.Id, status.First(x => x.Id == ctl.Id), ctl)).ToList();
    }
}

public sealed record FanSnapshot(IReadOnlyList<FanChannel> Channels, byte[] StatusRaw, byte[] ControlRaw);

public static class FanReader
{
    public static byte[] Read(NvClient hw, GpuSession gpu, Slot slot)
    {
        var b = NvClient.NewBuffer(slot);
        var r = hw.Call(gpu, slot, b);
        if (!r.IsOk) throw new InvalidOperationException($"{slot}: {r}");
        return b;
    }

    public static IReadOnlyList<FanStatusEntry> ReadStatus(NvClient hw, GpuSession gpu) =>
        FanParser.ParseStatus(Read(hw, gpu, Slot.ClientFanCoolersGetStatus));

    public static FanSnapshot Snapshot(NvClient hw, GpuSession gpu)
    {
        var status = Read(hw, gpu, Slot.ClientFanCoolersGetStatus);
        var control = Read(hw, gpu, Slot.ClientFanCoolersGetControl);
        return new FanSnapshot(FanParser.Join(FanParser.ParseStatus(status), FanParser.ParseControl(control)), status, control);
    }
}
