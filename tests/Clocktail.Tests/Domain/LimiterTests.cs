using Clocktail.Domain;
using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

public sealed class LimiterTests
{
    /// <summary>NVVDD's MAX on the stock 5090: 1095 mV.</summary>
    private const uint Max = 1_095_000;
    /// <summary>The wall in force: 598 W.</summary>
    private const double Wall = 598;

    private static LimiterReading R(double w, uint mv, uint mhz = 2800, uint util = 99, params string[] reasons) =>
        new(w, Wall, mhz, mv * 1000, util, reasons);

    /// <summary>A curve rising 15 MHz every 5 mV from 2900 MHz at 1000 mV to 1200 mV, cut flat at <paramref name="flatKhz"/>.</summary>
    private static IReadOnlyList<VfPoint> Curve(uint flatKhz = uint.MaxValue) => [.. Enumerable.Range(0, 41).Select(i =>
    {
        uint uv = 1_000_000 + (uint)i * 5_000, khz = Math.Min(2_900_000 + (uint)i * 15_000, flatKhz);
        return new VfPoint(i, uv, khz, khz, 0, uv);
    })];

    private static uint? Top(IReadOnlyList<VfPoint> curve) => Limiter.CurveTop(curve, Max)?.EffectiveKhz;

    [Fact]
    public void The_curve_top_is_the_highest_clock_below_max_at_the_lowest_voltage_that_reaches_it()
    {
        var rising = Limiter.CurveTop(Curve(), Max)!;
        Assert.Equal((1_095_000u, 3_185_000u), (rising.VoltageUv, rising.EffectiveKhz));
        var flat = Limiter.CurveTop(Curve(3_100_000), Max)!;
        Assert.Equal((1_070_000u, 3_100_000u), (flat.VoltageUv, flat.EffectiveKhz));
        Assert.Equal(1_200_000u, Limiter.CurveTop(Curve(), null)!.VoltageUv);   // MAX unknown: the whole curve
        Assert.Null(Limiter.CurveTop([], Max));
    }

    [Fact]
    public void At_the_wall_only_power_holds()
    {
        var held = Limiter.Holding(R(596, 1_015), Max, Top(Curve()));
        Assert.Equal([ClockLimit.Power], held);
        Assert.Equal("power_wall", Limiter.Key(held));
    }

    [Fact]
    public void One_step_under_max_the_voltage_limit_holds()
    {
        // 3170 MHz is also within a step of the curve's 3185 MHz top: the voltage limit is the one named.
        Assert.Equal([ClockLimit.Voltage], Limiter.Holding(R(420, 1_090, 3_170), Max, Top(Curve())));
    }

    [Fact]
    public void At_the_top_of_a_curve_cut_flat_the_curve_holds_far_under_max()
    {
        Assert.Equal([ClockLimit.Curve], Limiter.Holding(R(380, 1_070, 3_100), Max, Top(Curve(3_100_000))));
    }

    [Fact]
    public void Under_every_ceiling_nothing_holds()
    {
        var held = Limiter.Holding(R(420, 1_075, 2_950), Max, Top(Curve()));
        Assert.Equal([ClockLimit.None], held);
        Assert.Equal("no_limit", Limiter.Key(held));
    }

    [Fact]
    public void Idle_holds_nothing_even_one_step_under_max()
    {
        Assert.Equal([ClockLimit.Idle], Limiter.Holding(R(36, 1_090, 3_170, 1, "gpu_idle"), Max, Top(Curve())));
    }

    [Fact]
    public void Nvml_reasons_mark_thermal_and_hardware()
    {
        var held = Limiter.Holding(R(596, 1_000, 2_800, 99, "sw_power_cap", "sw_thermal_slowdown", "hw_slowdown"), Max, null);
        Assert.Equal([ClockLimit.Power, ClockLimit.Thermal, ClockLimit.Hardware], held);
        Assert.Equal("power_wall+thermal+hardware_slowdown", Limiter.Key(held));
    }

    [Fact]
    public void A_loaded_card_at_a_flat_top_is_capped_by_the_curve_although_nvml_says_gpu_idle()
    {
        // The daily curve's flat top under load: NVML sets gpu_idle at full utilization (344 W).
        Assert.Equal([ClockLimit.Curve], Limiter.Holding(R(344, 1_070, 3_100, 99, "gpu_idle"), Max, Top(Curve(3_100_000))));
    }

    [Fact]
    public void Gpu_idle_with_little_utilization_is_idle_above_the_idle_watts()
    {
        Assert.Equal([ClockLimit.Idle], Limiter.Holding(R(150, 900, 1_500, 3, "gpu_idle"), Max, Top(Curve())));
    }

    [Fact]
    public void Sw_power_cap_far_under_the_wall_is_a_power_sub_policy_not_the_wall()
    {
        // The memory phase: sw_power_cap at 340 W under a 598 W wall.
        Assert.Equal([ClockLimit.PowerPolicy], Limiter.Holding(R(340, 960, 2_480, 99, "sw_power_cap"), Max, Top(Curve())));
    }

    [Fact]
    public void Sw_power_cap_with_instantaneous_peaks_at_the_wall_is_the_wall()
    {
        // Kingdom Come 2 under a 600 W wall: a 552 W average, instantaneous peaks to 593 W, sw_power_cap set.
        var game = new LimiterReading(552, 600, 2_900, 960_000, 99, ["sw_power_cap"], PeakW: 593);
        Assert.Equal([ClockLimit.Power], Limiter.Holding(game, Max, Top(Curve())));
        // The same average with peaks well under the wall stays a sub-policy; and without sw_power_cap a peak means nothing.
        Assert.Equal([ClockLimit.PowerPolicy], Limiter.Holding(game with { PeakW = 560 }, Max, Top(Curve())));
        Assert.Equal([ClockLimit.None], Limiter.Holding(game with { Reasons = [] }, Max, Top(Curve())));
    }

    [Fact]
    public void A_rail_at_its_current_limit_is_the_current_limit_not_a_sub_policy()
    {
        // A trial's NVVDD limit of 300 A: the rail regulates 1-2 % under it, the board far under the wall.
        var r = new LimiterReading(420, Wall, 2_600, 900_000, 99, ["sw_power_cap"], NvvddA: 296, NvvddLimitA: 300, MsvddA: 40, MsvddLimitA: 180);
        Assert.Equal([ClockLimit.Current], Limiter.Holding(r, Max, Top(Curve())));
        Assert.Equal("current_limit", Limiter.Key(Limiter.Holding(r, Max, Top(Curve()))));
        // 10 % under the limit is not at it; without the cap bit the current alone is not taken for a limit.
        Assert.Equal([ClockLimit.PowerPolicy], Limiter.Holding(r with { NvvddA = 270 }, Max, Top(Curve())));
        Assert.Equal([ClockLimit.None], Limiter.Holding(r with { Reasons = [] }, Max, Top(Curve())));
        // At the wall and at a current limit at once: both, the wall first.
        Assert.Equal([ClockLimit.Power, ClockLimit.Current], Limiter.Holding(r with { BoardW = 595 }, Max, Top(Curve())));
    }

    [Fact]
    public void The_reliability_voltage_reason_counts_only_when_the_voltage_is_unread()
    {
        // The 5090 sets it at idle, far under MAX: with NVVDD and MAX read, the voltage decides.
        Assert.Equal([ClockLimit.None], Limiter.Holding(R(380, 1_000, 2_950, 99, "reliability_voltage"), Max, Top(Curve())));
        Assert.Equal([ClockLimit.Voltage], Limiter.Holding(new LimiterReading(380, Wall, 2_950, null, 99, ["reliability_voltage"]), Max, Top(Curve())));
        Assert.Equal([ClockLimit.Voltage], Limiter.Holding(R(380, 1_060, 2_950, 99, "reliability_voltage"), null, Top(Curve())));
    }

    [Fact]
    public void Unreadable_readings_hold_nothing_they_cannot_show()
    {
        // No watts, no voltage, no clock: only NVML's reasons can still say something.
        var r = new LimiterReading(null, null, null, null, null, ["sw_thermal_slowdown"]);
        Assert.Equal([ClockLimit.Thermal], Limiter.Holding(r, Max, 3_100_000));
        Assert.Equal([ClockLimit.None], Limiter.Holding(r with { Reasons = [] }, Max, 3_100_000));
    }

    [Fact]
    public void The_majority_of_the_samples_wins_and_a_tie_goes_to_the_latest()
    {
        IReadOnlyList<ClockLimit> power = [ClockLimit.Power], curve = [ClockLimit.Curve], both = [ClockLimit.Power, ClockLimit.Thermal];
        var m = Limiter.Majority([power, curve, power, both, power])!.Value;
        Assert.Equal((power, 3), (m.Held, m.Votes));

        var tie = Limiter.Majority([curve, power, power, curve])!.Value;
        Assert.Equal((curve, 2), (tie.Held, tie.Votes));

        // A set of limits is one verdict: power alone and power with temperature are not the same answer.
        var sets = Limiter.Majority([both, power, both])!.Value;
        Assert.Equal((both, 2), (sets.Held, sets.Votes));

        Assert.Null(Limiter.Majority([]));
    }

    [Fact]
    public void Every_limit_has_an_id_and_a_line_of_explanation()
    {
        var ids = Enum.GetValues<ClockLimit>().Select(Limiter.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Enum.GetValues<ClockLimit>(), l => Assert.False(string.IsNullOrWhiteSpace(Limiter.Explain(l))));
    }
}
