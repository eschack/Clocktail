using System.Diagnostics;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Rail = Clocktail.Domain.Rails.Rail;

namespace Clocktail.Cli;

/// <summary>
/// One read-only sample of where the power goes, for the UI's power-split view and the optimizer: board power and
/// limits, every clock domain, the working voltage of each rail, temperature and limit reasons. Takes no write window
/// and no lock; a caller polls it at its own rate. What is not decoded is said so ("undecoded"), never estimated.
/// </summary>
internal static class PowerSplitCommands
{
    public const string Usage = """

        Power split (read-only, one sample):
          --power-split            JSON: board power and limits, gfx/mem/xbar/sys/video clocks, NVVDD/MSVDD voltage, temperature, limit reasons
          --power-split-topology   also read the power topology (board power twice, in 1/1000 % of the default limit and
                                   in W; adds 5-17 ms)
        """;

    private const string Undecoded = "undecoded";

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Contains("--power-split")) return false;

        int? gpuIndex = null; string? gpuId = null; bool topology = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--power-split": break;
                case "--power-split-topology": topology = true; break;
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with --power-split");
            }
        }

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        var rails = RailsReader.Read(ctx.Hw, ctx.Session);   // layout and mask; not part of the sample's timing
        CurrentLimits? limits = null;
        try { limits = CurrentLimitsReader.Read(ctx.Hw, ctx.Session); } catch (InvalidOperationException) { }

        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var clocks = PowerSplitReader.ReadClocks(ctx.Hw, ctx.Session);
        double clocksMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var volts = PowerSplitReader.ReadVoltages(ctx.Hw, ctx.Session, rails);
        double voltsMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var t = mgr.Nvml != null && ctx.Gpu.HasNvml ? Telemetry.Read(ctx.Gpu, mgr.Nvml) : null;
        double nvmlMs = sw.Elapsed.TotalMilliseconds;
        // Each rail's output power from the power monitor (decoded 2026-10-19); the layout read stays out of the timing.
        sw.Restart();
        MonitorReading? nvvddOut = null, msvddOut = null;
        if (limits != null)
        {
            var readings = CurrentLimitsReader.Monitor(ctx.Hw, ctx.Session, limits.Channels);
            nvvddOut = limits.Output(Rail.Nvvdd, readings);
            msvddOut = limits.Output(Rail.Msvdd, readings);
        }
        double monitorMs = sw.Elapsed.TotalMilliseconds;
        double sampleMs = total.Elapsed.TotalMilliseconds;
        double? topoMs = null;
        IReadOnlyList<(uint Id, uint Raw)>? topo = null;
        if (topology)
        {
            sw.Restart();
            topo = PowerSplitReader.ReadTopology(ctx.Hw, ctx.Session);
            topoMs = sw.Elapsed.TotalMilliseconds;
        }

        static uint? Mhz(IReadOnlyDictionary<int, uint> c, int id) => c.TryGetValue(id, out var k) ? (k + 500) / 1000 : null;
        static double? Mv(IReadOnlyDictionary<Rail, uint> v, Rail r) => v.TryGetValue(r, out var uv) ? uv / 1000.0 : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("o"),
            power = new
            {
                board_w = t?.PowerWatts,
                limit_requested_w = t?.PowerLimitWatts,
                limit_enforced_w = t?.PowerEnforcedWatts,
                limit_default_w = t?.PowerDefaultWatts,
                nvvdd_w = nvvddOut?.Mw / 1000.0,
                nvvdd_a = nvvddOut?.Ma / 1000.0,
                msvdd_w = msvddOut?.Mw / 1000.0,
                msvdd_a = msvddOut?.Ma / 1000.0,
                topology = topo?.Select(e => new
                {
                    id = e.Id, label = e.Id switch { 0 => "board, steady", 1 => "board, wider spread", _ => Undecoded }, raw = e.Raw,
                    unit = "1/1000 % of the default power limit",
                    w = t?.PowerDefaultWatts is { } d ? Math.Round(PowerTopologyLayout.Watts(e.Raw, d), 1) : (double?)null,
                }),
            },
            clocks_mhz = new
            {
                graphics = Mhz(clocks, AllClocksLayout.Graphics),
                memory = Mhz(clocks, AllClocksLayout.Memory),
                xbar = Mhz(clocks, AllClocksLayout.Xbar),
                sys = Mhz(clocks, AllClocksLayout.Sys),
                video = Mhz(clocks, AllClocksLayout.Video),
            },
            voltage_mv = new { nvvdd = Mv(volts, Rail.Nvvdd), msvdd = Mv(volts, Rail.Msvdd) },
            temperature_c = t?.TemperatureC,
            limit_reasons = t?.ThrottleReasons,
            timing_ms = new
            {
                sample = Math.Round(sampleMs, 3), clocks = Math.Round(clocksMs, 3), voltages = Math.Round(voltsMs, 3), nvml = Math.Round(nvmlMs, 3), monitor = Math.Round(monitorMs, 3),
                topology = topoMs is { } x ? Math.Round(x, 3) : (double?)null,
            },
        }, CliContext.Json));
        return true;
    }
}
