using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

/// <summary>S23: when no agent has said "alive" for 5 minutes, the trial-only settings go back on every GPU, once per lapse,
/// and again on a GPU whose release did not all go back, until it does.</summary>
public class TrialLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static BrokerResponse Released() =>
        new(true, Result: JsonSerializer.SerializeToElement(new { success = true, released = new[] { 1 } }));

    /// <summary>The broker's answer while a stability run holds the card: nothing went back.</summary>
    private static BrokerResponse Busy() =>
        new(false, "a stability run is in progress (lab): the write window is held, try again when it finishes",
            JsonSerializer.SerializeToElement(new { success = false, released = Array.Empty<object>() }), BrokerCode.JudgeBusy);

    [Fact]
    public void A_lapsed_lease_releases_once_until_an_agent_is_heard_again()
    {
        var alive = T0;
        var asked = new List<BrokerRequest>();
        var log = new List<string>();
        var lease = new TrialLease(r => { asked.Add(r); return Released(); }, ["gpu-A", "gpu-B"], () => alive, log.Add);

        Assert.False(lease.Look(T0.AddMinutes(4)));
        Assert.True(lease.Look(T0.AddMinutes(5)));
        Assert.Equal(["gpu-A", "gpu-B"], asked.Select(a => a.GpuId));
        Assert.All(asked, a => Assert.Equal("release", a.Kind));
        Assert.Contains("5 min", asked[0].Value);
        Assert.Equal(2, log.Count);
        Assert.False(lease.Look(T0.AddMinutes(9)));      // asked already for this lapse
        Assert.Equal(2, asked.Count);

        alive = T0.AddMinutes(10);                       // an agent came back
        Assert.False(lease.Look(T0.AddMinutes(12)));
        Assert.True(lease.Look(T0.AddMinutes(16)));      // and went away again
    }

    /// <summary>The first release can find the card held (a stability run measuring, the write window busy, a driver error).
    /// Before 0.3.4 that was the lapse's only ask: the agent is gone, so nothing else would ask, and a fixed fan duty or a
    /// lowered limit stayed until a reboot. Now the GPU is asked again, 30 s later and then less often, until it goes back.</summary>
    [Fact]
    public void A_release_that_did_not_go_back_is_asked_again_until_it_does()
    {
        var asked = new List<DateTimeOffset>();
        var log = new List<string>();
        var now = T0;
        int busy = 3;
        var lease = new TrialLease(r => { asked.Add(now); return busy-- > 0 ? Busy() : Released(); }, ["gpu-A"], () => T0, log.Add);

        foreach (int s in new[] { 300, 315, 330, 345, 360, 390, 420, 450, 480, 510, 540, 600 })
        {
            now = T0.AddSeconds(s);
            lease.Look(now);
        }
        // Asked at 5:00, again 30 s later, then 60 s and 120 s after that; the fourth ask landed and the lapse is done.
        Assert.Equal([300, 330, 390, 510], asked.Select(a => (int)(a - T0).TotalSeconds));
        Assert.Contains("asking again in 30 s", log[0]);
        Assert.Contains("asking again in 60 s", log[1]);
        Assert.Contains("a stability run is in progress", log[0]);
        Assert.Contains("handed back (ask 4)", log[^1]);
        Assert.False(lease.Look(T0.AddMinutes(30)));
    }

    /// <summary>Each GPU is asked again on its own: one that went back is not asked twice in a lapse.</summary>
    [Fact]
    public void Only_the_gpu_that_did_not_go_back_is_asked_again()
    {
        var asked = new List<string>();
        var lease = new TrialLease(r => { asked.Add(r.GpuId!); return r.GpuId == "gpu-B" && asked.Count(g => g == "gpu-B") == 1 ? Busy() : Released(); },
            ["gpu-A", "gpu-B"], () => T0, _ => { });

        Assert.True(lease.Look(T0.AddMinutes(5)));
        Assert.False(lease.Look(T0.AddMinutes(5).AddSeconds(15)));   // not yet: 30 s
        Assert.True(lease.Look(T0.AddMinutes(5).AddSeconds(30)));
        Assert.Equal(["gpu-A", "gpu-B", "gpu-B"], asked);
        Assert.False(lease.Look(T0.AddMinutes(20)));
    }

    /// <summary>An agent heard again holds the trial-only settings once more: the asking stops with the lapse.</summary>
    [Fact]
    public void An_agent_heard_again_ends_the_asking()
    {
        var alive = T0;
        int asks = 0;
        var lease = new TrialLease(_ => { asks++; return Busy(); }, ["gpu-A"], () => alive, _ => { });

        Assert.True(lease.Look(T0.AddMinutes(5)));
        alive = T0.AddMinutes(5).AddSeconds(10);
        Assert.False(lease.Look(T0.AddMinutes(6)));
        Assert.Equal(1, asks);
    }

    /// <summary>An ask that throws waits its turn as one that failed, and the GPUs after it are still asked: the service's
    /// loop around the lease restarts it every few seconds, which would otherwise ask the same GPU each time.</summary>
    [Fact]
    public void An_ask_that_throws_waits_like_a_failed_one_and_the_others_are_still_asked()
    {
        var asked = new List<string>();
        var log = new List<string>();
        var lease = new TrialLease(r => { asked.Add(r.GpuId!); return r.GpuId == "gpu-A" ? throw new KeyNotFoundException("no such cell") : Released(); },
            ["gpu-A", "gpu-B"], () => T0, log.Add);

        Assert.True(lease.Look(T0.AddMinutes(5)));
        Assert.Equal(["gpu-A", "gpu-B"], asked);
        Assert.Contains("the ask threw: System.Collections.Generic.KeyNotFoundException: no such cell", log[0]);   // the whole exception, for the log
        Assert.Contains("KeyNotFoundException: no such cell; asking again in 30 s", log[1]);
        Assert.False(lease.Look(T0.AddMinutes(5).AddSeconds(5)));
        Assert.True(lease.Look(T0.AddMinutes(5).AddSeconds(30)));
        Assert.Equal(["gpu-A", "gpu-B", "gpu-A"], asked);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]       // at most the lease's own 5 minutes
    [InlineData(20, 300)]
    public void Asking_again_waits_longer_each_time_up_to_five_minutes(int misses, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), TrialLease.Again(misses));
}
