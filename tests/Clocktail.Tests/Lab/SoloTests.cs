using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>
/// The lab's soloist (2026-10-27, from Etude): the blocks judge each other lane by lane, the window of SMs that play moves
/// on, the share is fitted under the power wall, and a pass counts only where the clock held the point.
/// </summary>
public class SoloTests
{
    private const int Lanes = SoloKernel.Lanes;

    /// <summary>Blocks on SMs 0..sms-1, bps each, all agreeing, each at 2800 MHz for 100 ms.</summary>
    private static (ulong[] Sums, uint[] Sm, ulong[] Cycles, ulong[] Ns) Blocks(int sms, int bps)
    {
        int n = sms * bps;
        var sums = new ulong[n * Lanes];
        var sm = new uint[n];
        var cycles = new ulong[n];
        var ns = new ulong[n];
        for (int b = 0; b < n; b++)
        {
            for (int l = 0; l < Lanes; l++) sums[b * Lanes + l] = 1000UL + (ulong)l;
            sm[b] = (uint)(b % sms);
            ns[b] = 100_000_000;
            cycles[b] = 280_000_000;
        }
        return (sums, sm, cycles, ns);
    }

    [Fact]
    public void Blocks_judge_each_other_lane_by_lane_and_name_the_sm()
    {
        var (sums, sm, cycles, ns) = Blocks(8, 6);
        var clean = SoloRules.Judge(sums, sm, cycles, ns, 6, Enumerable.Range(0, 8).ToList());
        Assert.Empty(clean.Mismatches);
        Assert.False(clean.MajorityLost);
        Assert.Equal(48, clean.ActiveBlocks);
        Assert.Equal(2800, clean.EffMhz);
        Assert.Equal(1, clean.Fill);
        Assert.Equal(100, clean.BusyMsMax, 3);

        sums[13 * Lanes + 3] ^= 8;                    // block 13 (SM 5), the mma8 lane
        var bad = SoloRules.Judge(sums, sm, cycles, ns, 6, Enumerable.Range(0, 8).ToList());
        Assert.Equal([(13, 5, 3)], bad.Mismatches);
        Assert.Equal("mma8", SoloKernel.LaneNames[bad.Mismatches[0].Lane]);
    }

    [Fact]
    public void Silent_sms_are_left_out_and_fill_counts_the_players()
    {
        var (sums, sm, cycles, ns) = Blocks(8, 6);
        for (int b = 0; b < ns.Length; b++)
            if (sm[b] % 2 == 1) { ns[b] = 0; cycles[b] = 0; for (int l = 0; l < Lanes; l++) sums[b * Lanes + l] = 0; }
        ns[0] = 0;                                    // SM 0 ran only 5 of its 6 blocks
        var j = SoloRules.Judge(sums, sm, cycles, ns, 6, [0, 2, 4, 6]);
        Assert.Empty(j.Mismatches);                   // the silent blocks' zeros are not votes
        Assert.Equal(23, j.ActiveBlocks);
        Assert.Equal(0.75, j.Fill);
    }

    [Fact]
    public void Without_a_majority_no_block_can_be_trusted()
    {
        var (sums, sm, cycles, ns) = Blocks(4, 2);
        for (int b = 0; b < 8; b++) sums[b * Lanes + 2] = (ulong)(b % 3);   // the int lane: three values, none over half
        var j = SoloRules.Judge(sums, sm, cycles, ns, 2, [0, 1, 2, 3]);
        Assert.True(j.MajorityLost);
        Assert.All(j.Mismatches, m => Assert.Equal(2, m.Lane));
        Assert.Equal(5, j.Mismatches.Count);           // the tie goes to the smallest value: 0, held by 3 of 8
    }

    [Fact]
    public void The_window_moves_on_by_its_width_and_every_sm_plays_in_turn()
    {
        Assert.Equal(85, SoloRules.Players(170, 0.5));
        Assert.Equal(1, SoloRules.Players(170, 0.001));
        Assert.Equal([0, 1, 2], SoloRules.Window(8, 3, 0));
        Assert.Equal([3, 4, 5], SoloRules.Window(8, 3, 1));
        Assert.Equal([6, 7, 0], SoloRules.Window(8, 3, 2));
        var seen = Enumerable.Range(0, 3).SelectMany(c => SoloRules.Window(170, 60, c)).ToHashSet();
        Assert.Equal(170, seen.Count);
        var m = SoloRules.Mask([0, 31, 32, 169]);
        Assert.Equal(0x8000_0001u, m[0]);
        Assert.Equal(1u, m[1]);
        Assert.Equal(1u << (169 - 160), m[5]);
    }

    [Fact]
    public void The_share_is_fitted_under_the_wall()
    {
        Assert.Equal(1.0, SoloRules.Next(1.0, 500, 600));            // off the wall: as it is
        Assert.Equal(0.65, SoloRules.Next(1.0, 700, 600));           // 1.0 x (510-150)/(700-150) = 0.654, on the 0.05 grid
        Assert.Equal(0.65, SoloRules.Next(0.8, 580, 600));           // 0.8 x 360/430 = 0.67, on the grid
        Assert.Equal(0.3, SoloRules.Next(0.4, 571, 600));            // 0.4 x 360/421 = 0.34: at least one step lighter
        Assert.Equal(0.2, SoloRules.Next(0.3, 900, 600));            // never under the lightest share
        Assert.Null(SoloRules.Next(0.2, 600, 600));                  // on the wall at the lightest share: nothing left
        Assert.Equal(1.0, SoloRules.Next(1.0, 600, 0));              // no limit read: nothing to fit to
    }

    [Fact]
    public void A_pass_counts_only_where_the_clock_held_the_point()
    {
        Assert.Equal((true, null), SoloRules.Held(3011, 3060, 1.0, 560, 600));
        Assert.False(SoloRules.Held(2700, 2800, 1.0, 590, 600).Held);    // on the wall: the limiter's clock
        Assert.False(SoloRules.Held(3011, 3060, 0.8, 500, 600).Held);    // SMs meant to play did not all run
        Assert.False(SoloRules.Held(2500, 3007, 1.0, 400, 600).Held);    // NVML read a clock the SMs did not run (a pulsed load's lie)
        Assert.Null(SoloRules.Held(null, 3000, 1.0, 400, 600).Held);
        Assert.Contains("power wall", SoloRules.Held(2700, 2800, 1.0, 590, 600).Why);
    }

    /// <summary>A soloist on 16 SMs whose board draws base + per-SM power; the clock moves with the launches.</summary>
    private sealed class FakeSolo : ILoadEngine
    {
        public int ComputeThreads => 1 << 16;
        public int SmCount => 16;
        public ulong MemoryBufferBytes => 1UL << 30;
        public string? SoloUnavailable { get; init; }
        public int SoloBlocksPerSm => 6;
        public long Launches, BadAt = -1;
        public int Reads;
        public double Breadth = 1, EffMhz = 2750;
        public ChunkOutcome RunCompute(uint iterations, bool isReference, int threads = 0) => throw new NotSupportedException();
        public ChunkOutcome RunMemory(uint seed, AccessPattern pattern) => throw new NotSupportedException();
        public ChunkOutcome RunLatency(uint hops) => throw new NotSupportedException();
        public ChunkOutcome RunL2(uint seed) => throw new NotSupportedException();
        public SoloOutcome RunSolo(uint units, uint seed, uint[] mask, IReadOnlyCollection<int> playing)
        {
            Breadth = playing.Count / 16.0;
            // one NVML sample of this launch's load before it returns, as a real launch of 120 ms would get
            int r = Volatile.Read(ref Reads);
            SpinWait.SpinUntil(() => Volatile.Read(ref Reads) > r + 1, 1000);
            var bad = ++Launches == BadAt ? new List<(int, int, int)> { (0, playing.First(), 2) } : [];
            return new SoloOutcome(units * 0.0075, new SoloJudgement(bad, false, playing.Count * 6, EffMhz, 1, units * 0.0075));
        }
        public void Dispose() { }
    }

    private sealed class PowerSampler(FakeSolo e, Func<double, double> watts) : ISampler
    {
        public LabSample Read(double t, int phase)
        {
            var s = new LabSample(t, phase, watts(e.Breadth), 70, 2800, 13801, 99, 0, 600);
            Interlocked.Increment(ref e.Reads);
            return s;
        }
        public void Dispose() { }
    }

    private static (string Verdict, List<string> Reasons, PhaseResult Solo) Run(FakeSolo e, Func<double, double> watts, double seconds = 40)
    {
        double now = 0;
        long seen = 0;
        double Clock() { if (e.Launches != seen) { now += (e.Launches - seen) * 0.12; seen = e.Launches; } return now; }
        var cfg = new LabConfig(LabConfig.Parse("solo", seconds), SampleMs: 1);
        var phases = new LabRunner(cfg, e, new PowerSampler(e, watts), Clock, ms => now += ms / 1000.0, sampleWait: _ => Thread.Yield()).Run(CancellationToken.None);
        var (v, r) = LabRunner.Judge(phases, 1);
        return (v, r, phases[0]);
    }

    [Fact]
    public void The_soloist_fits_under_the_wall_holds_and_passes()
    {
        var e = new FakeSolo();
        var (verdict, reasons, p) = Run(e, b => 150 + 550 * b);       // whole chip 700 W of 600: on the wall
        Assert.Equal(LabResult.Pass, verdict);
        Assert.Empty(reasons);
        Assert.True(p.Held);
        Assert.InRange(p.SoloBreadth!.Value, 0.5, 0.8);
        Assert.InRange(p.SoloSms!.Value, 8, 13);
        Assert.Equal(2750, p.EffMhz);
        Assert.Equal(600, p.LimitW);
        Assert.Equal(16000u, p.SoloUnits);                            // 120 ms of 7.5 us units
    }

    [Fact]
    public void A_wrong_lane_fails_it_and_names_the_sm_and_the_pipe()
    {
        var e = new FakeSolo { BadAt = 20 };
        var (verdict, reasons, p) = Run(e, b => 150 + 300 * b);
        Assert.Equal(LabResult.Fail, verdict);
        Assert.Contains("int", reasons[0]);
        Assert.Single(p.LaneMismatches!);
        Assert.Equal("int", p.LaneMismatches![0].Lane);
    }

    [Fact]
    public void A_soloist_that_never_got_off_the_wall_proves_nothing()
    {
        var e = new FakeSolo();
        var (verdict, reasons, p) = Run(e, _ => 600);                  // the wall at any share
        Assert.Equal(LabResult.Inconclusive, verdict);
        Assert.False(p.Held);
        Assert.Contains("did not hold", reasons[0]);
        Assert.Equal(SoloRules.MinBreadth, p.SoloBreadth);
    }

    [Fact]
    public void A_stretched_clock_is_not_a_held_point()
    {
        var e = new FakeSolo { EffMhz = 2500 };                       // the SMs ran 2500 while NVML read 2800
        var (verdict, _, p) = Run(e, b => 150 + 300 * b);
        Assert.Equal(LabResult.Inconclusive, verdict);
        Assert.False(p.Held);
    }

    [Fact]
    public void A_driver_that_cannot_compile_it_leaves_the_run_inconclusive_not_failed()
    {
        var e = new FakeSolo { SoloUnavailable = "the soloist's kernel did not load: CUDA_ERROR_INVALID_PTX" };
        var (verdict, reasons, p) = Run(e, b => 300);
        Assert.Equal(LabResult.Inconclusive, verdict);
        Assert.Contains("did not load", reasons[0]);
        Assert.Null(p.Error);
    }

    [Fact]
    public void The_soloist_is_a_phase_and_its_kernel_is_embedded()
    {
        Assert.Equal(LabPhase.Solo, LabConfig.Parse("solo", 60)[0].Item1);
        var ptx = SoloKernel.Ptx();
        Assert.Contains(".entry solo(", ptx);
        Assert.Contains("mma.sync.aligned.m16n8k32.row.col.s32.s8.s8.s32", ptx);
        Assert.Contains("dp4a.s32.s32", ptx);
        Assert.Contains(".target sm_80", ptx);
    }
}
