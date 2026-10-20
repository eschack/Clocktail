using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Rails;

/// <summary>
/// The rules a rail write must pass on top of <see cref="RailRules.Plan"/>, all against the fresh snapshot.
/// They exist because the user's one hard limit is "never beyond what the firmware reports" (2026-10-17), and
/// that limit has to be a property of the plan, not of the caller's care:
/// <list type="bullet">
/// <item>there is no XOC path: the ceiling is always the standard one, min(domain max, 1.15 V);</item>
/// <item>every upper limit that is raised (REL, ALT/OP and OV alike) must stay at or under that ceiling.
/// On the 5090 OV already sits at 1.2 V, above the ceiling, so this rule means
/// OV can be lowered and never raised. Raising it would be useless anyway: MAX = min(REL, ALT, OV) on this
/// board (read live), and REL and ALT cannot pass the ceiling;</item>
/// <item>every change is a whole number of the domain's voltage steps, so the driver never has to round it
/// (a rounded value would read back different and be rolled back, after the write instead of before it);</item>
/// <item>VMIN may not end above the predicted MAX = min(REL, ALT/OP when reported, OV). <see cref="RailRules.Plan"/>
/// checks VMIN against REL and OV only; ALT is the third term of MAX, so lowering ALT under VMIN must be refused
/// before the write, not rolled back after it;</item>
/// <item>the post-write check is relative to the state before the write: a rail that was already outside the
/// ceiling (left there by another tool) may be moved towards it, and the check only fires on what got worse;</item>
/// <item>every direction rule above lets a slot go as far as its <em>origin</em>: the offsets the hardware held
/// before we owned the cell (the journal's original; for a rollback, the snapshot the write was planned on). The
/// firmware itself held that state, so going back to it, wholly or slot by slot, is never an excursion. The
/// reachable set per slot is max(ceiling, origin), so nothing ever goes above what the firmware held or the
/// ceiling. Stock OV is 1.2 V, above the ceiling; without this, a lowered OV could never be raised back.</item>
/// </list>
/// </summary>
public static class RailWriteRules
{
    private static readonly RailSlot[] All = [RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin];

    /// <param name="origin">The offsets before we owned the cell (see <see cref="RailRules.Plan"/>); never a user's request.</param>
    public static string? Check(RailRecord rail, RailRules.Request req, RailOffsets? origin = null)
    {
        long? O(RailSlot s) => origin is { } o ? rail.Eval(s) + ((long)o[s] - rail.Offsets[s]) : null;
        uint ceiling = RailRules.Ceiling(rail, xoc: false);
        uint step = Step(rail);
        foreach (var s in All)
        {
            if (req[s] is not { } want || want == rail.Offsets[s]) continue;
            long delta = (long)want - rail.Offsets[s];
            if (step != 0 && delta % step != 0)
                return $"{Name(s)} changes by {Mv(delta)} mV, not a whole number of the rail's {Mv(step)} mV steps";
            long eval = rail.Eval(s) + delta;
            if (s != RailSlot.Vmin && delta > 0 && eval > ceiling && !(O(s) is { } oe && eval <= oe))
                return $"{Name(s)} would rise to {Mv(eval)} mV, above the firmware-reported ceiling of {Mv(ceiling)} mV";
        }

        long E(RailSlot s) => rail.Eval(s) + ((long)(req[s] ?? rail.Offsets[s]) - rail.Offsets[s]);
        long maxNow = PredictedMax(rail, s => rail.Eval(s)), maxNew = PredictedMax(rail, E);
        long gapNow = rail.Eval(RailSlot.Vmin) - maxNow, gapNew = E(RailSlot.Vmin) - maxNew;
        long gapOrigin = origin != null ? O(RailSlot.Vmin)!.Value - PredictedMax(rail, x => O(x)!.Value) : long.MinValue;
        if (gapNew > 0 && gapNew > gapNow && gapNew > gapOrigin)
            return $"VMIN would be {Mv(E(RailSlot.Vmin))} mV, above the rail's resulting MAX of {Mv(maxNew)} mV (min of REL, ALT/OP, OV)";
        return null;
    }

    /// <summary>
    /// The offsets one slot may take with the other slots as they are, in whole steps: exactly the values
    /// <see cref="Check"/> and <see cref="RailRules.Plan"/> accept. Null when the slot cannot be changed at all.
    /// For UI sliders; the plan stays the authority.
    /// </summary>
    /// <remarks>Per rail and against the current state only: it ignores the origin and a tracking MSVDD, so on the
    /// 5090 it is too wide at the bottom (NVVDD REL/ALT/OV). Prefer the snapshot overload.</remarks>
    public static (int MinUv, int MaxUv)? Range(RailRecord rail, RailSlot slot)
    {
        if (!rail.Writable) return null;
        bool altReported = rail.Limits.AltOpUv != 0;
        if (slot == RailSlot.Alt && !altReported) return null;
        long step = Step(rail) == 0 ? 1 : Step(rail);
        long cur = rail.Offsets[slot], eval = rail.Eval(slot);
        uint ceiling = RailRules.Ceiling(rail, xoc: false);
        long floor = RailRules.Floor(rail);
        long vmin = rail.Eval(RailSlot.Vmin);
        long Min(params long[] v) => v.Min();

        long loEval, hiEval;
        if (slot == RailSlot.Vmin)
        {
            loEval = floor;
            long others = Min(rail.Eval(RailSlot.Rel), rail.Eval(RailSlot.Ov), altReported ? rail.Eval(RailSlot.Alt) : long.MaxValue, ceiling);
            hiEval = Math.Max(others, eval);   // never forces a change; at worst the current value
        }
        else
        {
            loEval = Math.Max(floor, vmin);
            hiEval = Math.Max(eval, ceiling);  // raises stop at the ceiling; a value already above it may stay
        }
        // Whole steps from the current offset, inside [loEval, hiEval].
        long lo = cur - (eval - loEval) / step * step, hi = cur + (hiEval - eval) / step * step;
        if (eval < loEval) lo = cur;
        if (slot == RailSlot.Rel && !altReported) hi = Math.Min(hi, 0);   // RailRules.Plan: positive REL needs ALT/OP
        if (lo > hi) return null;
        return ((int)lo, (int)hi);
    }

    /// <summary>
    /// Exactly the offsets <see cref="RailWrites.Decide"/> accepts for a move of this one slot, the others as they
    /// are: the whole-step interval around the current value, found by asking the rules themselves step by step
    /// (each rule is monotone in a one-slot move, so the accepted set is an interval). It sees the origin and a
    /// tracking MSVDD, which a closed form per rail cannot. Null when the slot cannot be changed at all.
    /// </summary>
    public static (int MinUv, int MaxUv)? Range(RailsSnapshot s0, Rail rail, RailSlot slot, RailOffsets? origin = null)
    {
        if (s0.Get(rail) is not { } rec || !rec.Writable) return null;
        if (slot == RailSlot.Alt && rec.Limits.AltOpUv == 0) return null;
        uint step = Step(rec);
        if (step == 0) return Range(rec, slot);   // no domain table: the closed form is all there is
        int cur = rec.Offsets[slot];
        bool Ok(long v)
        {
            if (v < int.MinValue || v > int.MaxValue) return false;
            int o = (int)v;
            var req = slot switch
            {
                RailSlot.Rel => new RailRules.Request(null, o, null, null),
                RailSlot.Alt => new RailRules.Request(null, null, o, null),
                RailSlot.Ov => new RailRules.Request(null, null, null, o),
                _ => new RailRules.Request(o, null, null, null),
            };
            return RailWrites.Decide(s0, rail, req, origin) is Mutation.Apply;
        }
        if (!Ok(cur)) return (cur, cur);
        // The evaluated value lives in µV within a 32-bit word: the walk is bounded by the domain anyway (the floor
        // stops it going down, the ceiling or origin going up), the cap only guards against a broken snapshot.
        int cap = (int)Math.Min(100_000L, 2 * (long)uint.MaxValue / step);
        long lo = cur, hi = cur;
        for (int i = 0; i < cap && Ok(lo - step); i++) lo -= step;
        for (int i = 0; i < cap && Ok(hi + step); i++) hi += step;
        return ((int)lo, (int)hi);
    }

    /// <summary>
    /// After a write, for every rail, compared with the state the write was planned on: a violation is a MAX that
    /// is above the ceiling and higher than before, or VMIN above MAX by more than before. A rail that was already
    /// outside does not trap a write that moves it back.
    /// </summary>
    /// <param name="written">The rail the write changed, whose <paramref name="origin"/> is given.</param>
    /// <param name="origin">That rail's offsets before we owned it: MAX and VMIN as they were predicted there (from
    /// <paramref name="before"/>) are also allowed, since the firmware held that state.</param>
    public static string? CheckAfterWrite(RailsSnapshot? before, RailsSnapshot after, Rail? written = null, RailOffsets? origin = null)
    {
        foreach (var r in after.Rails)
        {
            uint ceiling = RailRules.Ceiling(r, xoc: false);
            var b = before?.Get(r.Rail)?.Limits;
            var l = r.Limits;
            long maxOrigin = long.MinValue, gapOrigin = long.MinValue;
            if (r.Rail == written && origin is { } o && before?.Get(r.Rail) is { } br)
            {
                long OE(RailSlot x) => br.Eval(x) + ((long)o[x] - br.Offsets[x]);
                maxOrigin = PredictedMax(br, OE);
                gapOrigin = OE(RailSlot.Vmin) - maxOrigin;
            }
            if (l.MaxUv > ceiling && (b == null || l.MaxUv > b.Value.MaxUv) && l.MaxUv > maxOrigin)
                return $"{RailWrites.Key(r.Rail)}: MAX {Mv(l.MaxUv)} mV is above the ceiling of {Mv(ceiling)} mV" + (b is { } bb ? $" (was {Mv(bb.MaxUv)} mV)" : "");
            long gap = (long)l.VminUv - l.MaxUv, gapBefore = b is { } b2 ? (long)b2.VminUv - b2.MaxUv : 0;
            if (gap > 0 && gap > gapBefore && gap > gapOrigin)
                return $"{RailWrites.Key(r.Rail)}: VMIN {Mv(l.VminUv)} mV is above MAX {Mv(l.MaxUv)} mV";
        }
        return null;
    }

    /// <summary>Absolute form, for a caller with no pre-write snapshot: any MAX above the ceiling or VMIN above MAX
    /// is a violation, so a rail another tool left outside traps every write. Prefer the verify from <see cref="RailWrites.PrepareVerified"/>.</summary>
    public static string? CheckAfterWrite(RailsSnapshot after) => CheckAfterWrite(null, after);

    /// <summary>
    /// On the 5090, MSVDD's REL/ALT/OV bases equal NVVDD's evaluated values (read live): MSVDD may track
    /// NVVDD. When the snapshot shows that signature, an NVVDD write is also judged by what it would do to MSVDD if
    /// the coupling is real: MSVDD's limits shifted by NVVDD's change, then the same ceiling and VMIN rules (only
    /// what gets worse counts). If the coupling is false, this refuses only combinations near the ceiling.
    /// </summary>
    public static string? CheckCoupled(RailsSnapshot s0, Rail written, RailOffsets target, RailOffsets? origin = null)
    {
        if (written != Rail.Nvvdd || s0.Get(Rail.Nvvdd) is not { } nv || s0.Get(Rail.Msvdd) is not { } ms) return null;
        var coupled = new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov };
        if (!coupled.All(x => ms.Base(x) == nv.Eval(x))) return null;
        long Delta(RailSlot x) => (long)target[x] - nv.Offsets[x];
        long Max(Func<RailSlot, long> e) => Math.Min(Math.Min(e(RailSlot.Rel), e(RailSlot.Ov)), ms.Limits.AltOpUv != 0 ? e(RailSlot.Alt) : long.MaxValue);
        long maxNow = Max(x => ms.Eval(x)), maxNew = Max(x => ms.Eval(x) + Delta(x));
        // Where a tracking MSVDD sat when NVVDD was at its origin: the firmware held that too.
        long maxOrigin = origin is { } o ? Max(x => ms.Eval(x) + ((long)o[x] - nv.Offsets[x])) : long.MinValue;
        uint ceiling = RailRules.Ceiling(ms, xoc: false);
        if (maxNew > ceiling && maxNew > maxNow && maxNew > maxOrigin)
            return $"MSVDD tracks NVVDD on this board (its bases equal NVVDD's limits), and this change would put MSVDD's MAX at {Mv(maxNew)} mV, above the ceiling of {Mv(ceiling)} mV";
        long gapNow = ms.Eval(RailSlot.Vmin) - maxNow, gapNew = ms.Eval(RailSlot.Vmin) - maxNew;
        long gapOrigin = origin != null ? ms.Eval(RailSlot.Vmin) - maxOrigin : long.MinValue;
        if (gapNew > 0 && gapNew > gapNow && gapNew > gapOrigin)
            return $"MSVDD tracks NVVDD on this board, and this change would put MSVDD's MAX at {Mv(maxNew)} mV, under its VMIN of {Mv(ms.Eval(RailSlot.Vmin))} mV";
        return null;
    }

    internal static RailOffsets Target(RailRecord rail, RailRules.Request req) => new(
        req.RelUv ?? rail.Offsets.RelUv, req.AltUv ?? rail.Offsets.AltUv, req.OvUv ?? rail.Offsets.OvUv, req.VminUv ?? rail.Offsets.VminUv);

    private static long PredictedMax(RailRecord rail, Func<RailSlot, long> eval) =>
        Math.Min(Math.Min(eval(RailSlot.Rel), eval(RailSlot.Ov)), rail.Limits.AltOpUv != 0 ? eval(RailSlot.Alt) : long.MaxValue);

    private static uint Step(RailRecord rail) => rail.Domain is { IsValid: true } d ? d.StepUv : 0;

    internal static string Name(RailSlot s) => s switch { RailSlot.Rel => "REL", RailSlot.Alt => "ALT/OP", RailSlot.Ov => "OV", _ => "VMIN" };
    internal static string Mv(long uv) => (uv / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record RailApplyResult(
    Rail Rail,
    RailRules.Request Requested,
    /// <summary>The rail's offsets in the snapshot the write was planned on; null when there was no snapshot.</summary>
    RailOffsets? BeforeOffsets,
    RailOffsets? AfterOffsets,
    /// <summary>The evaluated limits the driver reported before planning and after the call.</summary>
    RailLimits? LimitsBefore,
    RailLimits? LimitsAfter,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared,
    /// <summary>
    /// What the post-write status read found: null when every rail's MAX and VMIN are inside the ceiling.
    /// When not null after an Applied write, the change was taken back (<see cref="RolledBack"/>).
    /// </summary>
    string? Violation,
    /// <summary>The give-back after a violation, through the same owned discipline; null when none was needed.</summary>
    OwnedWriteResult? RolledBack,
    /// <summary>Evaluated values that did not move by exactly the offset change: the driver's evaluation
    /// differs from "evaluated = base + offset". Findings, not failures (safety is <see cref="Violation"/>).</summary>
    IReadOnlyList<string> Surprises)
{
    public bool Succeeded => Tx.Succeeded && Violation == null;
}

/// <summary>
/// A rail's four offsets are one owned cell: VoltVoltRailsGetControl read with the rail mask, written back
/// read-modify-write through VoltVoltRailsSetControl, the rail's control entry as the cell, and
/// <see cref="RailRules.Plan"/> plus <see cref="RailWriteRules"/> as the plan. Ownership is <see cref="OwnedWriter"/>.
/// <para>
/// The plan needs more than the control structure: the evaluated limits (status), the rail identity (info) and
/// the voltage-domain bounds (devices). Those are read just before the transaction, and the plan refuses when
/// the transaction's own control snapshot holds different offsets than the ones that status was read against,
/// because then "evaluated = base + offset" would be computed from two different moments.
/// </para>
/// <para>
/// MAX is something the driver derives; it cannot be compared inside the control transaction. It is read
/// right after, and a MAX or VMIN outside the ceiling makes the call give the rail back to the offsets it had
/// before, with a second owned write.
/// </para>
/// </summary>
public sealed class RailWrites(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "rails";

    public static string Key(Rail r) => r.ToString().ToLowerInvariant();

    public static bool TryParseKey(string key, out Rail rail) => Enum.TryParse(key, ignoreCase: true, out rail) && Enum.IsDefined(rail);

    /// <summary>Control reads are queries: the rail mask goes in before the GET (as <see cref="RailsReader"/> does).</summary>
    public static TransactionSpec Spec(uint mask) => new(
        Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl,
        PrepareRead: b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), mask));

    /// <summary>Canonical encoding of a rail's offsets, µV: "rel=…,alt=…,ov=…,vmin=…".</summary>
    public static string Encode(RailOffsets o) => string.Create(CultureInfo.InvariantCulture, $"rel={o.RelUv},alt={o.AltUv},ov={o.OvUv},vmin={o.VminUv}");

    public static RailOffsets Decode(string s)
    {
        var map = s.Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0].Trim(), p => int.Parse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture));
        if (!new[] { "rel", "alt", "ov", "vmin" }.All(map.ContainsKey)) throw new FormatException($"'{s}' is not a complete rail state (rel, alt, ov, vmin)");
        return new RailOffsets(map["rel"], map["alt"], map["ov"], map["vmin"]);
    }

    /// <summary>The rail's offsets in a control structure alone; null when the mask does not hold the rail.</summary>
    public static RailOffsets? OffsetsIn(byte[] control, Rail rail)
    {
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(4));
        int bit = (int)rail;
        if ((mask >> bit & 1) == 0) return null;
        int n = BitOperations.PopCount(mask & ((1u << bit) - 1));
        int cb = RailsLayout.ControlBase + n * RailsLayout.ControlEntry;
        if (cb + RailsLayout.ControlEntry > control.Length) throw new InvalidDataException("rail control entry runs past the structure");
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(control.AsSpan(cb + o));
        return new RailOffsets(I(4), I(8), I(12), I(16));
    }

    public static OwnedCell Cell(Rail rail, uint mask) => new(
        Kind, Key(rail), "µV", $"{rail} voltage offsets (REL, ALT/OP, OV, VMIN), µV", Spec(mask),
        raw => OffsetsIn(raw, rail) is { } o ? Encode(o) : null);

    /// <summary>The whole request as a give-back: every slot set to the given offsets.</summary>
    public static RailRules.Request Exactly(RailOffsets o) => new(o.VminUv, o.RelUv, o.AltUv, o.OvUv);

    /// <summary>
    /// The plan, bound to the snapshot <paramref name="s0"/> it reasons from. It aborts when the transaction's
    /// control snapshot does not show every rail with the offsets <paramref name="s0"/> was read with.
    /// </summary>
    /// <param name="origin">The rail's offsets before we owned it (journal original; rollback: the pre-write snapshot); never a request.</param>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(RailsSnapshot s0, Rail rail, RailRules.Request req, RailOffsets? origin = null) => raw =>
    {
        var control = raw.ToArray();
        foreach (var r in s0.Rails)
            if (OffsetsIn(control, r.Rail) != r.Offsets)
                return new Mutation.Abort($"{Key(r.Rail)} offsets changed between the status read and the write snapshot; nothing written, try again");
        if (BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(4)) != s0.Mask)
            return new Mutation.Abort("the rail mask changed between reads");
        return Decide(s0, rail, req, origin);
    };

    /// <summary>Every rule a write must pass, on the snapshot alone (no hardware): the edits, or why not.
    /// The plan and <see cref="RailWriteRules.Range(RailsSnapshot, Rail, RailSlot, RailOffsets?)"/> both ask this.</summary>
    public static Mutation Decide(RailsSnapshot s0, Rail rail, RailRules.Request req, RailOffsets? origin = null)
    {
        var record = s0.Get(rail);
        if (record == null) return new Mutation.Abort($"the driver does not report {rail}");
        if (RailWriteRules.Check(record, req, origin) is { } why) return new Mutation.Abort(why);
        if (RailWriteRules.CheckCoupled(s0, rail, RailWriteRules.Target(record, req), origin) is { } coupled) return new Mutation.Abort(coupled);
        return RailRules.Plan(record, req, xoc: false, origin: origin);
    }

    public RailApplyResult Apply(GpuSession gpu, Rail rail, RailRules.Request req, bool takeOver = false)
    {
        var s0 = RailsReader.Read(hw, gpu);
        var before = s0.Get(rail);
        var writer = new OwnedWriter(hw, journal, sentinel, host);
        var cell = Cell(rail, s0.Mask);
        // The recorded original is the origin: any slot may go back as far as it, whoever asks.
        RailOffsets? original = journal.Load()?.Items.FirstOrDefault(i => i.Kind == Kind && i.Key == cell.Key)?.Original is { } o ? Decode(o) : null;
        var r = writer.Apply(gpu, cell, Plan(s0, rail, req, original), takeOver);

        // A failed write is restored field by field: only what this write changed goes back (Transaction step 6).
        // If the driver moved another rail as a side effect, that rail stays moved and nobody owns it; say so.
        if (r.Tx.Outcome == TxOutcome.Failed)
            return Result(r, null, OtherRailsMoved(gpu, s0, rail), null, []);
        // Anything else that did not land left the rails as s0 found them.
        if (r.Tx.Outcome != TxOutcome.Applied)
            return Result(r, null, null, null, []);

        RailsSnapshot s1;
        try { s1 = RailsReader.Read(hw, gpu); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            // The control readback matched, so the offsets are known; what the driver derived from them is not.
            return Result(r, null, $"the write landed but the status could not be read afterwards ({e.Message}); MAX unverified, run --rails", null, []);
        }

        var surprises = Surprises(before!, s1.Get(rail), r.Tx.Before, r.Tx.After!, s0, s1);
        var violation = RailWriteRules.CheckAfterWrite(s0, s1, rail, original);
        if (violation == null) return Result(r, s1, null, null, surprises);

        // Give back what this call changed: the offsets of the snapshot the write was planned on.
        var back = OffsetsIn(r.Tx.Before, rail)!.Value;
        var rb = writer.Apply(gpu, cell, Plan(s1, rail, Exactly(back), origin: back), takeOver: false);
        RailsSnapshot? s2 = null;
        try { s2 = RailsReader.Read(hw, gpu); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
        return Result(r, s2, violation, rb, surprises);

        RailApplyResult Result(OwnedWriteResult w, RailsSnapshot? after, string? v, OwnedWriteResult? undo, IReadOnlyList<string> notes)
        {
            var last = undo ?? w;
            RailOffsets? Off(string? s) => s == null ? null : Decode(s);
            // Not applied and state known: nothing moved, so the limits are the ones read before.
            var limitsAfter = after?.Get(rail)?.Limits ?? (w.Tx.Outcome != TxOutcome.Applied && w.StateKnown ? before?.Limits : null);
            return new RailApplyResult(rail, req, Off(w.Before), Off(last.After), before?.Limits, limitsAfter,
                w.Tx, last.Ownership, last.SentinelCleared, v, undo, notes);
        }
    }

    private string? OtherRailsMoved(GpuSession gpu, RailsSnapshot s0, Rail written)
    {
        var buf = Spec(s0.Mask).ReadBuffer();
        if (!hw.Call(gpu, Slot.VoltVoltRailsGetControl, buf).IsOk) return "the control structure could not be re-read after the failed write; other rails unverified";
        var moved = s0.Rails.Where(r => r.Rail != written && OffsetsIn(buf, r.Rail) != r.Offsets)
            .Select(r => $"{Key(r.Rail)} {Encode(r.Offsets)} → {Encode(OffsetsIn(buf, r.Rail)!.Value)}").ToList();
        return moved.Count == 0 ? null
            : $"the driver changed another rail as a side effect of this write and it was not given back ({string.Join("; ", moved)}); nobody holds its original";
    }

    /// <summary>
    /// For each changed slot of the written rail: did the evaluated value move by exactly the offset change? And
    /// did any other rail's evaluated limits move at all? (MSVDD shares NVVDD's REL base on the 5090; a coupling
    /// would show up here first.)
    /// </summary>
    private static IReadOnlyList<string> Surprises(RailRecord before, RailRecord? after, byte[] controlBefore, byte[] controlAfter, RailsSnapshot s0, RailsSnapshot s1)
    {
        if (after == null) return ["the rail is missing from the status read after the write"];
        var notes = new List<string>();
        foreach (var other in s0.Rails.Where(r => r.Rail != before.Rail))
            if (s1.Get(other.Rail)?.Limits is { } l && l != other.Limits)
                notes.Add($"{Key(other.Rail)} limits moved although it was not written: {other.Limits} → {l}");
        var o0 = OffsetsIn(controlBefore, before.Rail)!.Value;
        var o1 = OffsetsIn(controlAfter, before.Rail)!.Value;
        foreach (var s in new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin })
        {
            long delta = (long)o1[s] - o0[s];
            if (delta == 0) continue;
            if (s == RailSlot.Alt && before.Limits.AltOpUv == 0) continue;
            long predicted = before.Eval(s) + delta;
            if (after.Eval(s) != predicted)
                notes.Add($"{RailWriteRules.Name(s)}: offset {RailWriteRules.Mv(delta)} mV, evaluated {RailWriteRules.Mv(before.Eval(s))} → {RailWriteRules.Mv(after.Eval(s))} mV, predicted {RailWriteRules.Mv(predicted)} mV");
        }
        return notes;
    }

    /// <summary>For recovery: the cell and the plan that writes the recorded original back, from a fresh snapshot.</summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan) GiveBack(NvClient hw, GpuSession gpu, Rail rail, string original) =>
        Prepare(hw, gpu, Key(rail), original, original);

    /// <summary>
    /// The registry shape: key "nvvdd"|"msvdd", value in the cell's encoding ("rel=..,alt=..,ov=..,vmin=..", µV).
    /// A value may name only some slots ("rel=-5000"); the others stay as they are. Recovery passes the full original
    /// as both <paramref name="value"/> and <paramref name="original"/>.
    /// <para>
    /// The plan alone cannot see MAX, which the driver derives after the write: use <see cref="PrepareVerified"/>
    /// (or <see cref="Apply"/>) for a user write.
    /// </para>
    /// </summary>
    /// <param name="original">
    /// The journal's recorded original for this cell when we own it (complete encoding), else null. It is the
    /// origin every direction rule allows going back to (see <see cref="RailRules.Plan"/>); without it a user who
    /// lowered OV could not put it back to stock 1.2 V. Only ever a value read from the journal, never a request.
    /// </param>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value, string? original = null)
    {
        var (cell, plan, _) = PrepareVerified(hw, gpu, key, value, original);
        return (cell, plan);
    }

    /// <summary>
    /// <see cref="Prepare"/> plus the post-write check bound to the snapshot the plan was made on: run it after a
    /// landed write, before anything else touches the rails; it reads the status afresh and judges it relative to
    /// that snapshot and the origin (<see cref="RailWriteRules.CheckAfterWrite(RailsSnapshot?, RailsSnapshot, Rail?, RailOffsets?)"/>).
    /// A non-null answer names the limit broken; restore the control bytes the write replaced. The one-arg absolute
    /// check traps a rail another tool left above the ceiling, so a registry Verify should be this one.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan, Func<NvClient, GpuSession, string?> Verify) PrepareVerified(
        NvClient hw, GpuSession gpu, string key, string value, string? original = null)
    {
        if (!TryParseKey(key, out var rail)) throw new ArgumentException($"unknown rail '{key}' (nvvdd or msvdd)");
        var req = ParseRequest(value);
        RailOffsets? origin = original != null ? Decode(original) : null;   // Decode throws on a partial value
        var s0 = RailsReader.Read(hw, gpu);
        return (Cell(rail, s0.Mask), Plan(s0, rail, req, origin),
            (h, g) => RailWriteRules.CheckAfterWrite(s0, RailsReader.Read(h, g), rail, origin));
    }

    /// <summary>"rel=-5000,vmin=0" → a request with those slots set and the rest left alone. Keys are rel, alt, ov, vmin; values µV.</summary>
    public static RailRules.Request ParseRequest(string value)
    {
        int? rel = null, alt = null, ov = null, vmin = null;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var uv))
                throw new FormatException($"'{part}' is not slot=microvolts");
            switch (kv[0].ToLowerInvariant())
            {
                case "rel": rel = uv; break;
                case "alt": alt = uv; break;
                case "ov": ov = uv; break;
                case "vmin": vmin = uv; break;
                default: throw new FormatException($"unknown rail slot '{kv[0]}' (rel, alt, ov, vmin)");
            }
        }
        if (rel == null && alt == null && ov == null && vmin == null) throw new FormatException("no rail slot given");
        return new RailRules.Request(vmin, rel, alt, ov);
    }
}
