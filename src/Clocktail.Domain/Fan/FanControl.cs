using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Fan;

/// <summary>
/// What may be written to a fan channel. The firmware's Status range [min, max] is the only authority:
/// it is read fresh for every write, never cached, and a manual level outside it is refused. Below min is where a
/// fan may stall or stop; above max is a level the board does not support. Auto has no level to check.
/// </summary>
public static class FanRules
{
    public static string? Validate(FanStatusEntry range, FanState target)
    {
        if (!target.Manual) return null;
        if (target.Level < range.MinLevel || target.Level > range.MaxLevel)
            return $"level {target.Level} on channel {range.Id} is outside the firmware range [{range.MinLevel}, {range.MaxLevel}]";
        return null;
    }

    /// <summary>
    /// Read-modify-write on the fresh Control snapshot. Equal state: nothing to write. Otherwise the mode word gets
    /// bit 0 set or cleared (its other bits are the driver's and stay), and a manual target also gets its level.
    /// Going back to auto leaves the level field as the driver holds it: in auto it is the firmware's field.
    /// </summary>
    public static Mutation Plan(IReadOnlyList<FanStatusEntry> status, uint id, FanState target, byte[] freshControl)
    {
        var control = FanParser.ParseControl(freshControl);
        var channels = FanParser.Join(status, control);
        var ch = channels.FirstOrDefault(c => c.Id == id);
        if (ch == null) return new Mutation.Abort($"fan channel {id} does not exist (channels: {string.Join(", ", channels.Select(c => c.Id))})");
        if (Validate(ch.Status, target) is { } why) return new Mutation.Abort(why);
        if (ch.Control.State == target) return new Mutation.Apply([]);

        var edits = new List<Edit>();
        uint mode = target.Manual ? ch.Control.Mode | FanLayout.ManualBit : ch.Control.Mode & ~FanLayout.ManualBit;
        if (mode != ch.Control.Mode) edits.Add(Edit.UInt32(ch.Control.Offset + FanLayout.ControlMode, mode));
        if (target.Manual && target.Level != ch.Control.Level) edits.Add(Edit.UInt32(ch.Control.Offset + FanLayout.ControlLevel, target.Level));
        return new Mutation.Apply(edits);
    }

    /// <summary>
    /// The firmware ceiling as a check on a whole Control buffer about to be written, for a caller that sees only the
    /// buffer (the elevated broker): every manual channel's level lies inside its Status range. Null when it holds.
    /// </summary>
    public static string? CheckAgainstStatus(byte[] control, byte[] status)
    {
        try
        {
            foreach (var ch in FanParser.Join(FanParser.ParseStatus(status), FanParser.ParseControl(control)))
                if (FanRules.Validate(ch.Status, ch.Control.State) is { } why) return why;
            return null;
        }
        catch (InvalidDataException e) { return e.Message; }
    }
}

public sealed record FanApplyResult(
    uint Channel,
    FanState Requested,
    /// <summary>The channel in the snapshot the write was planned on; null when there was no snapshot.</summary>
    FanState? Before,
    FanState? After,
    FanStatusEntry Range,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// A fan channel is one owned cell: the Control block read and written whole, the channel's {mode bit 0, level}
/// as the cell, <see cref="FanRules"/> as the plan. The Status range is not owned and is read fresh for every call.
/// Ownership is <see cref="OwnedWriter"/>: the original (almost always "auto") is on disk before the first write,
/// and the record is dropped when the channel reads back at it.
/// </summary>
public sealed class FanControl(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "fan";

    public static string Key(uint id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The level field of a channel that is in auto mode is the firmware's: it may track the firmware's own duty.
    /// Those bytes are declared volatile; everything else in the block, including the level of every manual
    /// channel and every mode word, is compared exactly.
    /// </summary>
    public static TransactionSpec Spec(IEnumerable<int> autoIndexes) => new(
        Slot.ClientFanCoolersGetControl, Slot.ClientFanCoolersSetControl,
        Volatile: autoIndexes.Select(i => new ByteRange(FanLayout.ControlEntry(i) + FanLayout.ControlLevel, 4)).ToList());

    public static OwnedCell Cell(uint id, IEnumerable<int> autoIndexes) => new(
        Kind, Key(id), "level", "fan channel control: auto, or a manual level", Spec(autoIndexes),
        raw => FanParser.ControlFor(raw, id)?.State.ToString());

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(IReadOnlyList<FanStatusEntry> status, uint id, FanState target) =>
        raw => FanRules.Plan(status, id, target, raw.ToArray());

    /// <summary>
    /// The cell and plan for moving channel <paramref name="id"/> to <paramref name="target"/>, from a fresh read of
    /// both structures: Status for the range, Control for which channels will be in auto after the write.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan, FanStatusEntry Range) Prepare(NvClient hw, GpuSession gpu, uint id, FanState target)
    {
        var snap = FanReader.Snapshot(hw, gpu);
        var ch = snap.Channels.FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException($"fan channel {id} does not exist (channels: {string.Join(", ", snap.Channels.Select(c => c.Id))})");
        var auto = snap.Channels.Where(c => c.Id == id ? !target.Manual : !c.Control.State.Manual).Select(c => c.Control.Index);
        var status = snap.Channels.Select(c => c.Status).ToList();
        return (Cell(id, auto), Plan(status, id, target), ch.Status);
    }

    /// <summary>
    /// The registry shape every write path shares: key = channel id, value = the cell's canonical state
    /// ("auto" | "manual:&lt;level&gt;"). A write passes the requested state, recovery passes the journal's original;
    /// the firmware range check lives in the plan either way. Fan writes take no options.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw new FormatException($"'{key}' is not a fan channel id");
        var (cell, plan, _) = Prepare(hw, gpu, id, FanState.Parse(value));
        return (cell, plan);
    }

    public FanApplyResult Apply(GpuSession gpu, uint id, FanState target, bool takeOver = false)
    {
        var (cell, plan, range) = Prepare(hw, gpu, id, target);
        var r = new OwnedWriter(hw, journal, sentinel, host).Apply(gpu, cell, plan, takeOver);
        return new FanApplyResult(id, target, Decode(r.Before), Decode(r.After), range, r.Tx, r.Ownership, r.SentinelCleared);
    }

    private static FanState? Decode(string? s) => s == null ? null : FanState.Parse(s);
}
