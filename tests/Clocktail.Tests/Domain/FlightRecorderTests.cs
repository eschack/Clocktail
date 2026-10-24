using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public class FlightRecorderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    private static FlightSample S(double sec, uint mhz, uint mv, double w, uint util = 99, int readMs = 5) =>
        new(T0.AddSeconds(sec), readMs, mhz, mv, w, 70, util, 0x4);

    [Fact]
    public void Only_the_last_sixty_seconds_are_kept()
    {
        var r = new FlightRecorder(FlightRecorder.DefaultKeep);
        for (int i = 0; i <= 480; i++) r.Add(S(i * 0.25, 2800, 950, 590));
        var w = r.Freeze();
        Assert.Equal(241, w.Count);
        Assert.Equal(T0.AddSeconds(60), w[0].At);
    }

    /// <summary>The measured hang of 2026-10-18 17:59Z (re/tuning-results §2): 2895 MHz at 910 mV, 598.6 W; then the
    /// limit letting go, 2947 MHz at 970 mV, 514 W; then ~30 s frozen at 3180 MHz, 995-1000 mV, ~126 W.</summary>
    [Fact]
    public void The_sample_of_the_limit_letting_go_is_not_the_crash_point()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));        // 0 .. 19.5 s
        w.Add(S(20.0, 2947, 970, 514));                                             // letting go
        for (int i = 0; i < 60; i++) w.Add(S(20.5 + i * 0.5, 3180, 995 + (uint)(i % 2) * 5, 126));   // frozen
        var p = FlightRecorder.Pick(w, T0.AddSeconds(51));
        Assert.NotNull(p);
        Assert.Equal((2895u, 910u), (p!.Sample.ClockMhz!.Value, p.Sample.CoreMv!.Value));
        Assert.Equal(T0.AddSeconds(19.5), p.Sample.At);

        // A drop in power at the same point (a lighter scene) is not letting go.
        Assert.False(FlightRecorder.LettingGo(S(1, 2895, 910, 450), S(0.5, 2895, 910, 598)));
        Assert.True(FlightRecorder.LettingGo(S(1, 2947, 970, 514), S(0.5, 2895, 910, 598.6)));
    }

    /// <summary>When the GPU stopped answering, for whose crash it is: the first frozen sample of the 17:59Z hang (the live
    /// looks of the recovered driver after it change nothing), or the first blocked read, whichever came first, with the
    /// last look before it; with neither, no hang.</summary>
    [Fact]
    public void The_hang_starts_where_the_readings_froze_or_a_read_blocked()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));        // 0 .. 19.5 s
        w.Add(S(20.0, 2947, 970, 514));                                             // letting go
        for (int i = 0; i < 60; i++) w.Add(S(20.5 + i * 0.5, 3180, 995 + (uint)(i % 2) * 5, 126));   // frozen to 50 s
        Assert.Equal((T0.AddSeconds(20), T0.AddSeconds(20.5)), FlightRecorder.Hang(w, T0.AddSeconds(51)));
        w.Add(S(50.6, 1200, 750, 90, util: 10));                                    // the recovered driver, idle
        w.Add(S(50.8, 1200, 750, 90, util: 8));
        Assert.Equal((T0.AddSeconds(20), T0.AddSeconds(20.5)), FlightRecorder.Hang(w, T0.AddSeconds(51)));
        w.Insert(70, S(34.9, 3180, 1000, 126, readMs: 1500));                       // a read blocked later in the hang
        Assert.Equal(T0.AddSeconds(20.5), FlightRecorder.Hang(w, T0.AddSeconds(51))!.Value.Stopped);

        var steady = Enumerable.Range(0, 80).Select(i => S(i * 0.5, 3045, 985, 380)).ToList();
        Assert.Null(FlightRecorder.Hang(steady, T0.AddSeconds(40)));
        var blocked = new List<FlightSample>(steady) { S(40.2, 3045, 985, 380, readMs: 1800) };
        Assert.Equal((T0.AddSeconds(39.5), T0.AddSeconds(40.2)), FlightRecorder.Hang(blocked, T0.AddSeconds(42)));
    }

    /// <summary>Looks that read nothing (the driver failing every read during the reset) between the frozen tail and a read
    /// that blocks as the driver comes back are the same hang; by themselves they are none (a GPU NVML cannot read reads
    /// nothing all the time).</summary>
    [Fact]
    public void Looks_that_read_nothing_join_the_hang_but_start_none()
    {
        static FlightSample Nothing(double sec) => new(T0.AddSeconds(sec), 5, null, null, null, null, null, null);
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));        // 0 .. 19.5 s
        for (int i = 0; i < 52; i++) w.Add(S(20.5 + i * 0.5, 3180, 1000, 126));  // frozen 20.5 .. 46 s
        for (int i = 0; i < 8; i++) w.Add(Nothing(46.25 + i * 0.25));            // nothing read, 46.25 .. 48 s
        w.Add(S(48.25, 1200, 750, 90, util: 5, readMs: 1500));                    // the driver coming back, slowly
        Assert.Equal((T0.AddSeconds(19.5), T0.AddSeconds(20.5)), FlightRecorder.Hang(w, T0.AddSeconds(51)));

        Assert.Null(FlightRecorder.Hang(Enumerable.Range(0, 80).Select(i => Nothing(i * 0.5)).ToList(), T0.AddSeconds(41)));
    }

    /// <summary>A look that blocked and read nothing is the hang's own (its first look here), not one to cross; a GPU that
    /// reads nothing all along is not bridged back to a slow look long before the reset; a rail read is a reading.</summary>
    [Fact]
    public void A_blocked_look_that_read_nothing_starts_the_hang_and_nothing_bridges_far()
    {
        static FlightSample Nothing(double sec, int readMs = 5) => new(T0.AddSeconds(sec), readMs, null, null, null, null, null, null);
        var w = new List<FlightSample>();
        for (int i = 0; i <= 58; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));      // 0 .. 29 s
        w.Add(Nothing(30, readMs: 2000));                                          // blocked 2 s, then failed
        for (int i = 0; i < 18; i++) w.Add(Nothing(32.25 + i * 0.25));            // failing fast, 32.25 .. 36.5 s
        w.Add(S(37, 2895, 910, 598.6, readMs: 9000));                              // blocked until the driver came back
        Assert.Equal((T0.AddSeconds(29), T0.AddSeconds(30)), FlightRecorder.Hang(w, T0.AddSeconds(47)));

        var none = new List<FlightSample> { Nothing(0, readMs: 1500) };           // a slow first look, 50 s before
        for (int i = 1; i < 100; i++) none.Add(Nothing(1 + i * 0.5));             // nothing to read, ever
        none.Add(Nothing(51, readMs: 1200));                                       // a slow look near the reset
        Assert.Equal(T0.AddSeconds(51), FlightRecorder.Hang(none, T0.AddSeconds(53))!.Value.Stopped);

        // No NVML, but the rail is read: those looks answered, so a slow look 5 s before another is not the same hang.
        var railOnly = Enumerable.Range(0, 100).Select(i => new FlightSample(T0.AddSeconds(i * 0.5), 5, null, 910, null, null, null, null)).ToList();
        railOnly[92] = railOnly[92] with { ReadMs = 1500 };                        // 46 s
        railOnly.Add(new FlightSample(T0.AddSeconds(51), 1200, null, 910, null, null, null, null));
        Assert.Equal(T0.AddSeconds(51), FlightRecorder.Hang(railOnly, T0.AddSeconds(53))!.Value.Stopped);
    }

    /// <summary>On a GPU NVML reads, a look with no clock and no power is NVML failing during the reset even when the rail
    /// still answers: it joins the frozen tail to the slow read of the driver coming back, as a look that read nothing does.</summary>
    [Fact]
    public void Looks_NVML_failed_join_the_hang_though_the_rail_answered()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));        // 0 .. 19.5 s
        for (int i = 0; i < 52; i++) w.Add(S(20.5 + i * 0.5, 3180, 1000, 126));  // frozen 20.5 .. 46 s
        for (int i = 0; i < 8; i++) w.Add(new FlightSample(T0.AddSeconds(46.25 + i * 0.25), 5, null, 1000, null, null, null, null));   // NVML failing, the rail read
        w.Add(S(48.25, 1200, 750, 90, util: 5, readMs: 1500));                    // the driver coming back, slowly
        Assert.Equal((T0.AddSeconds(19.5), T0.AddSeconds(20.5)), FlightRecorder.Hang(w, T0.AddSeconds(51)));
    }

    /// <summary>The hang is the last episode before the reset: an earlier slow look, or the frozen tail of an earlier reset
    /// still in the window, is not it, while a read that blocked through the TDR delay is; a stretch that ended long before
    /// the reset is not this hang either. Taking the first one in the window blamed the trial before the one that hung.</summary>
    [Fact]
    public void The_hang_is_the_last_episode_before_the_reset_not_an_earlier_one()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i <= 60; i++) w.Add(S(i * 0.5, 2895, 910, 598.6, readMs: i == 10 ? 1200 : 5));   // 0 .. 30 s, one slow look at 5 s
        for (int i = 0; i < 40; i++) w.Add(S(30.5 + i * 0.5, 3180, 1000, 126));                         // frozen from 30.5 s to 50 s
        Assert.Equal((T0.AddSeconds(30), T0.AddSeconds(30.5)), FlightRecorder.Hang(w, T0.AddSeconds(51)));

        var x = new List<FlightSample>();
        for (int i = 0; i < 6; i++) x.Add(S(i * 0.5, 2895, 910, 598.6));                      // 0 .. 2.5 s
        for (int i = 0; i < 11; i++) x.Add(S(3 + i * 0.5, 3180, 1000, 126));                  // an earlier reset's frozen tail, 3 .. 8 s
        for (int i = 0; i < 46; i++) x.Add(S(8.5 + i * 0.5, 1500, 800, 120, util: 30));       // answering again, 8.5 .. 31 s
        x.Add(S(31.5, 2895, 910, 598.6, readMs: 20_000));                                     // blocked from 31.5 s until the reset
        Assert.Equal((T0.AddSeconds(31), T0.AddSeconds(31.5)), FlightRecorder.Hang(x, T0.AddSeconds(52)));

        var y = new List<FlightSample>();
        for (int i = 0; i < 20; i++) y.Add(S(i * 0.5, 2895, 910, 598.6));                     // 0 .. 9.5 s
        for (int i = 0; i < 21; i++) y.Add(S(10 + i * 0.5, 3180, 1000, 126));                 // frozen 10 .. 20 s
        for (int i = 0; i < 40; i++) y.Add(S(20.5 + i * 0.5, 1500, 800, 120, util: 30));      // answering, 20.5 .. 40 s
        Assert.Null(FlightRecorder.Hang(y, T0.AddSeconds(45)));
    }

    /// <summary>S-4: the GPU looks hung while its readings sit frozen after power fell, or a read blocks; a steady pinned
    /// load or an old window is not a hang.</summary>
    [Fact]
    public void A_hang_is_seen_while_it_lasts_and_a_steady_load_is_not_one()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598));
        for (int i = 0; i < 12; i++) w.Add(S(20 + i * 0.5, 3180, 1000, 126));
        Assert.Contains("frozen at 3180 MHz", FlightRecorder.HungNow(w, T0.AddSeconds(26)));
        Assert.Null(FlightRecorder.HungNow(w, T0.AddSeconds(40)));                               // the window is old: the recorder stopped

        var steady = Enumerable.Range(0, 80).Select(i => S(i * 0.5, 3045, 985, 380)).ToList();   // pinned at a flat top
        Assert.Null(FlightRecorder.HungNow(steady, T0.AddSeconds(40)));
        var blocked = new List<FlightSample>(steady) { S(40.2, 3045, 985, 380, readMs: 1800) };
        Assert.Contains("blocked for 1800 ms", FlightRecorder.HungNow(blocked, T0.AddSeconds(41)));
    }

    /// <summary>A hang at the top of the curve under nvgwls -l 40: NVML reads the square wave as ~40 % utilization at
    /// ~380 W. It used to leave no crash point (loaded meant 60 % or more).</summary>
    [Fact]
    public void A_light_load_hang_has_a_crash_point_and_idle_still_has_none()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 3045, 985, 380, util: 40));
        for (int i = 0; i < 60; i++) w.Add(S(20 + i * 0.5, 3180, 1000, 120, util: 100));   // frozen, power fell into it
        var p = FlightRecorder.Pick(w, T0.AddSeconds(51));
        Assert.Equal(985u, p!.Sample.CoreMv);
        Assert.False(FlightRecorder.Loaded(S(0, 2100, 850, 90, util: 30)));   // the desktop: not load
    }

    [Fact]
    public void The_crash_point_is_the_last_loaded_sample_before_the_reads_block_not_the_wall_letting_go()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.25, 2820, 955, 597));          // 0 .. 9.75 s in game
        w.Add(S(10.0, 3150, 1035, 120, readMs: 2300));                             // hung: blocked read, wall gone
        w.Add(S(12.5, 3150, 1035, 110));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(14));
        Assert.NotNull(p);
        Assert.Equal(955u, p!.Sample.CoreMv);
        Assert.Equal(T0.AddSeconds(9.75), p.Sample.At);
    }

    [Fact]
    public void Without_a_blocked_read_the_low_power_tail_and_the_last_two_seconds_are_left_out()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 20; i++) w.Add(S(i * 0.5, 2805, 940, 598));            // 0 .. 9.5 s
        w.Add(S(10, 3150, 1035, 140));                                             // the wall let go: low power
        w.Add(S(11.5, 2805, 940, 598));                                            // within 2 s of the reset: may be the hang
        var p = FlightRecorder.Pick(w, T0.AddSeconds(13));
        Assert.Equal(T0.AddSeconds(9.5), p!.Sample.At);
        Assert.Null(FlightRecorder.Pick([S(1, 210, 750, 30, util: 2)], T0.AddSeconds(5)));   // idle: no point
    }

    // 2026-10-18 17:59Z: the hung GPU kept answering without blocking, 3180 MHz at 995–1000 mV, util 100 %, ~125 W,
    // for ~30 s until Windows logged the reset.
    private static IEnumerable<FlightSample> Frozen(double from, double to)
    {
        for (double t = from; t < to; t += 0.25) yield return S(t, 3180, ((int)(t * 4) % 7 == 0) ? 995u : 1000u, 126 - (t - from) * 0.08, util: 100);
    }

    [Fact]
    public void A_hang_that_keeps_answering_is_cut_off_at_the_load_drop()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 120; i++) w.Add(S(i * 0.25, (uint)(2880 + i % 3 * 7), (uint)(915 - i % 2 * 5), 598));   // 0 .. 29.75 s at the wall
        w.AddRange(Frozen(30, 60));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(60.5));
        Assert.NotNull(p);
        Assert.Equal(T0.AddSeconds(29.75), p!.Sample.At);
        Assert.InRange(p.Sample.CoreMv!.Value, 910u, 915u);
        Assert.Contains("froze", p.Why);
    }

    [Fact]
    public void A_window_that_is_frozen_throughout_gives_no_crash_point_rather_than_a_false_one()
    {
        Assert.Null(FlightRecorder.Pick([.. Frozen(0, 20)], T0.AddSeconds(20.5)));
    }

    [Fact]
    public void A_steady_load_at_the_curve_cap_is_not_taken_for_a_hang()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.25, (uint)(3120 + i % 2 * 15), 1040, 310));   // light load around the cap
        for (int i = 40; i < 80; i++) w.Add(S(i * 0.25, 3150, 1040, 305));                      // pinned at the cap for 10 s
        Assert.Null(FlightRecorder.FrozenFrom(w));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(22));
        Assert.Equal(3150u, p!.Sample.ClockMhz);
    }
}
