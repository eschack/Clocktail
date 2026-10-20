using System.Globalization;

namespace Clocktail.Domain.Fps;

/// <summary>
/// A game's frame rate from a PresentMon capture, its CSV one row per frame: per application the frames, the average,
/// the 1 % and 0.1 % lows (the frame rate over the slowest 1 % and 0.1 % of frames: a stutter that a percentile at the
/// edge would hide still shows), the frame time's P50 and P99, and the rate frames reached the screen. Columns are found by name in the header, which changes between PresentMon versions: MsBetweenPresents is the
/// frame time (what PresentMon's own console counts as fps), MsBetweenDisplayChange the time between frames shown.
/// </summary>
public static class FrameStats
{
    public sealed record App(string Name, int Frames, double Seconds, double AvgFps, double Low1Fps, double Low01Fps,
        double P50Ms, double P99Ms, double? DisplayedFps);

    /// <summary>The desktop compositor presents all the time; it is never the game.</summary>
    public const string Compositor = "dwm.exe";

    /// <summary>Every application in the capture that presented, most frames first.</summary>
    public static IReadOnlyList<App> Parse(IEnumerable<string> lines)
    {
        var f = new Frames();
        foreach (var l in lines) f.Add(l);
        return f.Apps();
    }

    /// <summary>The game: the named process (with or without .exe), else the application with the most frames other than
    /// the compositor. Null when nothing else presented.</summary>
    public static App? Game(IReadOnlyList<App> apps, string? process)
    {
        if (process is { Length: > 0 } p)
        {
            string want = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe";
            return apps.FirstOrDefault(a => string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase));
        }
        return apps.FirstOrDefault(a => !string.Equals(a.Name, Compositor, StringComparison.OrdinalIgnoreCase));
    }

    internal static App Summarize(string name, List<double> ms, List<double> shown)
    {
        var sorted = ms.Order().ToList();
        double total = ms.Sum();
        double P(double q) => sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
        double Low(double share)
        {
            int n = Math.Max(1, (int)Math.Round(share * sorted.Count));
            return 1000 * n / sorted.TakeLast(n).Sum();
        }
        static double R(double v) => Math.Round(v, 1);
        return new App(name, ms.Count, Math.Round(total / 1000, 1), R(1000 * ms.Count / total), R(Low(0.01)), R(Low(0.001)),
            Math.Round(P(0.5), 2), Math.Round(P(0.99), 2), shown.Count == 0 ? null : R(1000 * shown.Count / shown.Sum()));
    }
}

/// <summary>Frame rows as they come, line by line: the header first (it names the columns), then one row per frame. Rows
/// without a frame time (the first frame of a swap chain, one PresentMon could not time) are skipped.</summary>
public sealed class Frames
{
    private int _app = -1, _between = -1, _shown = -1;
    private readonly Dictionary<string, (List<double> Ms, List<double> Shown)> _byApp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The header line, once seen: a window of rows read later needs it too.</summary>
    public string? Header { get; private set; }

    public int Count { get; private set; }

    public void Add(string line)
    {
        line = line.TrimStart((char)0xFEFF);   // PresentMon writes its CSV as UTF-8 with a byte-order mark
        if (line.Length == 0) return;
        var f = line.Split(',');
        if (_app < 0)
        {
            int app = Array.IndexOf(f, "Application"), between = Array.IndexOf(f, "MsBetweenPresents");
            if (app < 0 || between < 0) return;   // not the header
            (_app, _between, _shown, Header) = (app, between, Array.IndexOf(f, "MsBetweenDisplayChange"), line);
            return;
        }
        if (f.Length <= Math.Max(_app, _between) || !double.TryParse(f[_between], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0) return;
        if (!_byApp.TryGetValue(f[_app], out var a)) _byApp[f[_app]] = a = ([], []);
        a.Ms.Add(ms);
        if (_shown >= 0 && _shown < f.Length && double.TryParse(f[_shown], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0) a.Shown.Add(d);
        Count++;
    }

    /// <summary>A fresh accumulator that knows this one's header (for the next window of rows).</summary>
    public Frames Next()
    {
        var n = new Frames();
        if (Header != null) n.Add(Header);
        return n;
    }

    public IReadOnlyList<FrameStats.App> Apps() =>
        [.. _byApp.Select(kv => FrameStats.Summarize(kv.Key, kv.Value.Ms, kv.Value.Shown)).OrderByDescending(a => a.Frames)];
}

/// <summary>A frame-rate capture the elevated broker runs (PresentMon needs administrator rights or the Performance Log
/// Users group). Start answers where the CSV goes; the capture ends by itself after its seconds, or at Stop.</summary>
public interface IFrameCapture
{
    /// <exception cref="InvalidOperationException">The capture could not start; the message says why.</exception>
    object Start(string dir, int seconds, string? process);

    object Stop();
}
