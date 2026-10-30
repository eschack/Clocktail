using System.Globalization;
using Clocktail.Domain.Game;

namespace Clocktail.Lab.Game;

/// <summary>
/// What one step asks Quake II RTX to render. Every value is absolute, so a step never depends on the one before it (a
/// map's own .cfg, a renderer restart or a lost command cannot leave a setting behind unnoticed: the state query reads
/// every one back). Scale is the internal resolution in percent of the window per axis (the game's viewsize, 25..200:
/// over 100 renders more pixels than the window shows). The window itself never changes size.
/// </summary>
public sealed record Render(
    int Scale, int MaxFps = 0, bool Vsync = false,
    int DrsTarget = 0, int DrsMin = 50, int DrsMax = 100,
    double Bounces = 1, int Reflect = 2, bool Caustics = true, bool GodRays = true, bool Bloom = true,
    bool Denoiser = true, bool Fsr = false, int Fov = 90, int Projection = 0, int Sun = 5, double Particles = 1,
    bool Photo = false)
{
    /// <summary>The game's own dynamic resolution (it moves the scale every few frames to hold <see cref="DrsTarget"/> fps).</summary>
    public bool Drs => DrsTarget > 0;

    /// <summary>The console variables that set this state, in the order they are written (DRS's bounds before DRS itself).</summary>
    public IReadOnlyList<(string Name, string Value)> Cvars()
    {
        static string B(bool b) => b ? "1" : "0";
        static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        var list = new List<(string, string)>();
        if (Drs)
        {
            list.Add(("drs_minscale", N(DrsMin)));
            list.Add(("drs_maxscale", N(DrsMax)));
            list.Add(("drs_target", N(DrsTarget)));
        }
        list.AddRange([
            ("drs_enable", B(Drs)), ("viewsize", N(Scale)), ("r_maxfps", N(MaxFps)), ("vid_vsync", B(Vsync)),
            ("pt_num_bounce_rays", N(Bounces)), ("pt_reflect_refract", N(Reflect)), ("pt_caustics", B(Caustics)),
            ("gr_enable", B(GodRays)), ("bloom_enable", B(Bloom)), ("flt_enable", B(Denoiser)), ("flt_fsr_enable", B(Fsr)),
            ("fov", N(Fov)), ("pt_projection", N(Projection)), ("sun_preset", N(Sun)), ("cl_particle_num_factor", N(Particles)),
            ("pt_accumulation_rendering", B(Photo)), ("pt_dof", B(Photo)),
        ]);
        return list;
    }

    /// <summary>Short words for the log: "100 % uncapped, 2 bounces, reflect 8, fov 110".</summary>
    public string Words()
    {
        var parts = new List<string> { Drs ? $"DRS {DrsMin}-{DrsMax} % at {DrsTarget} fps" : $"{Scale} %" };
        parts.Add(Vsync ? "vsync" : MaxFps > 0 ? $"cap {MaxFps} fps" : "uncapped");
        parts.Add(Inv($"{Bounces:0.#} bounce{(Bounces == 1 ? "" : "s")}, reflect {Reflect}"));
        if (!Denoiser) parts.Add("no denoiser");
        if (Fsr) parts.Add("FSR");
        if (Projection != 0) parts.Add($"projection {Projection}");
        if (Fov != 90) parts.Add($"fov {Fov}");
        if (Sun == GameScript.SunTimeLapse) parts.Add("sun time-lapse");
        if (Particles > 1) parts.Add(Inv($"particles x{Particles:0.#}"));
        if (Photo) parts.Add("photo mode");
        return string.Join(", ", parts);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Where a step plays: the bundled demo at a second of it (a seek within it, or the demo started again), or a live
/// map the script plays itself.</summary>
public sealed record Scene(string Kind, double Seek = 0, string? Map = null)
{
    public const string Demo = "demo", Play = "play";
}

/// <summary>
/// A card-side change at a step's start, written by the service as a trial: power (the board wall, mW; never above the
/// wall the run started under), fans (a fixed duty in %, 0 = back to the fan curve; <see cref="ReleaseAtC"/>: the duty goes
/// back to the curve once the GPU reaches that temperature, a heat soak), boost (1 = Boost Lock, the clock held at the
/// curve's top even at idle, 0 = released).
/// </summary>
public sealed record Lever(string Kind, uint Value, string Why, int? ReleaseAtC = null)
{
    public const string Power = GameCatalog.Power, Fans = GameCatalog.Fans, Boost = GameCatalog.Boost;

    public string Words() => Kind switch
    {
        Power => string.Create(CultureInfo.InvariantCulture, $"wall {Value / 1000.0:0} W"),
        Fans => Value == 0 ? "fans on their curve" : ReleaseAtC is { } c ? $"fans {Value} % until {c} °C" : $"fans {Value} %",
        Boost => Value != 0 ? "Boost Lock on" : "Boost Lock off",
        _ => Kind,
    };
}

/// <summary>
/// One step of the script. Kind says what the load is for the judge's coverage: heavy (uncapped at full scale or more: the
/// wall, the middle of the curve), light (little work, so the clock climbs to the curve's top), capped (full-quality frames
/// under a frame cap: each frame a burst at the top), idle (almost nothing, a menu's load), photo (accumulated reference
/// path tracing), mixed (the game's own resolution scaling, a ramp). A light, capped or idle step right after a heavy one
/// is the jump onto the top games make; a heavy one after idle is the load arriving at the top.
/// </summary>
public sealed record GameStep(int Index, string Pattern, string Name, string Kind, double Seconds, Render Render,
    Scene? Scene = null, IReadOnlyList<string>? Actions = null, IReadOnlyList<Lever>? Levers = null, string? RtApi = null)
{
    public const string Heavy = "heavy", Light = "light", Capped = "capped", Idle = "idle", Photo = "photo", Mixed = "mixed";
    public bool Lowers => Kind is Light or Capped or Idle;
}

/// <summary>The whole script, in order; the runner plays it until the credited time reaches <see cref="Seconds"/>.</summary>
public sealed record GamePlan(int Seed, double Seconds, IReadOnlyList<GameStep> Steps, IReadOnlyList<string> Epochs)
{
    public double Nominal => Steps.Sum(s => s.Seconds);
}

/// <summary>What the planner may use. StartPowerMw: the wall in force when the run starts (no power step goes above it);
/// MinPowerMw: the board's lowest. Maps: the live maps the installed data has (empty: no play pattern).</summary>
public sealed record GamePlanOptions(
    double Seconds, int Seed, IReadOnlyList<string> Patterns, IReadOnlyList<string> Maps, int MaxScale,
    IReadOnlyList<string> Levers, uint StartPowerMw = 0, uint MinPowerMw = 0, int RefreshHz = 60,
    int WarmFanPct = GameScript.WarmFanPct, int WarmC = 76);

/// <summary>
/// The director: a seeded script of load patterns over Quake II RTX, the card-side levers on a slower clock under them.
/// The same seed and options give the same script. The patterns:
/// <list type="bullet">
/// <item>heavy: path tracing flat out (full scale or supersampled, two bounces, reflections, caustics, god rays): the wall.</item>
/// <item>leap: heavy and light in turn every 0.5-3 s, the light half still path tracing at a low scale under a frame cap: the
/// jump onto the curve's top with graphics work running, the move that hung the reference card in two games.</item>
/// <item>pulse: full-quality frames under a low frame cap (20-72 fps) or vsync: every frame a burst at the top clock.</item>
/// <item>burst: almost idle (10-15 fps at 25 %), then flat out at the highest scale for 0.3-2 s, over and over: the load step
/// arriving at the top clock.</item>
/// <item>ramp: the internal resolution walked up and down in 5-25 % steps every 0.25-1 s, uncapped.</item>
/// <item>drs: the game's own dynamic resolution scaling chasing a frame rate: the load moving every few frames.</item>
/// <item>cuts: the demo seeked to a new camera every 1-3 s: denoiser history, textures and geometry thrown away each time.</item>
/// <item>toggles: the expensive features switched every 1-2 s (bounces, reflections, caustics, god rays, the denoiser, FSR).</item>
/// <item>wide: panoramic projections at wide fields of view (everything around the camera traced).</item>
/// <item>sky: the sun in time-lapse, lighting changing every frame.</item>
/// <item>photo: photo mode, accumulated reference path tracing with depth of field at full scale or more, then back.</item>
/// <item>play: a live shareware map played by the script: god mode, every weapon, the camera spinning, rockets, the
/// hyperblaster and flares firing (explosions, dynamic lights, particles), short dashes; heavy and light in turn mid-fight.</item>
/// <item>restart: the renderer restarted on the other ray-tracing path (ray query / ray tracing pipeline), its window made again.</item>
/// </list>
/// </summary>
public static class GameScript
{
    public const string Heavy = GameCatalog.Heavy, Leap = GameCatalog.Leap, Pulse = GameCatalog.Pulse, Burst = GameCatalog.Burst,
        Ramp = GameCatalog.Ramp, Drs = GameCatalog.Drs, Cuts = GameCatalog.Cuts, Toggles = GameCatalog.Toggles, Wide = GameCatalog.Wide,
        Sky = GameCatalog.Sky, PhotoMode = GameCatalog.Photo, Play = GameCatalog.Play, Restart = GameCatalog.Restart;

    public const int SunTimeLapse = 2, WarmFanPct = 50, FanMinPct = 30;
    /// <summary>The demo's camera sections the script seeks within (q2demo1 is about 63 s; a little is kept free at its end).</summary>
    public const double DemoSeconds = 58;
    /// <summary>The weapons the play pattern fires (Quake II RTX adds the flare gun: each flare is a light).</summary>
    public static readonly IReadOnlyList<string> Weapons = ["Rocket Launcher", "HyperBlaster", "BFG10K", "Chaingun", "Grenade Launcher", "flare gun", "Railgun"];
    /// <summary>The live maps the script plays, in the order it prefers them: the shareware three, then the full game's first.</summary>
    public static readonly IReadOnlyList<string> PlayMaps = ["demo1", "demo2", "demo3", "base1", "base2", "base3"];

    /// <summary>The console commands a step may send besides its settings: the play pattern's, nothing that saves, quits,
    /// binds keys or reaches the network.</summary>
    public static bool AllowedAction(string a)
    {
        string head = a.Split(' ', 2)[0];
        return head is "god" or "notarget" or "give" or "use" or "centerview"
            or "+attack" or "-attack" or "+right" or "-right" or "+left" or "-left" or "+forward" or "-forward" or "+back" or "-back"
            or "+lookup" or "-lookup" or "+lookdown" or "-lookdown" or "+moveup" or "-moveup" or "cl_yawspeed" or "cl_pitchspeed";
    }

    /// <summary>Every button the play pattern may hold, released when it ends or the scene changes.</summary>
    public static readonly IReadOnlyList<string> Releases = ["-attack", "-right", "-left", "-forward", "-back", "-lookup", "-lookdown", "-moveup"];

    public static GamePlan Plan(GamePlanOptions o)
    {
        if (!double.IsFinite(o.Seconds) || o.Seconds < 30) throw new ArgumentException("a game test runs 30 s or more");
        if (o.MaxScale is < 100 or > 200) throw new ArgumentException("the highest scale is 100..200 %");
        var patterns = o.Patterns.Where(p => p != Play || o.Maps.Count > 0).ToList();
        if (patterns.Count == 0) throw new ArgumentException("no pattern to play");
        var r = new Random(o.Seed);
        var steps = new List<Draft>();
        double t = 0;

        // A heavy warm-up first: the card warms, the wall is reached, the first frames are the baseline.
        Add(steps, ref t, HeavyPattern(r, o, warmup: true));
        // Every enabled pattern once in a shuffled order (a deck), then weighted draws; never the same one twice in a row.
        var deck = patterns.OrderBy(_ => r.Next()).ToList();
        string? last = Heavy;
        while (t < o.Seconds)
        {
            string p;
            if (deck.Count > 0) { p = deck[0]; deck.RemoveAt(0); if (p == last && deck.Count > 0) { deck.Add(p); p = deck[0]; deck.RemoveAt(0); } }
            else p = Weighted(r, patterns, last);
            last = p;
            Add(steps, ref t, Pattern(p, r, o));
        }

        var (levers, epochs) = LeverSchedule(steps, o, r);
        var list = steps.Select((d, i) => new GameStep(i, d.Pattern, d.Name, d.Kind, d.Seconds, d.Render, d.Scene, d.Actions,
            levers.TryGetValue(i, out var l) ? l : null, d.RtApi)).ToList();
        return new GamePlan(o.Seed, o.Seconds, list, epochs);
    }

    private sealed record Draft(string Pattern, string Name, string Kind, double Seconds, Render Render, Scene? Scene = null,
        IReadOnlyList<string>? Actions = null, string? RtApi = null)
    {
        public double Start { get; set; }
    }

    private static void Add(List<Draft> steps, ref double t, IEnumerable<Draft> pattern)
    {
        foreach (var d in pattern)
        {
            d.Start = t;
            steps.Add(d);
            t += d.Seconds;
        }
    }

    private static readonly Dictionary<string, int> Weights = new()
    {
        [Heavy] = 2, [Leap] = 4, [Pulse] = 2, [Burst] = 3, [Ramp] = 1, [Drs] = 1, [Cuts] = 1, [Toggles] = 1, [Wide] = 1,
        [Sky] = 1, [PhotoMode] = 1, [Play] = 2, [Restart] = 1,
    };

    private static string Weighted(Random r, List<string> patterns, string? last)
    {
        var pool = patterns.Count > 1 ? patterns.Where(p => p != last).ToList() : patterns;
        int total = pool.Sum(p => Weights[p]), x = r.Next(total);
        foreach (var p in pool) { x -= Weights[p]; if (x < 0) return p; }
        return pool[^1];
    }

    private static T Pick<T>(Random r, params T[] xs) => xs[r.Next(xs.Length)];
    private static double Between(Random r, double lo, double hi, double step = 0.25) => lo + Math.Round(r.NextDouble() * (hi - lo) / step) * step;
    private static int Clamp(int scale, GamePlanOptions o) => Math.Clamp(scale, 25, o.MaxScale);
    private static Scene DemoAt(Random r, double seconds) => new(Scene.Demo, Math.Floor(Between(r, 0, Math.Max(0, DemoSeconds - Math.Min(seconds, 40) - 2), 1)));

    /// <summary>Path tracing flat out: full scale or supersampled, the expensive features on.</summary>
    private static Render HeavyRender(Random r, GamePlanOptions o) => new(
        Scale: Clamp(Pick(r, 100, 100, 100, 125, 150, o.MaxScale), o), Bounces: Pick(r, 1.0, 2, 2), Reflect: Pick(r, 2, 4, 6, 8),
        Caustics: true, GodRays: true, Bloom: true, Fov: Pick(r, 75, 90, 100, 110), Sun: Pick(r, 3, 4, 5, 6, 7, 8, SunTimeLapse),
        Particles: Pick(r, 1.0, 2, 3));

    /// <summary>Little work, still path tracing: a low scale under a frame cap, so the board lets go and the clock climbs.</summary>
    private static Render LightRender(Random r, GamePlanOptions o) => new(
        Scale: Pick(r, 25, 33, 40, 50), MaxFps: Pick(r, 60, 90, 120, 144, 240, Math.Clamp(o.RefreshHz, 30, 480)),
        Bounces: Pick(r, 0, 0.5, 1), Reflect: Pick(r, 0, 1, 2), Caustics: r.Next(2) == 0, GodRays: r.Next(2) == 0, Fov: Pick(r, 75, 90),
        Sun: Pick(r, 4, 5, 6, 7));

    private static Render IdleRender(Random r) => new(Scale: 25, MaxFps: Pick(r, 10, 12, 15), Bounces: 0, Reflect: 0, Caustics: false, GodRays: false, Bloom: false);

    private static IEnumerable<Draft> Pattern(string p, Random r, GamePlanOptions o) => p switch
    {
        Heavy => HeavyPattern(r, o, warmup: false),
        Leap => LeapPattern(r, o),
        Pulse => PulsePattern(r, o),
        Burst => BurstPattern(r, o),
        Ramp => RampPattern(r, o),
        Drs => DrsPattern(r, o),
        Cuts => CutsPattern(r, o),
        Toggles => TogglesPattern(r, o),
        Wide => WidePattern(r, o),
        Sky => SkyPattern(r, o),
        PhotoMode => PhotoPattern(r, o),
        Play => PlayPattern(r, o),
        Restart => RestartPattern(r, o),
        _ => throw new ArgumentException($"unknown pattern {p}"),
    };

    private static IEnumerable<Draft> HeavyPattern(Random r, GamePlanOptions o, bool warmup)
    {
        double s = warmup ? 20 : Between(r, 8, 20, 1);
        yield return new(Heavy, warmup ? "warm-up" : "sustain", GameStep.Heavy, s, warmup ? new Render(100, Bounces: 2, Reflect: 4) : HeavyRender(r, o), DemoAt(r, s));
    }

    private static IEnumerable<Draft> LeapPattern(Random r, GamePlanOptions o)
    {
        var heavy = HeavyRender(r, o) with { Scale = Clamp(Pick(r, 100, 100, 125), o) };
        var light = LightRender(r, o);
        int cycles = r.Next(4, 11);
        Scene? scene = DemoAt(r, cycles * 4);
        for (int i = 0; i < cycles; i++)
        {
            yield return new(Leap, "leap-heavy", GameStep.Heavy, Between(r, 1, 3, 0.5), heavy, scene);
            scene = null;
            yield return new(Leap, "leap-light", GameStep.Light, Between(r, 0.5, 3, 0.5), light);
        }
    }

    private static IEnumerable<Draft> PulsePattern(Random r, GamePlanOptions o)
    {
        int n = r.Next(1, 3);
        Scene? scene = DemoAt(r, 25);
        for (int i = 0; i < n; i++)
        {
            bool vsync = r.Next(5) == 0 && o.RefreshHz is >= 30 and <= 240;
            var render = HeavyRender(r, o) with { MaxFps = vsync ? 0 : Pick(r, 20, 24, 30, 40, 45, 50, 60, 72), Vsync = vsync };
            yield return new(Pulse, vsync ? "pulse-vsync" : $"pulse-{render.MaxFps}", GameStep.Capped, Between(r, 6, 15, 1), render, scene);
            scene = null;
        }
    }

    private static IEnumerable<Draft> BurstPattern(Random r, GamePlanOptions o)
    {
        var heavy = HeavyRender(r, o) with { Scale = Clamp(Pick(r, 100, 125, 150, o.MaxScale), o), Bounces = 2, Reflect = 8 };
        int n = r.Next(3, 9);
        Scene? scene = DemoAt(r, n * 3);
        for (int i = 0; i < n; i++)
        {
            yield return new(Burst, "burst-idle", GameStep.Idle, Between(r, 0.5, 3, 0.25), IdleRender(r), scene);
            scene = null;
            yield return new(Burst, "burst-load", GameStep.Heavy, Between(r, 0.25, 2, 0.25), heavy);
        }
    }

    private static IEnumerable<Draft> RampPattern(Random r, GamePlanOptions o)
    {
        int top = r.Next(4) == 0 ? o.MaxScale : 100, step = Pick(r, 5, 10, 25);
        double dwell = Pick(r, 0.25, 0.5, 1.0);
        var baseRender = HeavyRender(r, o) with { Bounces = 1, Reflect = 2 };
        var scales = new List<int>();
        for (int s = 25; s < top; s += step) scales.Add(s);
        scales.Add(top);
        scales.AddRange(Enumerable.Reverse(scales).Skip(1));
        Scene? scene = DemoAt(r, scales.Count * dwell);
        foreach (int s in scales)
        {
            yield return new(Ramp, $"ramp-{s}", s >= 75 ? GameStep.Heavy : GameStep.Mixed, dwell, baseRender with { Scale = s }, scene);
            scene = null;
        }
    }

    private static IEnumerable<Draft> DrsPattern(Random r, GamePlanOptions o)
    {
        int max = Pick(r, 100, Math.Min(150, o.MaxScale), o.MaxScale), min = Pick(r, 25, 40, 50);
        var render = HeavyRender(r, o) with { Scale = 100, DrsTarget = Pick(r, 45, 60, 90, 120, 144, 165), DrsMin = min, DrsMax = Math.Max(max, 50) };
        double s = Between(r, 10, 20, 1);
        yield return new(Drs, $"drs-{render.DrsTarget}", GameStep.Mixed, s, render, DemoAt(r, s));
    }

    private static IEnumerable<Draft> CutsPattern(Random r, GamePlanOptions o)
    {
        var render = HeavyRender(r, o) with { Scale = Clamp(Pick(r, 100, 125), o) };
        int n = r.Next(4, 9);
        for (int i = 0; i < n; i++)
        {
            double s = Between(r, 1, 3, 0.5);
            yield return new(Cuts, "cut", GameStep.Heavy, s, render, DemoAt(r, s));
        }
    }

    private static IEnumerable<Draft> TogglesPattern(Random r, GamePlanOptions o)
    {
        int n = r.Next(6, 13);
        Scene? scene = DemoAt(r, n * 2);
        for (int i = 0; i < n; i++)
        {
            bool rich = i % 2 == 0;
            var render = new Render(Scale: 100, Bounces: rich ? Pick(r, 1.0, 2) : Pick(r, 0, 0.5), Reflect: rich ? Pick(r, 4, 8) : Pick(r, 0, 1),
                Caustics: rich || r.Next(3) == 0, GodRays: rich || r.Next(3) == 0, Bloom: r.Next(4) != 0, Denoiser: rich || r.Next(3) != 0,
                Fsr: !rich && r.Next(2) == 0, Fov: 90, Sun: Pick(r, 4, 5, 6));
            yield return new(Toggles, rich ? "toggles-rich" : "toggles-lean", GameStep.Heavy, Between(r, 1, 2, 0.5), render, scene);
            scene = null;
        }
    }

    private static IEnumerable<Draft> WidePattern(Random r, GamePlanOptions o)
    {
        int n = r.Next(1, 3);
        Scene? scene = DemoAt(r, 25);
        for (int i = 0; i < n; i++)
        {
            int projection = r.Next(1, 6);
            var render = HeavyRender(r, o) with { Scale = 100, Projection = projection, Fov = Pick(r, 100, 115, 130) };
            yield return new(Wide, $"wide-{projection}", GameStep.Heavy, Between(r, 8, 15, 1), render, scene);
            scene = null;
        }
    }

    private static IEnumerable<Draft> SkyPattern(Random r, GamePlanOptions o)
    {
        double s = Between(r, 10, 20, 1);
        yield return new(Sky, "sky-time-lapse", GameStep.Heavy, s, HeavyRender(r, o) with { Sun = SunTimeLapse }, DemoAt(r, s));
    }

    private static IEnumerable<Draft> PhotoPattern(Random r, GamePlanOptions o)
    {
        double s = Between(r, 6, 12, 1);
        var photo = new Render(Scale: Clamp(Pick(r, 100, 125, 150), o), Bounces: 2, Reflect: 8, Photo: true, Sun: Pick(r, 4, 5, 6, 7));
        yield return new(PhotoMode, "photo-accumulate", GameStep.Photo, s, photo, DemoAt(r, s));
        // Out of photo mode straight into a light step: the jump from the heaviest frames to a light load.
        yield return new(PhotoMode, "photo-release", GameStep.Light, Between(r, 2, 4, 1), LightRender(r, o));
    }

    private static IEnumerable<Draft> PlayPattern(Random r, GamePlanOptions o)
    {
        string map = o.Maps[r.Next(o.Maps.Count)];
        string weapon = Pick(r, Weapons.ToArray());
        int yaw = Pick(r, 60, 90, 140, 200, 300);
        var heavy = HeavyRender(r, o) with { Particles = Pick(r, 2.0, 3, 4) };
        var light = LightRender(r, o);
        string spin = r.Next(2) == 0 ? "+right" : "+left";
        yield return new(Play, "play-arrive", GameStep.Heavy, Between(r, 5, 8, 1), heavy, new Scene(Scene.Play, Map: map),
            ["god", "notarget", "give all", $"use {weapon}", $"cl_yawspeed {yaw}", "cl_pitchspeed 60", spin]);
        int rounds = r.Next(3, 7);
        for (int i = 0; i < rounds; i++)
        {
            var fire = new List<string> { "+attack" };
            if (r.Next(3) == 0) fire.Add($"use {Pick(r, Weapons.ToArray())}");
            if (r.Next(3) == 0) fire.Add(r.Next(2) == 0 ? "+lookup" : "+lookdown");
            yield return new(Play, "play-fight", GameStep.Heavy, Between(r, 2, 5, 0.5), heavy, Actions: fire);
            // Mid-fight the load drops for a moment (a light step), then comes back: the jump onto the top in combat.
            yield return new(Play, "play-lull", GameStep.Light, Between(r, 1, 3, 0.5), light, Actions: ["-lookup", "-lookdown", "centerview"]);
            if (r.Next(2) == 0)
                yield return new(Play, "play-dash", GameStep.Heavy, Between(r, 0.5, 1.5, 0.5), heavy, Actions: ["+forward", "+moveup"]);
            yield return new(Play, "play-stop", GameStep.Heavy, 0.5, heavy, Actions: ["-forward", "-moveup", "-attack"]);
        }
    }

    private static IEnumerable<Draft> RestartPattern(Random r, GamePlanOptions o)
    {
        // The renderer restarts on the other ray-tracing path; the runner alternates query and pipeline from what is in force.
        double s = Between(r, 8, 12, 1);
        yield return new(Restart, "restart", GameStep.Heavy, s, HeavyRender(r, o) with { Scale = 100 }, DemoAt(r, s), RtApi: "other");
    }

    /// <summary>
    /// The card-side levers on a slower clock than the patterns, each change attached to the first step of the first pattern
    /// that starts after its epoch begins (never mid-pattern), so a run writes a handful of trials, not hundreds. Power: from
    /// the starting wall down in thirds of the board's range and back, never above the start. Fans: on their curve, then 100 %
    /// (a cold card: the curve's temperature steps put the clocks higher at the same voltage), then a heat soak at a low duty
    /// until <see cref="GamePlanOptions.WarmC"/> (a warm card), then the curve again. Boost: Boost Lock for a stretch or two
    /// past the first third, where every load step arrives at the curve's top.
    /// </summary>
    private static (Dictionary<int, List<Lever>> At, List<string> Epochs) LeverSchedule(List<Draft> steps, GamePlanOptions o, Random r)
    {
        var changes = new List<(double At, Lever Lever)>();
        double run = o.Seconds;
        if (o.Levers.Contains(Lever.Power) && o.StartPowerMw > o.MinPowerMw + 25_000)
        {
            uint span = o.StartPowerMw - o.MinPowerMw;
            uint Round(double mw) => (uint)(Math.Round(mw / 5000) * 5000);
            var lower = new[] { Round(o.StartPowerMw - span / 3.0), Round(o.StartPowerMw - span * 2 / 3.0), o.MinPowerMw }.Distinct()
                .Where(w => w < o.StartPowerMw && w >= o.MinPowerMw).ToList();
            double epoch = Math.Clamp(run / 6, 60, 240);
            uint previous = o.StartPowerMw;
            for (int k = 1; k * epoch < run - 20; k++)
            {
                uint wall = k % 3 == 0 || lower.Count == 0 ? o.StartPowerMw : lower.Where(w => w != previous).OrderBy(_ => r.Next()).First();
                if (wall == previous) continue;
                changes.Add((k * epoch, new Lever(Lever.Power, wall, wall == o.StartPowerMw ? "power epoch: back to the starting wall" : "power epoch: a lower wall puts heavy scenes lower on the curve")));
                previous = wall;
            }
        }
        if (o.Levers.Contains(Lever.Fans))
        {
            double epoch = Math.Clamp(run / 4, 90, 300);
            var cycle = new[]
            {
                new Lever(Lever.Fans, 100, "fan epoch: a cold card (100 %): the curve's temperature steps put the clock higher at the same voltage"),
                new Lever(Lever.Fans, (uint)Math.Clamp(o.WarmFanPct, FanMinPct, 100), "fan epoch: a heat soak at a low duty, back to the fan curve at the soak temperature", o.WarmC),
                new Lever(Lever.Fans, 0, "fan epoch: the fan curve"),
            };
            for (int k = 1; k * epoch < run - 30; k++) changes.Add((k * epoch, cycle[(k - 1) % cycle.Length]));
        }
        if (o.Levers.Contains(Lever.Boost) && run >= 240)
        {
            double len = Math.Clamp(run * 0.15, 60, 300);
            foreach (double frac in run >= 1200 ? new[] { 0.4, 0.8 } : [0.45])
            {
                changes.Add((run * frac, new Lever(Lever.Boost, 1, "boost epoch: Boost Lock, every load step arrives at the curve's top")));
                changes.Add((Math.Min(run * frac + len, run - 15), new Lever(Lever.Boost, 0, "boost epoch over: Boost Lock released")));
            }
        }

        var at = new Dictionary<int, List<Lever>>();
        var epochs = new List<string>();
        // Every pattern's first step sets its scene (a seek, a map): those are the boundaries a lever may land on.
        var starts = steps.Select((d, i) => (d, i)).Where(x => x.d.Scene != null).ToList();
        foreach (var (time, lever) in changes.OrderBy(c => c.At))
        {
            var first = starts.FirstOrDefault(x => x.d.Start >= time);
            if (first.d == null) continue;
            var list = at.TryGetValue(first.i, out var l) ? l : at[first.i] = [];
            list.RemoveAll(x => x.Kind == lever.Kind);   // two of a kind on one step: the later wins
            list.Add(lever);
            epochs.Add(string.Create(CultureInfo.InvariantCulture, $"{first.d.Start:0} s: {lever.Words()}"));
        }
        return (at, epochs);
    }
}
