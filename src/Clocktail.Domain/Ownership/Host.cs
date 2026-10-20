using System.Diagnostics;

namespace Clocktail.Domain.Ownership;

/// <summary>The two process facts ownership records depend on, behind an interface so recovery can be tested.</summary>
public interface IHost
{
    int Pid { get; }
    /// <summary>Stable for one boot of the machine, different after a reboot. A reboot resets every hardware setting
    /// this program made, so records from another boot describe state that no longer exists.</summary>
    string BootId { get; }
    /// <summary>Is <paramref name="pid"/> running, and (when <paramref name="startedBy"/> is given) the same process that
    /// was running then? A process that started after <paramref name="startedBy"/> is a reused pid, not the owner.</summary>
    bool IsProcessAlive(int pid, DateTimeOffset? startedBy = null);
    DateTimeOffset Now { get; }
}

public sealed class SystemHost : IHost
{
    /// <summary>Clock slack between a process's start time and the instant it wrote its first record.</summary>
    private static readonly TimeSpan ReuseSlack = TimeSpan.FromSeconds(5);

    public int Pid => Environment.ProcessId;

    public string BootId { get; } = BootIdentity.Current();

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>Is a process with this id running? Present in the process list and not known to have exited. A process
    /// we may see but not open (another session, a protected process) is running: "cannot tell" must not read as "gone",
    /// because both callers treat "gone" as permission to act (adopt its hardware, ignore its judge lock).</summary>
    public bool IsProcessAlive(int pid, DateTimeOffset? startedBy = null)
    {
        if (pid <= 0) return false;
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }          // not in the process list
        catch (InvalidOperationException) { return false; }
        using (p)
        {
            try { if (p.HasExited) return false; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
            if (startedBy is not { } by) return true;
            // After a reboot the old owner's pid can belong to anything (row 11d: the broker's old pid 8364 came back as
            // another process and held the record "live"). A start time we cannot read stays "alive", as above.
            try { return p.StartTime.ToUniversalTime() <= by.UtcDateTime + ReuseSlack; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
        }
    }
}
