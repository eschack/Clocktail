using System.Globalization;

namespace Clocktail.Lab.Game;

/// <summary>
/// One answer to the state query: what the game says it is doing, read back over rcon with a fresh marker, so a stale UDP
/// reply or an acknowledgement is never taken for a rendered frame. RenderScale is the renderer's own feedback of the scale
/// its last frame was drawn at (DRS's too), not the variable that asked for it. Hdr is the centre pixel's linear colour after
/// the temporal filter, read back from the GPU. Invalid readback text is not evidence of a GPU arithmetic fault.
/// </summary>
public sealed record GameFrame(string Map, double? DemoSeconds, double? TimerSeconds, int Fps, int? RenderScale, int? Health,
    IReadOnlyDictionary<string, string> Cvars, double[]? Hdr, string? ViewDir)
{
    /// <summary>The fields the query asks for, in order. Macros (cl_*, r_fps) are the client's; the rest are variables.</summary>
    public static readonly IReadOnlyList<string> Fields =
    [
        "cl_mapname", "cl_demopos", "cl_timer", "r_fps", "cl_resolution_scale", "cl_health",
        "viewsize", "r_maxfps", "vid_vsync", "drs_enable", "pt_num_bounce_rays", "pt_reflect_refract", "pt_caustics", "gr_enable",
        "bloom_enable", "flt_enable", "flt_fsr_enable", "fov", "pt_projection", "sun_preset", "cl_particle_num_factor",
        "pt_accumulation_rendering", "pt_dof", "cl_paused", "vid_rtx", "cl_async", "ray_tracing_api", "cl_hdr_color", "cl_viewdir",
        "drs_minscale", "drs_maxscale", "drs_target",
    ];

    /// <summary>The variables the test needs the game to answer: a build that leaves one empty cannot be judged by it.</summary>
    public static readonly IReadOnlyList<string> Required = [.. new Render(100, DrsTarget: 60).Cvars().Select(c => c.Name), "cl_paused", "vid_rtx", "cl_async", "ray_tracing_api"];

    /// <summary>The echo that asks for every field: ${name} so a '|' right after a name does not join it (the console reads a
    /// bare $name up to the next space).</summary>
    public static string Query(string marker) => "echo " + marker + "|" + string.Join("|", Fields.Select(f => "${" + f + "}"));

    /// <summary>The frame in a reply, or null when the reply has no line for this marker or it does not parse.</summary>
    public static GameFrame? Parse(string reply, string marker)
    {
        var line = reply.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(marker + "|", StringComparison.Ordinal));
        if (line == null) return null;
        var p = line.Split('|');
        if (p.Length != Fields.Count + 1) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Fields.Count; i++) values[Fields[i]] = p[i + 1].Trim();
        if (!int.TryParse(values["r_fps"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps)) return null;
        return new GameFrame(values["cl_mapname"], Clock(values["cl_demopos"]), Clock(values["cl_timer"]), fps,
            Int(values["cl_resolution_scale"]), Int(values["cl_health"]), values, Vector(values["cl_hdr_color"]), values["cl_viewdir"]);
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>"m:ss.f", "m:ss" or "h:m:ss" as seconds; null when it is none of them.</summary>
    public static double? Clock(string s)
    {
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0 || !double.IsFinite(v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>"(r, g, b)" as MSVC prints it, including inf and nan(ind). Malformed text returns null, not a fabricated NaN.</summary>
    public static double[]? Vector(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '(' || s[^1] != ')') return null;
        var parts = s[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        var vector = new double[3];
        for (int i = 0; i < parts.Length; i++)
        {
            string text = parts[i].ToLowerInvariant(), unsigned = text.TrimStart('+', '-');
            if (unsigned is "nan" or "nan(ind)" or "nan(snan)" or "nan(qnan)") vector[i] = double.NaN;
            else if (unsigned is "inf" or "infinity") vector[i] = text.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out vector[i]) || !double.IsFinite(vector[i])) return null;
        }
        return vector;
    }

    /// <summary>The centre pixel read back a NaN (hardware or renderer fault). Infinity alone is not judged: a half-float
    /// can overflow in a working frame.</summary>
    public bool NonFinite => Hdr is { } h && h.Any(double.IsNaN);
    public bool Infinite => Hdr is { } h && h.Any(double.IsInfinity);

    public string Get(string name) => Cvars.TryGetValue(name, out var v) ? v : "";

    /// <summary>The variables of <paramref name="render"/> this frame does not hold, as "name=wanted (reads actual)"; empty when
    /// it holds them all. Numbers compare as numbers ("0.5" is "0.500"), the rest as text.</summary>
    public IReadOnlyList<(string Name, string Want, string Got)> Mismatches(Render render)
    {
        var list = new List<(string, string, string)>();
        foreach (var (name, want) in render.Cvars())
        {
            string got = Get(name);
            if (!Same(want, got)) list.Add((name, want, got));
        }
        if (!Same("1", Get("vid_rtx"))) list.Add(("vid_rtx", "1", Get("vid_rtx")));
        if (!Same("1", Get("cl_async"))) list.Add(("cl_async", "1", Get("cl_async")));
        return list;
    }

    public static bool Same(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? Math.Abs(x - y) < 1e-6 : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Photo mode is in force: paused by the pause command (cl_paused 2) with accumulation on.</summary>
    public bool Paused => Get("cl_paused") is "1" or "2";

    /// <summary>The required variables this build left empty.</summary>
    public IReadOnlyList<string> Missing() => [.. Required.Where(r => string.IsNullOrWhiteSpace(Get(r)))];
}
