namespace Clocktail.Domain.Profiles;

/// <summary>
/// Daily profiles saved under a name ("quiet", "games", "benchmark"), so a person can keep several and switch with a
/// sentence. They live beside the daily profile, in this card's own state folder (profiles\&lt;name&gt;.json), so a saved
/// profile is always this card's. Loading one makes it the daily profile, as a preset does; the daily profile stays the
/// one the service puts back after every boot and driver reset.
/// </summary>
public static class SavedProfiles
{
    public const string Folder = "profiles";
    public const int MaxNameLength = 32;

    public static string DirBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, Folder);

    public static string PathOf(string dir, string name) => Path.Combine(dir, name + ".json");

    /// <summary>The name as it is stored (trimmed, lowercase), or why it cannot be one: letters (any script), digits, '-'
    /// and '_', and never a preset's name, so loading by name is never ambiguous.</summary>
    public static (string? Name, string? Error) Normalize(string? name)
    {
        var n = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(n)) return (null, "a saved profile needs a name");
        if (n.Length > MaxNameLength) return (null, $"a name is at most {MaxNameLength} characters");
        if (!n.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return (null, "a name is letters, digits, '-' and '_' (no spaces)");
        if (Presets.Find(n) != null) return (null, $"'{n}' is a preset that ships with Clocktail: pick another name");
        return (n, null);
    }

    /// <summary>Every saved profile, by name. A file that does not read as a profile is left out, not fatal.</summary>
    public static IReadOnlyList<(string Name, DailyProfile Profile)> All(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var all = new List<(string, DailyProfile)>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
            try { if (DailyProfile.Load(f) is { } p) all.Add((Path.GetFileNameWithoutExtension(f), p)); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return all;
    }
}
