namespace Clocktail.Domain.Ownership;

/// <summary>
/// The crash sentinel. A file is created before settings are applied and deleted after they all landed.
/// If it still exists when the program starts, the previous application never finished (crash, hang, hard
/// reset), and automatic application must not run again: that is the "bad settings → crash → auto-apply at
/// logon → crash" loop. A person can still apply manually and take the sentinel over.
/// <para>
/// The file is also this program's published intent to the other tools on the machine (a stability run reads it
/// before it starts measuring), which is why <see cref="Begin"/> asks its stand-down question only once the file is
/// on disk. A tool that looks first and publishes second can be looked at by the other tool in the gap, and both go
/// ahead; with both tools publishing first, the one that looks second always sees the other.
/// </para>
/// </summary>
public sealed class ApplySentinel
{
    private readonly string _path;
    private readonly Func<string?> _standDown;

    /// <param name="standDown">Asked once the sentinel is on disk, before the window is handed to the caller: a
    /// reason to stand down (another tool's lock, seen now that this one is published) ends the window before any
    /// byte reaches the driver, as a <see cref="StandDownException"/>. Null means nothing to ask.</param>
    public ApplySentinel(string path, Func<string?>? standDown = null)
    {
        _path = path;
        _standDown = standDown ?? (() => null);
    }

    public string Path => _path;

    /// <summary>True when a previous application did not complete. <paramref name="content"/> says which kind.</summary>
    public bool IsBlocked(out string content)
    {
        content = "";
        if (!File.Exists(_path)) return false;
        try { content = File.ReadAllText(_path).Trim(); } catch (IOException) { content = "(unreadable)"; }
        return true;
    }

    /// <summary>
    /// Start an application. <paramref name="takeOver"/> lets a manual application proceed despite a stale
    /// sentinel; an automatic one must pass false and will get null when blocked. <paramref name="detail"/> names
    /// the write in flight (the cell and the value it is being set to): if the machine goes down inside this
    /// window, that line is the only record of what was being written, and recovery keeps it (<see cref="CrashLog"/>).
    /// Throws <see cref="StandDownException"/> when the stand-down check answers once the sentinel is published; the
    /// file is then back to what it was (gone, or the stale one a take-over was replacing) before the throw.
    /// </summary>
    public Scope? Begin(string mode, bool takeOver, string? detail = null)
    {
        bool stale = File.Exists(_path);
        if (!takeOver && stale) return null;
        string? previous = null;
        if (stale) { try { previous = File.ReadAllText(_path); } catch (IOException) { } }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
        File.WriteAllText(_path, $"Clocktail {mode} apply in progress\n" + (detail != null ? detail + "\n" : ""));

        // Published; now look. Only this order closes the race in which both tools looked, saw nothing, and went ahead.
        if (_standDown() is { } reason)
        {
            if (!stale) Complete();
            else if (previous != null) File.WriteAllText(_path, previous);
            throw new StandDownException(reason);
        }
        return new Scope(this);
    }

    private void Complete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Recovery's way out: cleared once every recorded item has been looked at and its state is known.</summary>
    public void Clear() => Complete();

    /// <summary>Dispose only on success: an exception that unwinds through the scope leaves the sentinel in place.</summary>
    public sealed class Scope(ApplySentinel owner)
    {
        public void Complete() => owner.Complete();
    }
}

/// <summary>The window was ended before any write: another tool's lock was there once this program's sentinel was on
/// disk. <see cref="Reason"/> is that tool's own description of itself (its pid and label).</summary>
public sealed class StandDownException(string reason) : InvalidOperationException($"stood down: {reason}")
{
    public string Reason { get; } = reason;
}
