using System.Text.RegularExpressions;

namespace Clocktail.Domain.Game;

/// <summary>
/// What the game test (game_test, clocktail-lab game-test) can play, shared by the MCP server that checks a request and the
/// lab that plays it: the load patterns of its script and the card-side levers under them.
/// </summary>
public static class GameCatalog
{
    public const string Heavy = "heavy", Leap = "leap", Pulse = "pulse", Burst = "burst", Ramp = "ramp", Drs = "drs", Cuts = "cuts",
        Toggles = "toggles", Wide = "wide", Sky = "sky", Photo = "photo", Play = "play", Restart = "restart";

    public static readonly IReadOnlyList<string> AllPatterns = [Heavy, Leap, Pulse, Burst, Ramp, Drs, Cuts, Toggles, Wide, Sky, Photo, Play, Restart];
    /// <summary>Every pattern but restart, which closes the game's window and makes it again (it may lose the foreground).</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = [.. AllPatterns.Where(p => p != Restart)];

    public const string Power = "power", Fans = "fans", Boost = "boost";
    public static readonly IReadOnlyList<string> AllLevers = [Power, Fans, Boost];
    /// <summary>The wall and the fans; Boost Lock is asked for by name: it holds the clock at the top, so it takes away the
    /// jump onto the top that games make (another condition, not the main one).</summary>
    public static readonly IReadOnlyList<string> DefaultLevers = [Power, Fans];

    /// <summary>A comma-separated choice from <paramref name="all"/>; "default" (or nothing), "all" or "none".</summary>
    public static IReadOnlyList<string> Parse(string? text, IReadOnlyList<string> all, IReadOnlyList<string> defaults, string what)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) return defaults;
        var t = text.Trim().ToLowerInvariant();
        if (t is "none") return [];
        if (t is "all") return all;
        var list = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (list.FirstOrDefault(p => !all.Contains(p)) is { } bad)
            throw new ArgumentException($"'{bad}' is not one of the {what}: {string.Join(", ", all)} (or default, all, none)");
        return list;
    }
}

/// <summary>Where Quake II RTX is installed. Nothing is read from the installation but its files' names and the executable's
/// hash, and nothing in it is written.</summary>
public static class QuakeLocator
{
    /// <summary>The build the game test was written against (NVIDIA/Q2RTX v1.8.0, d65761f2, Steam, 2025-03-26). Another build
    /// is not refused: what the test needs is checked in its answers at the start.</summary>
    public const string KnownSha256 = "b88891ed4aa71a79ad158840aaccc27a54aa0d992359a54696ac6905f5db57b3";
    public const string Variable = "CLOCKTAIL_Q2RTX";
    public const string SteamAppId = "1089130";

    /// <summary>q2rtx.exe: CLOCKTAIL_Q2RTX, else every Steam library (libraryfolders.vdf in Steam's own folder), else the
    /// standard Steam folder. Null when none has it.</summary>
    public static string? Find(Func<string, bool>? exists = null, string? steamDir = null, Func<string, string?>? read = null)
    {
        exists ??= File.Exists;
        read ??= p => { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch (IOException) { return null; } };
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } e) return exists(e) ? Path.GetFullPath(e) : null;
        steamDir ??= SteamDir() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var roots = new List<string> { steamDir };
        if (read(Path.Combine(steamDir, "steamapps", "libraryfolders.vdf")) is { } vdf) roots.AddRange(Libraries(vdf));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string exe = Path.Combine(root, "steamapps", "common", "Quake II RTX", "q2rtx.exe");
            if (exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>The "path" entries of Steam's libraryfolders.vdf (its backslashes doubled).</summary>
    public static IEnumerable<string> Libraries(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    private static string? SteamDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string s && s.Length > 0 ? s.Replace('/', '\\') : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The highest internal resolution by the card's video memory: 200 % (four times the window's pixels) from 24 GiB,
    /// 150 % from 16 GiB, else 125 %.</summary>
    public static int MaxScaleFor(ulong? vramBytes) => vramBytes switch
    {
        >= 23UL << 30 => 200,
        >= 15UL << 30 => 150,
        _ => 125,
    };
}
