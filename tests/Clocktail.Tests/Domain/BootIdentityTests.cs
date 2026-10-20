using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Xunit;

namespace Clocktail.Tests.Domain;

public class BootIdentityTests
{
    [Fact]
    public void A_clock_correction_does_not_make_a_live_state_look_rebooted()
    {
        // Same boot, uptime 3 h; the wall clock is corrected by 90 s between the two reads (an NTP sync).
        var t0 = new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
        var before = BootIdentity.UptimeIdentity(t0, 3 * 3600_000L);
        var after = BootIdentity.UptimeIdentity(t0.AddMinutes(10).AddSeconds(90), 3 * 3600_000L + 10 * 60_000L);
        Assert.NotEqual(before, after);   // the old scheme compared these with ==, and called it a reboot
        Assert.True(BootIdentity.SameBoot(before, after));
    }

    [Fact]
    public void A_real_reboot_is_still_a_reboot()
    {
        var t0 = new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
        Assert.False(BootIdentity.SameBoot(BootIdentity.UptimeIdentity(t0, 3 * 3600_000L), BootIdentity.UptimeIdentity(t0.AddHours(1), 120_000L)));
        Assert.False(BootIdentity.SameBoot("win-boot-11111111-1111-1111-1111-111111111111", "win-boot-22222222-2222-2222-2222-222222222222"));
        Assert.False(BootIdentity.SameBoot("win-bootcount-41", "win-bootcount-42"));
        Assert.False(BootIdentity.SameBoot("boot-A", "boot-B"));
    }

    [Fact]
    public void Identities_that_cannot_be_compared_count_as_the_same_boot()
    {
        // A record from the uptime scheme read by a host that now has the kernel's boot GUID: writing originals back
        // over a reset card is harmless, dropping them from a live card is not.
        Assert.True(BootIdentity.SameBoot("boot-29846812", "win-boot-11111111-1111-1111-1111-111111111111"));
        Assert.True(BootIdentity.SameBoot("win-bootcount-41", "win-boot-11111111-1111-1111-1111-111111111111"));
        Assert.True(BootIdentity.SameBoot(null, "win-boot-11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void The_current_identity_is_stable_within_a_process()
    {
        Assert.Equal(BootIdentity.Current(), BootIdentity.Current());
        Assert.False(string.IsNullOrWhiteSpace(new SystemHost().BootId));
    }

    [Fact]
    public void A_journal_written_before_a_clock_correction_is_inspected_as_the_same_boot()
    {
        var dir = Directory.CreateTempSubdirectory("mv-boot-").FullName;
        try
        {
            var t0 = new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
            var writer = new FakeHost { Pid = 100, BootId = BootIdentity.UptimeIdentity(t0, 3 * 3600_000L) };
            var j = new OwnershipJournal(Path.Combine(dir, "ownership.json"), writer, "gpu-1");
            j.Claim(new OwnedItem("pstates20", "core", "0", t0, null));
            var reader = new FakeHost { Pid = 200, BootId = BootIdentity.UptimeIdentity(t0.AddMinutes(30).AddSeconds(-75), 3 * 3600_000L + 30 * 60_000L) };
            reader.Alive.Clear();   // the writer (pid 100) has exited
            var plan = new OwnershipJournal(Path.Combine(dir, "ownership.json"), reader, "gpu-1").Inspect();
            Assert.Equal(RecoveryReason.OwnerDied, plan.Reason);   // not Rebooted: the original "0" is still owed to the card
            Assert.Single(plan.Items);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_judge_trace_says_why_a_lock_reads_free_or_busy()
    {
        var dir = Directory.CreateTempSubdirectory("mv-judge-").FullName;
        try
        {
            var path = Path.Combine(dir, "judge.lock");
            Assert.Null(JudgeLock.BusyReason(path, new SystemHost(), out var t1));
            Assert.Contains("absent", t1);
            File.WriteAllText(path, $"{{\"Pid\":{Environment.ProcessId},\"RunId\":\"r\",\"Label\":\"x\",\"StartedUtc\":\"{DateTimeOffset.UtcNow:o}\"}}");
            Assert.NotNull(JudgeLock.BusyReason(path, new SystemHost(), out var t2));
            Assert.Contains("alive", t2);
        }
        finally { Directory.Delete(dir, true); }
    }
}
