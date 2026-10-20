using System.Text.RegularExpressions;

namespace Clocktail.Domain.Ownership;

/// <summary>
/// Which boot of the machine a record was written in. Everything the journal decides about a record from another
/// boot rests on this, and the two possible mistakes are not equally bad:
/// <list type="bullet">
/// <item>calling a live state "rebooted" drops its originals while the tuning is still on the card, so the way back
/// to stock is lost;</item>
/// <item>calling a real reboot "same boot" only makes recovery write the originals over a card the reboot already
/// reset, which is a harmless no-op.</item>
/// </list>
/// So an identity must change only when the machine boots (never with the wall clock), and when two identities
/// cannot be compared the answer is "same boot". The old identity, boot instant = now − uptime rounded to a minute,
/// moved whenever the clock was corrected (NTP, time zone sync), which is exactly the first mistake.
/// </summary>
public static partial class BootIdentity
{
    private const string WindowsGuidPrefix = "win-boot-";
    private const string WindowsCountPrefix = "win-bootcount-";
    private const string UptimePrefix = "boot-";

    /// <summary>Minutes two uptime-derived identities may differ by and still be one boot: clock corrections are seconds,
    /// a reboot is far more than this unless the machine comes back within the tolerance.</summary>
    public const long UptimeToleranceMinutes = 5;

    /// <summary>This boot's identity: the kernel's own per-boot id where the OS has one, the uptime instant only as a last resort.</summary>
    public static string Current()
    {
        if (OperatingSystem.IsLinux())
        {
            try { return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows() && WindowsBootCount() is { } count) return WindowsCountPrefix + count;
        return UptimeIdentity(DateTimeOffset.UtcNow, Environment.TickCount64);
    }

    /// <summary>The uptime-instant identity (the pre-fix scheme); kept for the fallback and for comparing old records.</summary>
    public static string UptimeIdentity(DateTimeOffset now, long uptimeMs) =>
        $"{UptimePrefix}{(now - TimeSpan.FromMilliseconds(uptimeMs)).ToUnixTimeSeconds() / 60}";

    /// <summary>Were <paramref name="recorded"/> and <paramref name="current"/> taken in the same boot? Equal ids: yes.
    /// Two uptime instants: yes within the tolerance. Two ids of the same other kind that differ: no. Ids of different
    /// kinds cannot be compared (a record written before this scheme, or by a host whose primary source failed), so:
    /// yes, the harmless mistake.</summary>
    public static bool SameBoot(string? recorded, string current)
    {
        if (string.IsNullOrEmpty(recorded) || recorded == current) return true;
        var (rk, rv) = Parse(recorded);
        var (ck, cv) = Parse(current);
        if (rk != ck) return true;
        if (rk == Kind.Uptime) return Math.Abs(rv - cv) <= UptimeToleranceMinutes;
        return false;
    }

    private enum Kind { Opaque, WindowsGuid, WindowsCount, Uptime }

    private static (Kind, long) Parse(string id)
    {
        if (id.StartsWith(WindowsGuidPrefix, StringComparison.Ordinal)) return (Kind.WindowsGuid, 0);
        if (id.StartsWith(WindowsCountPrefix, StringComparison.Ordinal)) return (Kind.WindowsCount, 0);
        if (UptimeId().Match(id) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var minutes)) return (Kind.Uptime, minutes);
        return (Kind.Opaque, 0);
    }

    [GeneratedRegex(@"^boot-(\d+)$")]
    private static partial Regex UptimeId();

    // Not SYSTEM_BOOT_ENVIRONMENT_INFORMATION's BootIdentifier: that GUID names the boot loader entry, not the boot, and
    // stayed the same across the row 11d crash reboot (win-boot-3e459ee3-… before and after), so a reboot read as
    // "same boot". Records carrying it ("win-boot-") still parse, and compare as "same boot" with anything else.

    // The boot counter Windows keeps for the prefetcher; it increments once per boot.
    private static long? WindowsBootCount()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue("BootId") is int n ? n : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
