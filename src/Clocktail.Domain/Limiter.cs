using Clocktail.Domain.Vf;

namespace Clocktail.Domain;

/// <summary>What holds the core clock now (the tray's main readout).</summary>
public enum ClockLimit { Idle, None, Power, PowerPolicy, Voltage, Curve, Thermal, Hardware }

/// <summary>One look at the card, as much of it as the classification needs: board watts and the limit in force (NVML's
/// enforced one), the core clock, NVVDD's present voltage, utilization and NVML's limit reasons by name
/// (<c>Telemetry</c>'s: gpu_idle, sw_power_cap, reliability_voltage, ...).</summary>
public sealed record LimiterReading(double? BoardW, double? LimitW, uint? ClockMhz, uint? NvvddUv, uint? UtilPct, IReadOnlyList<string> Reasons);

/// <summary>
/// Which limit holds the clock, from one reading. The firmware climbs the V/F curve until the first limit stops it, so
/// at most a few are in force at once, and each leaves its own mark:
/// <list type="bullet">
/// <item>The power wall: board watts at the enforced limit (within 10 W, the regulator's band).</item>
/// <item>A power sub-policy: NVML's sw_power_cap with the board well under the wall. The memory phase sets it at
/// 336-361 W under a 575/598 W wall (stability runs, 2026-10-18), so the bit alone is not the wall.</item>
/// <item>The voltage limit: NVVDD within two steps (12.5 mV) of MAX. When the top holds the firmware settles one step
/// under MAX; under the wall the card sits 55-75 mV under (drill 20), so the two do not blur. The driver's reason bit
/// 0x400 (reliability_voltage) counts only when NVVDD or MAX is unread: the 5090 sets it at idle, 290 mV under MAX
/// (2026-10-20).</item>
/// <item>The curve's own top: the clock at the highest frequency the curve reaches below MAX. A curve cut flat stops the
/// climb there, because more voltage would buy no clock.</item>
/// <item>Temperature or a hardware slowdown: NVML's own reasons; nothing else shows them.</item>
/// </list>
/// None of them while the card works means the load itself asks for less than any limit allows. Readings jitter, so a
/// caller samples a few times and takes <see cref="Majority"/>.
/// </summary>
public static class Limiter
{
    public const double WallBandW = 10;
    public const uint TopBandUv = 12_500;
    /// <summary>One step of the curve: a clock this close to the curve's top is at it.</summary>
    public const uint CurveBandKhz = 15_000;
    /// <summary>Below this the card is idling: the limits are not what decides the clock.</summary>
    public const double IdleW = 120;
    /// <summary>NVML's gpu_idle counts as idleness only with the GPU this little busy (a loaded card at a flat top sets it too).</summary>
    public const uint IdleUtilPercent = 10;

    public static bool Idle(LimiterReading r) =>
        r.BoardW is < IdleW || r.Reasons.Contains("gpu_idle") && r.UtilPct is < IdleUtilPercent;

    public static bool PowerHolds(LimiterReading r) =>
        r.BoardW is { } w && r.LimitW is { } l && w >= l - WallBandW;

    public static bool PowerPolicyHolds(LimiterReading r) => r.Reasons.Contains("sw_power_cap") && !PowerHolds(r);

    public static bool TopHolds(LimiterReading r, uint? maxUv) =>
        maxUv is { } m && r.NvvddUv is { } uv ? (long)uv + TopBandUv >= m : r.Reasons.Contains("reliability_voltage");

    public static bool CurveHolds(LimiterReading r, uint? curveTopKhz) =>
        curveTopKhz is { } top && r.ClockMhz is { } mhz && mhz * 1000L + CurveBandKhz >= top;

    public static bool ThermalHolds(LimiterReading r) =>
        r.Reasons.Contains("sw_thermal_slowdown") || r.Reasons.Contains("hw_thermal_slowdown");

    public static bool HardwareHolds(LimiterReading r) =>
        r.Reasons.Contains("hw_slowdown") || r.Reasons.Contains("hw_power_brake_slowdown");

    /// <summary>
    /// The ceiling the curve sets: the highest frequency it reaches up to MAX, at the lowest voltage that reaches it. The
    /// firmware runs a frequency at the lowest voltage that gives it, so on a curve cut flat the voltage stops there too.
    /// Null for an empty curve.
    /// </summary>
    public static VfPoint? CurveTop(IReadOnlyList<VfPoint> points, uint? maxUv)
    {
        VfPoint? best = null;
        foreach (var p in points)
        {
            if (maxUv is { } m && p.VoltageUv > m) break;
            if (best is null || p.EffectiveKhz > best.EffectiveKhz) best = p;
        }
        return best;
    }

    /// <summary>The limits in force, the wall first; <see cref="ClockLimit.Idle"/> or <see cref="ClockLimit.None"/> alone when none is.</summary>
    public static IReadOnlyList<ClockLimit> Holding(LimiterReading r, uint? maxUv, uint? curveTopKhz)
    {
        if (Idle(r)) return [ClockLimit.Idle];
        var held = new List<ClockLimit>();
        if (PowerHolds(r)) held.Add(ClockLimit.Power);
        else if (PowerPolicyHolds(r)) held.Add(ClockLimit.PowerPolicy);
        if (TopHolds(r, maxUv)) held.Add(ClockLimit.Voltage);
        else if (CurveHolds(r, curveTopKhz)) held.Add(ClockLimit.Curve);
        if (ThermalHolds(r)) held.Add(ClockLimit.Thermal);
        if (HardwareHolds(r)) held.Add(ClockLimit.Hardware);
        return held.Count == 0 ? [ClockLimit.None] : held;
    }

    /// <summary>
    /// The verdict most samples agree on (the whole set of limits, compared as a set), and how many did; a tie goes to
    /// the latest of the tied verdicts, the card as it is now. Null for no samples.
    /// </summary>
    public static (IReadOnlyList<ClockLimit> Held, int Votes)? Majority(IReadOnlyList<IReadOnlyList<ClockLimit>> samples)
    {
        if (samples.Count == 0) return null;
        var groups = samples.Select((h, i) => (Key: Key(h), Held: h, At: i)).GroupBy(x => x.Key)
            .Select(g => (Held: g.Last().Held, Votes: g.Count(), Last: g.Max(x => x.At)));
        var best = groups.OrderByDescending(g => g.Votes).ThenByDescending(g => g.Last).First();
        return (best.Held, best.Votes);
    }

    /// <summary>The verdict's ids joined, "power_wall+thermal": one string per set of limits.</summary>
    public static string Key(IReadOnlyList<ClockLimit> held) => string.Join("+", held.Select(Id));

    /// <summary>The stable id an agent reads.</summary>
    public static string Id(ClockLimit l) => l switch
    {
        ClockLimit.Idle => "idle",
        ClockLimit.None => "no_limit",
        ClockLimit.Power => "power_wall",
        ClockLimit.PowerPolicy => "power_policy",
        ClockLimit.Voltage => "voltage_limit",
        ClockLimit.Curve => "curve_cap",
        ClockLimit.Thermal => "thermal",
        _ => "hardware_slowdown",
    };

    /// <summary>One line of plain English per limit: what holds the clock and what would move it.</summary>
    public static string Explain(ClockLimit l) => l switch
    {
        ClockLimit.Idle => "The card is idle (under 120 W, or NVML's gpu_idle at under 10 % utilization): no limit decides the clock.",
        ClockLimit.None => "The card is loaded but under every limit: the load asks for less than the limits allow (CPU-bound, a frame cap, a light scene).",
        ClockLimit.Power => "Board power is at the power limit (within 10 W): the card climbs the curve only as far as the watts allow; less voltage per MHz (or a higher limit) buys clock.",
        ClockLimit.PowerPolicy => "NVML reports sw_power_cap with the board far under its power limit: a power sub-policy, not the wall, holds the clock (seen in memory-heavy phases).",
        ClockLimit.Voltage => "NVVDD is within two steps (12.5 mV) of its MAX: the curve cannot climb higher; more clock needs a higher curve at the top voltage.",
        ClockLimit.Curve => "The clock is at the highest frequency the V/F curve reaches below MAX: the curve's own top (a flat top) holds it; raising the top points would let it climb.",
        ClockLimit.Thermal => "NVML reports a thermal slowdown: temperature holds the clock.",
        _ => "NVML reports a hardware slowdown (hw_slowdown or the power brake): a board-level signal throttles the GPU.",
    };
}
