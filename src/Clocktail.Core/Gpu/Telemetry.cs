using Clocktail.Core.Native;
using Clocktail.Hw;

namespace Clocktail.Core.Gpu;

public sealed record TelemetrySnapshot(
    double? TemperatureC,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? PowerDefaultWatts,
    double? PowerEnforcedWatts,
    double? PowerMinWatts,
    double? PowerMaxWatts,
    uint? GraphicsClockMhz,
    uint? MemoryClockMhz,
    uint? GraphicsMaxMhz,
    uint? MemoryMaxMhz,
    uint? GpuUtilPercent,
    uint? MemUtilPercent,
    ulong? VramTotalBytes,
    ulong? VramUsedBytes,
    IReadOnlyList<uint> FanPercent,
    IReadOnlyList<string> ThrottleReasons,
    double? PowerInstantWatts = null,
    int? TempMarginC = null);

/// <summary>
/// NVML's cumulative time-in-limit counters at one moment, nanoseconds since the driver loaded. Two of them give the share
/// of the time between that each reason held the clock, with nothing missed between samples (a 2-4 Hz look at the reason
/// bits can miss a short cap). Power is NVML's power policy: the board wall and the rails' current limits alike set it.
/// </summary>
public sealed record LimitCounters(DateTimeOffset At, ulong? PowerNs, ulong? ThermalNs, ulong? SwThermalNs, ulong? HwThermalNs, ulong? ReliabilityNs)
{
    /// <summary>The percentage of the time from <paramref name="start"/> to this one each counter ran, rounded to 0.1;
    /// null for a counter missing at either end (or one that went backwards: the driver restarted).</summary>
    public IReadOnlyDictionary<string, double?> SharesSince(LimitCounters start)
    {
        double ns = (At - start.At).Ticks * 100.0;
        double? Share(ulong? a, ulong? b) => a is { } x && b is { } y && y >= x && ns > 0 ? Math.Round(Math.Min(100, (y - x) / ns * 100), 1) : null;
        return new Dictionary<string, double?>
        {
            ["power_capped_pct"] = Share(start.PowerNs, PowerNs),
            ["thermal_policy_pct"] = Share(start.ThermalNs, ThermalNs),
            ["sw_thermal_slowdown_pct"] = Share(start.SwThermalNs, SwThermalNs),
            ["hw_thermal_slowdown_pct"] = Share(start.HwThermalNs, HwThermalNs),
            ["reliability_pct"] = Share(start.ReliabilityNs, ReliabilityNs),
        };
    }
}

public static class Telemetry
{
    // nvmlClocksThrottleReasons bit values (public NVML headers). 0x400 is not in them: it is named "Reliability Voltage"
    // here, and on the 5090 it is set exactly while the stock curve sits at the rail's MAX (StabilityLab runs).
    private static readonly (ulong Bit, string Name)[] Reasons =
    [
        (0x1, "gpu_idle"), (0x2, "applications_clocks_setting"), (0x4, "sw_power_cap"),
        (0x8, "hw_slowdown"), (0x10, "sync_boost"), (0x20, "sw_thermal_slowdown"),
        (0x40, "hw_thermal_slowdown"), (0x80, "hw_power_brake_slowdown"), (0x100, "display_clock_setting"),
        (0x400, "reliability_voltage"),
    ];

    private static T? Try<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch (NvmlException) { return null; }
    }

    public static TelemetrySnapshot Read(GpuDevice gpu, NvmlApi nvml)
    {
        if (!gpu.HasNvml) throw new InvalidOperationException("This GPU has no matching NVML device.");
        var h = gpu.NvmlHandle;
        var cons = Try(() => nvml.PowerConstraintsMw(h));
        var util = Try(() => nvml.Utilization(h));
        var mem = Try(() => nvml.Memory(h));

        var fans = new List<uint>();
        uint nf = nvml.FanCount(h);
        for (uint i = 0; i < nf; i++) { var f = nvml.FanPercent(h, i); if (f != null) fans.Add(f.Value); }

        ulong tr = nvml.ThrottleReasons(h);
        var names = Reasons.Where(r => (tr & r.Bit) != 0).Select(r => r.Name).ToList();

        static double? W(uint? mw) => mw == null ? null : mw.Value / 1000.0;
        var instant = nvml.FieldValues(h, NvmlField.PowerInstantMw)?[0].Value;
        return new TelemetrySnapshot(
            Try(() => (double)nvml.TemperatureC(h)),
            W(Try(() => nvml.PowerUsageMw(h))),
            W(Try(() => nvml.PowerLimitMw(h))),
            W(Try(() => nvml.PowerDefaultMw(h))),
            W(Try(() => nvml.PowerEnforcedMw(h))),
            cons == null ? null : cons.Value.MinMw / 1000.0,
            cons == null ? null : cons.Value.MaxMw / 1000.0,
            Try(() => nvml.ClockMhz(h, NvmlClock.Graphics)),
            Try(() => nvml.ClockMhz(h, NvmlClock.Memory)),
            Try(() => nvml.MaxClockMhz(h, NvmlClock.Graphics)),
            Try(() => nvml.MaxClockMhz(h, NvmlClock.Memory)),
            util?.Gpu, util?.Memory, mem?.Total, mem?.Used, fans, names,
            instant / 1000.0, nvml.MarginTemperatureC(h));
    }

    /// <summary>The time-in-limit counters now; null when this NVML has no field call.</summary>
    public static LimitCounters? Counters(GpuDevice gpu, NvmlApi nvml)
    {
        if (!gpu.HasNvml) return null;
        var v = nvml.FieldValues(gpu.NvmlHandle, NvmlField.PowerPolicyNs, NvmlField.ThermalPolicyNs, NvmlField.SwThermalSlowdownNs,
            NvmlField.HwThermalSlowdownNs, NvmlField.ReliabilityNs);
        if (v == null) return null;
        ulong? Ns(int i) => v[i].Status != 0 ? null : v[i].Type == 3 ? v[i].Raw : v[i].Value is { } d ? (ulong)d : null;   // ns as 64-bit, no double rounding
        return new LimitCounters(DateTimeOffset.UtcNow, Ns(0), Ns(1), Ns(2), Ns(3), Ns(4));
    }
}
