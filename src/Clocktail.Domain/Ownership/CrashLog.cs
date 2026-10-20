using System.Text.Json;

namespace Clocktail.Domain.Ownership;

/// <summary>
/// The machine went down while this program held cells: with a write window open (what was in flight), or uncleanly
/// with none open (a bugcheck or a power loss under load), and what was held.
/// </summary>
public sealed record CrashRecord(
    /// <summary>When recovery found it (the next start after the reboot).</summary>
    DateTimeOffset FoundAt,
    /// <summary>The boot the record was written in: the one that ended.</summary>
    string BootId,
    /// <summary>The sentinel's text (the mode and the cell and value in flight), or, with no window open, the unclean
    /// shutdown's evidence.</summary>
    string Sentinel,
    /// <summary>The cells this program held at the time, with their originals.</summary>
    IReadOnlyList<OwnedItem> Items);

/// <summary>
/// The one piece of evidence a tuning tool can ever get that a particular write took the GPU down: the sentinel
/// says a window was open, the journal says what was held, and the reboot says the machine did not come back
/// from it on its own. Recovery would otherwise clear both as "reset by the reboot" and the evidence would be
/// gone, so it appends one line here first. A line per crash, JSON, oldest first, beside the sentinel.
/// </summary>
public static class CrashLog
{
    public const string FileName = "crashes.jsonl";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string PathBeside(string sentinelPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sentinelPath))!, FileName);

    public static void Append(string path, CrashRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n");
    }

    /// <summary>
    /// The one door a window found open across a reboot leaves through. When the journal's record predates this boot
    /// and the sentinel is still up, the crash goes on record here and then both marks are cleared as reset, in that
    /// order; the log's path comes back. Anything else (no record, a record from this boot, a sentinel with no record)
    /// is left alone and null comes back. Idempotent and cheap, so every door the stale marks could otherwise leave
    /// through calls it first: a write's origin lookup, the write itself, recovery, the broker's ownership report and
    /// its start. The first of those used to be <see cref="OwnershipJournal.Adopt"/>, which drops a record from before
    /// the boot without a look at the sentinel, so the first write after a reboot erased the evidence before recovery
    /// could see it.
    /// <para>
    /// With <paramref name="uncleanShutdown"/> (the broker's start found the last boot ended in a bugcheck or a power
    /// loss) a record from before the boot that still held cells is a crash too, though no window was open: the daily
    /// profile is held all day, and a curve that blue-screens a game leaves no window open, only that (row 11d,
    /// 2026-10-18: bugcheck 0x133 with no driver-reset event). Without it the profile would go back on unchanged at
    /// every boot.
    /// </para>
    /// </summary>
    public static string? SettleReboot(OwnershipJournal journal, ApplySentinel sentinel, IHost host, string? uncleanShutdown = null)
    {
        var plan = journal.Inspect();
        if (plan.Reason != RecoveryReason.Rebooted) return null;
        string inFlight;
        if (sentinel.IsBlocked(out var open)) inFlight = open;
        else if (uncleanShutdown != null && plan.Items.Count > 0) inFlight = $"no write window open; the machine went down uncleanly ({uncleanShutdown})";
        else return null;
        var path = PathBeside(sentinel.Path);
        Append(path, new CrashRecord(host.Now, journal.Load()?.BootId ?? "", inFlight, plan.Items));
        journal.Clear();
        sentinel.Clear();
        return path;
    }

    /// <summary>The last <paramref name="count"/> records, oldest first; a line that does not parse is skipped.</summary>
    public static IReadOnlyList<CrashRecord> ReadLast(string path, int count)
    {
        if (!File.Exists(path)) return [];
        var records = new List<CrashRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<CrashRecord>(line, Json) is { } r) records.Add(r); }
            catch (JsonException) { }
        }
        return records.Count <= count ? records : records.GetRange(records.Count - count, count);
    }
}
