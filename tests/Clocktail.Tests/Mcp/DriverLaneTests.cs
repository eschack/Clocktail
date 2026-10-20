using System.Diagnostics;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>A call stuck in the driver (a hung GPU waits out the TDR delay inside NVAPI) must not hang every tool behind it.</summary>
public class DriverLaneTests
{
    [Fact]
    public async Task A_caller_behind_a_stuck_call_is_told_after_the_wait_and_the_next_one_at_once()
    {
        var lane = new DriverLane(TimeSpan.FromMilliseconds(300));
        using var stuck = new ManualResetEventSlim();
        using var inside = new ManualResetEventSlim();
        var holder = Task.Run(() => { using (lane.Enter("a sample")) { inside.Set(); stuck.Wait(); } });
        inside.Wait();

        var t = Stopwatch.StartNew();
        var first = Assert.Throws<DriverBusyException>(() => lane.Enter("a full read"));
        Assert.InRange(t.ElapsedMilliseconds, 250, 3000);
        Assert.Contains("a sample is still waiting on it", first.Message);
        Assert.Contains("nothing was read", first.Message);

        t.Restart();
        Assert.Throws<DriverBusyException>(() => lane.Enter("another read"));   // held past the wait: no waiting at all
        Assert.InRange(t.ElapsedMilliseconds, 0, 200);

        stuck.Set();
        await holder;
        using (lane.Enter("after it returned")) { }   // the lane is free again once the driver answers
    }

    [Fact]
    public async Task The_holder_can_enter_again_and_a_short_hold_is_waited_for()
    {
        var lane = new DriverLane(TimeSpan.FromSeconds(2));
        using (lane.Enter("outer")) using (lane.Enter("inner")) { }
        var holder = Task.Run(() => { using (lane.Enter("a quick read")) Thread.Sleep(150); });
        Thread.Sleep(30);
        using (lane.Enter("next")) { }   // waits the 150 ms out, no error
        await holder;
    }
}
