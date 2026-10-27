using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>
/// The lab's leap (2026-10-27, after six game hangs on the reference card): the soloist heavy then light, over and over, so the
/// clock leaps to the curve's top; a pass counts only where it leapt, a wrong lane fails it, a hang is the GPU fault it leaves.
/// </summary>
public class LeapTests
{
    /// <summary>A soloist on 16 SMs; NVML reads the clock the share that played last asks for.</summary>
    private sealed class FakeLeap : ILoadEngine
    {
        public int ComputeThreads => 1 << 16;
        public int SmCount => 16;
        public ulong MemoryBufferBytes => 1UL << 30;
        public string? SoloUnavailable { get; init; }
        public int SoloBlocksPerSm => 6;
        public long Launches, BadAt = -1, DeadAt = -1;
        public int Reads;
        public double Breadth = 1;
        public readonly List<double> Shares = [];
        public ChunkOutcome RunCompute(uint iterations, bool isReference, int threads = 0) => throw new NotSupportedException();
        public ChunkOutcome RunMemory(uint seed, AccessPattern pattern) => throw new NotSupportedException();
        public ChunkOutcome RunLatency(uint hops) => throw new NotSupportedException();
        public ChunkOutcome RunL2(uint seed) => throw new NotSupportedException();
        public SoloOutcome RunSolo(uint units, uint seed, uint[] mask, IReadOnlyCollection<int> playing)
        {
            Breadth = playing.Count / 16.0;
            Shares.Add(Breadth);
            int r = Volatile.Read(ref Reads);
            SpinWait.SpinUntil(() => Volatile.Read(ref Reads) > r + 1, 1000);
            if (++Launches == DeadAt) return SoloOutcome.Failed("CUDA_ERROR_LAUNCH_TIMEOUT (the context is dead)");
            var bad = Launches == BadAt ? new List<(int, int, int)> { (0, playing.First(), 3) } : [];
            return new SoloOutcome(units * 0.0075, new SoloJudgement(bad, false, playing.Count * 6, 3000, 1, units * 0.0075));
        }
        public void Dispose() { }
    }

    private sealed class ClockSampler(FakeLeap e, Func<double, uint> mhz) : ISampler
    {
        public LabSample Read(double t, int phase)
        {
            var s = new LabSample(t, phase, 150 + 450 * e.Breadth, 70, mhz(e.Breadth), 13801, 99, 0, 600);
            Interlocked.Increment(ref e.Reads);
            return s;
        }
        public void Dispose() { }
    }

    private static (string Verdict, List<string> Reasons, PhaseResult Leap) Run(FakeLeap e, Func<double, uint> mhz, double seconds = 30)
    {
        double now = 0;
        long seen = 0;
        double Clock() { if (e.Launches != seen) { now += (e.Launches - seen) * 0.12; seen = e.Launches; } return now; }
        var cfg = new LabConfig(LabConfig.Parse("leap", seconds), SampleMs: 1);
        var phases = new LabRunner(cfg, e, new ClockSampler(e, mhz), Clock, ms => now += ms / 1000.0, sampleWait: _ => Thread.Yield()).Run(CancellationToken.None);
        var (v, r) = LabRunner.Judge(phases, 1);
        return (v, r, phases[0]);
    }

    [Fact]
    public void The_clock_leaps_every_cycle_and_the_leap_passes()
    {
        var e = new FakeLeap();
        var (verdict, reasons, p) = Run(e, b => b > 0.5 ? 2850u : 3120u);     // the whole chip holds 2850, a quarter leaps to 3120
        Assert.Equal(LabResult.Pass, verdict);
        Assert.Empty(reasons);
        Assert.True(p.Held);
        Assert.InRange(p.Leaps!.Value, 8, 11);                              // 30 s of 1.5 s + 1.5 s cycles
        Assert.Equal(p.Leaps, p.Leapt);
        Assert.Equal(2850, p.HeavyMhz);
        Assert.Equal(3120, p.PeakMhz);
        Assert.Equal(4, p.SoloSms);
        Assert.Equal(3000, p.EffMhz);
        // the halves alternate: whole chip, then a quarter, and back
        Assert.Contains(1.0, e.Shares);
        Assert.Contains(0.25, e.Shares);
        Assert.True(e.Shares.Zip(e.Shares.Skip(1)).Count(s => s.First != s.Second) >= 2 * p.Leaps!.Value - 1);
    }

    [Fact]
    public void A_clock_that_never_leaps_proves_nothing()
    {
        var e = new FakeLeap();
        var (verdict, reasons, p) = Run(e, _ => 2950u);                        // a curve flat where the heavy load already sits
        Assert.Equal(LabResult.Inconclusive, verdict);
        Assert.False(p.Held);
        Assert.Equal(0, p.Leapt);
        Assert.Contains("never made the move", reasons[0]);
    }

    [Fact]
    public void A_wrong_lane_in_the_light_half_fails_it_and_names_the_pipe()
    {
        var e = new FakeLeap { BadAt = 30 };                                   // after calibration and a heavy half: a light chunk
        var (verdict, reasons, p) = Run(e, b => b > 0.5 ? 2850u : 3120u);
        Assert.Equal(LabResult.Fail, verdict);
        Assert.Contains("mma8", reasons[0]);
        Assert.Equal("mma8", p.LaneMismatches!.Single().Lane);
    }

    [Fact]
    public void A_hang_in_the_leap_is_a_gpu_fault()
    {
        var e = new FakeLeap { DeadAt = 30 };
        var (verdict, reasons, p) = Run(e, b => b > 0.5 ? 2850u : 3120u);
        Assert.Equal(LabResult.Fail, verdict);
        Assert.Contains("GPU fault", reasons[0]);
        Assert.Null(p.Held);
    }

    [Fact]
    public void Too_short_a_leap_cannot_be_judged()
    {
        Assert.Null(LeapRules.Held(2, 2, 2850, 3120).Held);
        Assert.True(LeapRules.Held(10, 5, 2850, 3120).Held);
        Assert.False(LeapRules.Held(10, 4, 2850, 3120).Held);
        Assert.True(LeapRules.Leapt(2850, 2940));
        Assert.False(LeapRules.Leapt(2850, 2930));
        Assert.False(LeapRules.Leapt(null, 3120));
        Assert.Equal(LabPhase.Leap, LabConfig.Parse("leap", 60)[0].Item1);
    }
}
