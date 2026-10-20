using Clocktail.Domain.Power;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class PerfDecreaseTests
{
    [Fact]
    public void The_public_bits_by_name()
    {
        Assert.Empty(Card.PerfDecreaseReasons(0));   // idle and the 600 W wall on the reference card
        Assert.Equal(["insufficient_power"], Card.PerfDecreaseReasons(0x10));
        Assert.Equal(["thermal_protection", "power_control"], Card.PerfDecreaseReasons(0x3));
    }

    [Fact]
    public void Power_topology_entries_are_thousandths_of_a_percent_of_the_default_limit()
    {
        // nvgwls at three loads under a 575 W default limit, 2026-10-20.
        Assert.InRange(PowerTopologyLayout.Watts(104_300, 575), 599, 601);
        Assert.InRange(PowerTopologyLayout.Watts(85_700, 575), 492, 494);
        Assert.InRange(PowerTopologyLayout.Watts(45_000, 575), 258, 260);
    }
}
