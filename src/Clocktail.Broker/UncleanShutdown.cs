using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Broker;

/// <summary>
/// Did the last boot end uncleanly? Windows writes it into the System log early in the next boot: Kernel-Power 41 (the
/// system rebooted without cleanly shutting down), EventLog 6008 (the previous shutdown was unexpected) and, after a
/// bugcheck, WER 1001. All three were there after the 0x133 of 2026-10-18 02:44Z, which left no driver-reset event and
/// no write window open, so this is the only evidence such a crash leaves. Only events of this boot count: the query
/// starts at the boot (now minus the tick count).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class UncleanShutdown
{
    private static readonly (string Provider, int Id, string Name)[] Signs =
    [
        ("Microsoft-Windows-Kernel-Power", 41, "Kernel-Power 41"),
        ("EventLog", 6008, "EventLog 6008"),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001, "BugCheck 1001"),
    ];

    private const uint QueryChannelPath = 0x1;

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(IntPtr resultSet, uint eventsSize, [Out] IntPtr[] events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(IntPtr handle);

    /// <summary>The signs of an unclean end found since this boot started, joined ("Kernel-Power 41, BugCheck 1001"), or null.</summary>
    /// <exception cref="InvalidOperationException">The System log could not be queried.</exception>
    public static string? SinceBoot()
    {
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var found = Signs.Where(s => Any(s.Provider, s.Id, boot)).Select(s => s.Name).ToList();
        return found.Count == 0 ? null : string.Join(", ", found);
    }

    private static bool Any(string provider, int id, DateTime sinceUtc)
    {
        string query = $"*[System[Provider[@Name='{provider}'] and EventID={id} and TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
        var results = EvtQuery(IntPtr.Zero, "System", query, QueryChannelPath);
        if (results == IntPtr.Zero)
            throw new InvalidOperationException($"could not query the System log (error {Marshal.GetLastPInvokeError()})");
        try
        {
            var events = new IntPtr[1];
            if (!EvtNext(results, 1, events, 1000, 0, out uint n) || n == 0) return false;
            EvtClose(events[0]);
            return true;
        }
        finally { EvtClose(results); }
    }
}
