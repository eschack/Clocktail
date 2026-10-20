using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

public sealed record ClockApplyResult(
    ClockDomain Domain,
    int RequestedKhz,
    /// <summary>The offset in the snapshot the write was planned on; null when the transaction got no snapshot.</summary>
    int? BeforeKhz,
    int? AfterKhz,
    /// <summary>The entry's reported frequency (data +0x18) before and after: the driver's own statement of what the offset did.</summary>
    int? FreqBeforeKhz,
    int? FreqAfterKhz,
    TxResult Tx,
    /// <summary>What the ownership journal says about this domain after the call.</summary>
    string Ownership,
    bool SentinelCleared)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// A P0 clock offset is one owned cell: the Pstates20 table read and written through request mode, the offset
/// field as the cell, <see cref="Pstates20Rules"/> as the plan. Everything about ownership is <see cref="OwnedWriter"/>.
/// </summary>
public sealed class ClockOffsets(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "pstates20";

    public static string Key(ClockDomain d) => d.ToString().ToLowerInvariant();

    public static OwnedCell Cell(ClockDomain domain) => new(
        Kind, Key(domain), "kHz", "P0 clock offset, kHz", Pstates20Layout.Spec,
        raw => Pstates20Parser.Parse(raw).P0(domain)?.DeltaKhz.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The plan: the driver's own range on the offset; for the core clock, the budget it shares with the V/F points;
    /// and for any domain, the curve a Pstates20 write would wipe.
    /// <para>
    /// Budget: every point runs at base + global + point offset and the driver states one range for the core clock,
    /// so the two offsets spend one budget and whichever moves second checks the other: the V/F path bounds
    /// global + point when it writes points, and this plan bounds it against the points as they stand
    /// (<see cref="VfBudget"/>). Recovery is held to it too: the original global was a state the firmware held, but
    /// not necessarily together with the points that are there now.
    /// </para>
    /// <para>
    /// Wipe: any Pstates20 set, memory offset included, resets every V/F point offset to 0 (live, 2026-10-18: a memory
    /// +358 MHz written 22 s after a curve cleared it during a stability run; reproduced by the V/F thread, while a
    /// power-limit write leaves the curve alone). So a P0 offset that would change is refused while any point offset is
    /// non-zero: the order is P0 offsets, then the power limit, then the curve, and the rule lives here so no caller
    /// has to remember it. Recovery gives the curve back before the P0 offsets for the same reason.
    /// </para>
    /// </summary>
    /// <param name="curve">The V/F point offsets read for this write (<see cref="Curve"/>), or null when the driver has
    /// none: then nothing else spends the budget, nothing can be wiped, and the driver's range is the whole limit.</param>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(ClockDomain domain, int deltaKhz, VfBudget? curve = null) =>
        raw =>
        {
            var snap = Pstates20Parser.Parse(raw.ToArray());
            var own = Pstates20Rules.Plan(snap, domain, deltaKhz);
            if (own is not Mutation.Request || curve == null) return own;
            if (WipeReason(curve) is { } wipe) return new Mutation.Abort(wipe);
            if (domain != ClockDomain.Core) return own;
            return curve.Check(VfLimits.From(snap), deltaKhz) is { } over ? new Mutation.Abort(over) : own;
        };

    /// <summary>Why a Pstates20 write may not run over this curve: it would reset the point offsets in force. Null when
    /// every offset is 0, or when they cannot be read (the read failing does not make a curve exist; the budget rule
    /// still holds the global for the core clock).</summary>
    public static string? WipeReason(VfBudget curve)
    {
        if (curve.Offsets == null) return null;
        int set = curve.Offsets.Count(o => o.OffsetKhz != 0);
        return set == 0 ? null
            : $"{set} V/F point offset(s) are in force, and any Pstates20 write resets every one of them to 0: write the P0 offsets before the curve (P0 offsets, power limit, curve last), or give the curve back to 0 first";
    }

    /// <summary>
    /// The V/F point offsets a P0 offset write must look at, read before planning. Null when the driver has no V/F
    /// point structure (no read is made). Offsets that exist but cannot be read come back as such: for the core clock
    /// the plan then allows only a global between 0 and the one in force, which no curve the range admits can turn
    /// into a violation, so nothing is written blind and the way back to 0 stays open.
    /// </summary>
    public static VfBudget? Curve(NvClient hw, GpuSession gpu) =>
        hw.Api.Resolve(Slot.ClockClientClkVfPointsGetInfo) == 0 ? null : VfBudget.Read(hw, gpu);

    public ClockApplyResult Apply(GpuSession gpu, ClockDomain domain, int deltaKhz, bool takeOver = false)
    {
        var r = new OwnedWriter(hw, journal, sentinel, host).Apply(gpu, Cell(domain), Plan(domain, deltaKhz, Curve(hw, gpu)), takeOver, deltaKhz.ToString(CultureInfo.InvariantCulture));
        var before = Entry(r.Tx.Before, domain);
        // The readback describes the hardware unless the write failed and was verifiably restored (then: before).
        var after = r.Tx.Outcome == TxOutcome.Aborted || r.Tx.Restore == RestoreState.Restored ? before
            : r.Tx.After != null ? Entry(r.Tx.After, domain) : null;
        return new ClockApplyResult(domain, deltaKhz, before?.DeltaKhz, after?.DeltaKhz, before?.FreqKhz, after?.FreqKhz, r.Tx, r.Ownership, r.SentinelCleared);
    }

    private static ClockOffset? Entry(byte[]? raw, ClockDomain domain)
    {
        if (raw == null || raw.Length == 0) return null;
        try { return Pstates20Parser.Parse(raw).P0(domain); }
        catch (InvalidDataException) { return null; }
    }
}
