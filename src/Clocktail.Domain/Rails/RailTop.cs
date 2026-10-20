namespace Clocktail.Domain.Rails;

/// <summary>
/// Which upper limit is the rail's top right now, and how far the top can move under the write rules.
/// The driver enforces one ceiling, MAX = min(REL, ALT/OP when reported, OV): the firmware walks the V/F curve up to
/// MAX and no further, so exactly the slots equal to MAX are in force (<see cref="Binding"/>; two when they tie, none
/// when MAX sits below all three and something the control structure does not expose caps it). Moving the top up
/// means raising every slot below the target, so the next wall is the lowest slot above MAX (<see cref="Next"/>).
/// Moving it down needs only one slot.
/// </summary>
/// <param name="ReachUpUv">The highest MAX the rules allow: each upper slot raised as far as
/// <see cref="RailWriteRules.Range(RailsSnapshot, Rail, RailSlot, RailOffsets?)"/> accepts, the lowest of those wins.</param>
/// <param name="ReachDownUv">The lowest MAX the rules allow: the lowest any one upper slot may go.</param>
/// <param name="StepUv">The domain's voltage step, 0 when the board reports no domain table.</param>
public sealed record RailTop(
    Rail Rail,
    uint MaxUv,
    IReadOnlyList<RailSlot> Binding,
    RailSlot? Next,
    uint? NextUv,
    uint ReachUpUv,
    uint ReachDownUv,
    uint StepUv)
{
    /// <summary>
    /// Whether the top is what holds a measured operating voltage, or something else (the power wall, temperature, a
    /// clock limit) holds it lower. The regulator settles one step under MAX when the top binds: live, light loads read
    /// 1.090 V under a 1.095 V MAX and 1.065 V under 1.070 V (drill 20), while the power-capped burn sat at 1.015 V,
    /// 55 mV under, with the same MAX. Two steps of margin separate the two cases cleanly.
    /// </summary>
    public bool Holds(uint operatingUv) => (long)MaxUv - operatingUv <= 2L * (StepUv == 0 ? 5_000 : StepUv);

    private static readonly RailSlot[] Upper = [RailSlot.Rel, RailSlot.Alt, RailSlot.Ov];

    /// <param name="origin">The rail's offsets before we owned it, as for the write rules; null when nothing is held.</param>
    public static RailTop? Of(RailsSnapshot s, Rail rail, RailOffsets? origin = null)
    {
        if (s.Get(rail) is not { } r) return null;
        var reported = Upper.Where(x => x != RailSlot.Alt || r.Limits.AltOpUv != 0).ToArray();
        uint max = r.Limits.MaxUv;
        var binding = reported.Where(x => r.Eval(x) == max).ToArray();
        var above = reported.Where(x => r.Eval(x) > max).OrderBy(r.Eval).ToArray();

        long up = long.MaxValue, down = long.MaxValue;
        foreach (var x in reported)
        {
            // A slot the rules will not move stays where it is in both directions.
            var range = RailWriteRules.Range(s, rail, x, origin);
            long eval = r.Eval(x), cur = r.Offsets[x];
            up = Math.Min(up, range is { } g ? eval + (g.MaxUv - cur) : eval);
            down = Math.Min(down, range is { } h ? eval + (h.MinUv - cur) : eval);
        }
        // MAX under all three is held by something else; no slot move lifts it past that, nor is it known how far.
        if (binding.Length == 0) up = Math.Min(up, max);
        down = Math.Min(down, max);

        return new RailTop(rail, max, binding, above.Length > 0 ? above[0] : null, above.Length > 0 ? r.Eval(above[0]) : null,
            (uint)up, (uint)down, r.Domain is { IsValid: true } d ? d.StepUv : 0);
    }
}
