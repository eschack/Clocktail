using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

public class ResetBurstsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 21, 47, 0, TimeSpan.Zero);

    [Fact]
    public void The_device_restart_of_2026_10_19_is_two_resets_not_thirteen()
    {
        // nvlddmkm 153 as the System log has them: the TDR at 21:47:25.9, then the restart of the GPU it left hung.
        double[] seconds = [25.891, 70.401, 70.407, 70.419, 70.432, 70.446, 70.460, 70.474, 70.488, 70.500, 72.314, 86.469, 88.135, 88.152];
        var bursts = new ResetBursts(ResetBursts.DefaultQuiet);
        var starts = seconds.Where(s => bursts.Starts(T0.AddSeconds(s))).ToList();
        Assert.Equal([25.891, 70.401, 86.469], starts);
    }

    [Fact]
    public void A_storm_that_keeps_going_stays_one_reset()
    {
        var bursts = new ResetBursts(TimeSpan.FromSeconds(3));
        Assert.True(bursts.Starts(T0));
        for (int i = 1; i <= 30; i++) Assert.False(bursts.Starts(T0.AddSeconds(2.5 * i)));
        Assert.True(bursts.Starts(T0.AddSeconds(2.5 * 30 + 3)));
    }
}
