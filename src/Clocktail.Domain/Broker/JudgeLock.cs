using System.Text.Json;
using Clocktail.Domain.Ownership;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The other half of a contract with StabilityLab. While it measures a state it holds a lock file
/// <c>{Pid, RunId, Label, StartedUtc}</c>; the broker reads it before opening a write window, so an automatic
/// write never moves the hardware out from under a run in progress. It is the mirror of StabilityLab refusing
/// to start while the broker's own apply.lock exists. A lock whose process is gone is stale and ignored, so a
/// crashed run never wedges every future write; a lock we cannot parse names no live process, so it is ignored
/// too rather than blocking forever.
/// </summary>
public static class JudgeLock
{
    public sealed record Info(int Pid, string? RunId = null, string? Label = null, DateTimeOffset? StartedUtc = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Where a lock may sit. A program started from an MSIX-packaged app (the Claude desktop app runs StabilityLab
    /// this way) has its new files under %LocalAppData% redirected to
    /// <c>%LocalAppData%\Packages\&lt;family&gt;\LocalCache\Local\…</c>. It sees the merged view, so to it the lock
    /// is at the usual path; the elevated broker runs outside the package and sees only the real folder, where the
    /// lock is absent. So the broker looks at the real path and at the same relative path in every package's cache.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string path, string localAppData)
    {
        var list = new List<string> { path };
        var root = Path.GetFullPath(localAppData).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return list;
        var rel = full[(root.Length + 1)..];
        var packages = Path.Combine(root, "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages))
                {
                    var p = Path.Combine(pkg, "LocalCache", "Local", rel);
                    if (File.Exists(p)) list.Add(p);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Busy when any of <paramref name="paths"/> names a live run; the trace covers every path looked at.</summary>
    public static string? BusyReason(IEnumerable<string> paths, IHost host, out string trace)
    {
        var traces = new List<string>();
        string? busy = null;
        foreach (var p in paths)
        {
            var b = BusyReason(p, host, out var t);
            traces.Add(t);
            busy ??= b;
        }
        trace = string.Join("; ", traces);
        return busy;
    }

    /// <summary>A sentence saying why a write must wait, or null when the GPU is free.</summary>
    public static string? BusyReason(string path, IHost host) => BusyReason(path, host, out _);

    /// <summary>As <see cref="BusyReason(string, IHost)"/>, with <paramref name="trace"/> saying how the verdict was
    /// reached (file present, pid parsed, liveness, any exception), so a wrong "free" can be read off the broker log.</summary>
    public static string? BusyReason(string path, IHost host, out string trace)
    {
        Info? info;
        try
        {
            if (!File.Exists(path)) { trace = $"{path}: absent"; return null; }
            info = JsonSerializer.Deserialize<Info>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            trace = $"{path}: unreadable ({e.GetType().Name}: {e.Message})";
            return null;
        }

        if (info == null || info.Pid <= 0) { trace = $"{path}: no pid"; return null; }
        bool alive = host.IsProcessAlive(info.Pid, info.StartedUtc);   // a lock left by a crash must not come back to life through a reused pid
        trace = $"{path}: pid {info.Pid} {(alive ? "alive" : "not running")}";
        if (!alive) return null;
        var label = string.IsNullOrWhiteSpace(info.Label) ? "" : $" '{info.Label}'";
        return $"pid {info.Pid}{label}";
    }
}
