using System.Globalization;

namespace Clocktail.Domain.Platform;

/// <summary>
/// A release's version as npm and the build stamp write it ("0.1.4", "0.1.4+cf6f1aa"): major.minor.patch, compared
/// by number. A pre-release ("0.0.0-stage", npm's placeholder while a publish is processed) is never a release.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        int plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        if (t.StartsWith('v')) t = t[1..];
        if (t.Contains('-')) return null;
        var parts = t.Split('.');
        if (parts.Length != 3) return null;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return new ReleaseVersion(n[0], n[1], n[2]);
    }

    /// <summary>True when <paramref name="latest"/> is a release newer than <paramref name="current"/>; false when
    /// either does not read as one.</summary>
    public static bool IsNewer(string? latest, string? current) =>
        Parse(latest) is { } l && Parse(current) is { } c && l.CompareTo(c) > 0;

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
