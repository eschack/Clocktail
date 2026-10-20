namespace Clocktail.Domain.Rails;

/// <summary>
/// The voltage rules as pure functions. Everything is in microvolts.
/// <list type="bullet">
/// <item>ceiling = min(domain max, 1.15 V); XOC = domain max; no valid domain table → 1.15 V / 1.25 V.</item>
/// <item>new evaluated value = new offset − current offset + current evaluated value.</item>
/// <item>floor (domain min) applies to every slot; VMIN ≤ REL, VMIN ≤ OV, VMIN ≤ ceiling.</item>
/// <item>only an increase is checked against the ceiling, and only for REL and ALT; OV never is.</item>
/// <item>every direction rule bounds what a write makes <em>worse</em>, measured against both the current state and
/// the origin (the state the hardware held before we owned the cell): a value up to the origin is always allowed,
/// because the firmware itself held it. So each slot may reach max(ceiling, origin), never more.</item>
/// </list>
/// </summary>
public static class RailRules
{
    public const uint StandardCeilingUv = 1_150_000;
    public const uint XocFallbackCeilingUv = 1_250_000;

    public static uint Ceiling(RailRecord rail, bool xoc)
    {
        var d = rail.Domain;
        if (d is { IsValid: true }) return xoc ? d.Value.MaxUv : Math.Min(d.Value.MaxUv, StandardCeilingUv);
        return xoc ? XocFallbackCeilingUv : StandardCeilingUv;
    }

    public static uint Floor(RailRecord rail) => rail.Domain is { IsValid: true } d ? d.MinUv : 0;

    /// <summary>A requested change: null slots are left as they are.</summary>
    public readonly record struct Request(int? VminUv, int? RelUv, int? AltUv, int? OvUv)
    {
        public int? this[RailSlot s] => s switch { RailSlot.Rel => RelUv, RailSlot.Alt => AltUv, RailSlot.Ov => OvUv, _ => VminUv };
    }

    /// <summary>
    /// Validate a request against the fresh snapshot and turn it into control-structure edits.
    /// Returns the edits (possibly empty when nothing changes) or a reason to abort.
    /// </summary>
    /// <param name="origin">
    /// The offsets the hardware held before we owned this cell: the journal's recorded original (or, for a rollback,
    /// the snapshot the write was planned on). Every direction rule (ceiling on raises, VMIN consistency, positive
    /// REL) lets a slot go as far as its origin value, because that is a state the firmware itself held; the floor
    /// and representability still apply. Without it a stock value above the ceiling (OV 1.2 V on the 5090) could
    /// never be given back once lowered. Callers pass only values read from the hardware or the journal, never a
    /// user's request. Null: the current state is the only reference.
    /// </param>
    public static Mutation Plan(RailRecord rail, Request req, bool xoc, bool relOnlyOk = false, RailOffsets? origin = null)
    {
        if (!rail.Writable) return new Mutation.Abort($"{rail.Rail} is not writable: {rail.NotWritableReason}");

        uint ceiling = Ceiling(rail, xoc);
        uint floor = Floor(rail);
        bool altReported = rail.Limits.AltOpUv != 0;

        // Target offsets: requested or current.
        int vmin = req.VminUv ?? rail.Offsets.VminUv;
        int rel = req.RelUv ?? rail.Offsets.RelUv;
        int alt = req.AltUv ?? rail.Offsets.AltUv;
        int ov = req.OvUv ?? rail.Offsets.OvUv;


        if (!altReported && req.AltUv is { } a && a != rail.Offsets.AltUv)
            return new Mutation.Abort("the driver does not report an operating limit (ALT/OP) for this rail, so ALT cannot be changed");
        if (req.RelUv is { } r && r > 0 && !(origin is { } o0 && r <= o0.RelUv) && !altReported && !(relOnlyOk && alt == 0))
            return new Mutation.Abort("a positive REL offset requires a writable REL-only record with no ALT/OP offset");

        long vminE = Eval(rail, RailSlot.Vmin, vmin), relE = Eval(rail, RailSlot.Rel, rel), ovE = Eval(rail, RailSlot.Ov, ov);
        long? altE = altReported ? Eval(rail, RailSlot.Alt, alt) : null;

        foreach (var (name, e) in new[] { ("VMIN", vminE), ("REL", relE), ("OV", ovE) }.Concat(altE is { } ae ? [("ALT/OP", ae)] : Array.Empty<(string, long)>()))
        {
            if (e < floor) return new Mutation.Abort($"{name} would evaluate to {Mv(e)} mV, below the reported device minimum of {Mv(floor)} mV");
            if (e > uint.MaxValue) return new Mutation.Abort($"{name} would evaluate to {Mv(e)} mV, which the driver cannot represent");
        }
        // How far VMIN sits above the lowest of its bounds (> 0 is a conflict); only a conflict that grows is refused.
        long Conflict(long v, long r2, long o2) => Math.Max(Math.Max(v - r2, v - o2), v - ceiling);
        long conflict = Conflict(vminE, relE, ovE);
        long conflictNow = Conflict(rail.Eval(RailSlot.Vmin), rail.Eval(RailSlot.Rel), rail.Eval(RailSlot.Ov));
        long conflictOrigin = origin is { } o1 ? Conflict(Eval(rail, RailSlot.Vmin, o1.VminUv), Eval(rail, RailSlot.Rel, o1.RelUv), Eval(rail, RailSlot.Ov, o1.OvUv)) : long.MinValue;
        if (conflict > 0 && conflict > conflictNow && conflict > conflictOrigin)
            return new Mutation.Abort($"the minimum-voltage offset conflicts with the current upper voltage limits (VMIN {Mv(vminE)} mV, REL {Mv(relE)} mV, OV {Mv(ovE)} mV, ceiling {Mv(ceiling)} mV)");
        bool UpToOrigin(RailSlot s, long e) => origin is { } o3 && e <= Eval(rail, s, o3[s]);
        if (rel > rail.Offsets.RelUv && relE > ceiling && !UpToOrigin(RailSlot.Rel, relE))
            return new Mutation.Abort($"REL {Mv(rel)} mV would put this limit at {Mv(relE)} mV at the current driver baseline; the selected mode allows up to {Mv(ceiling)} mV");
        if (altE is { } ae2 && alt > rail.Offsets.AltUv && ae2 > ceiling && !UpToOrigin(RailSlot.Alt, ae2))
            return new Mutation.Abort($"ALT/OP {Mv(alt)} mV would put this limit at {Mv(ae2)} mV at the current driver baseline; the selected mode allows up to {Mv(ceiling)} mV");

        var edits = new List<Edit>();
        void Put(RailSlot s, int value) { if (value != rail.Offsets[s]) edits.Add(Edit.Int32(rail.ControlFieldOffset(s), value)); }
        Put(RailSlot.Vmin, vmin); Put(RailSlot.Rel, rel); Put(RailSlot.Alt, alt); Put(RailSlot.Ov, ov);
        return new Mutation.Apply(edits);
    }

    /// <summary>After a write: the rail's MAX must sit under the ceiling and VMIN under MAX.</summary>
    public static string? CheckAfterWrite(RailRecord rail, bool xoc)
    {
        uint ceiling = Ceiling(rail, xoc);
        if (rail.Limits.MaxUv > ceiling || rail.Limits.VminUv > rail.Limits.MaxUv)
            return $"the resulting voltage limits exceed the active mode ceiling or conflict with VMIN (MAX {Mv(rail.Limits.MaxUv)} mV, VMIN {Mv(rail.Limits.VminUv)} mV, ceiling {Mv(ceiling)} mV)";
        return null;
    }

    private static long Eval(RailRecord rail, RailSlot s, int newOffset) => (long)newOffset - rail.Offsets[s] + rail.Eval(s);
    private static string Mv(long uv) => (uv / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
