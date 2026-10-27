using System.Reflection;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The tools hold the driver's and the firmware's limits and none of their own (the user, 2026-10-27).</summary>
public class CurveToolsTests
{
    [Fact]
    public void No_cap_or_step_of_the_tools_own_is_left()
    {
        var names = typeof(TuningTools).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Select(m => m.Name).ToHashSet();
        foreach (var gone in new[] { "StepMhz", "MaxAboveDailyMhz", "OverDaily", "MinMv", "ShiftStepMv", "MemoryStepMhz", "XbarStepMhz",
                                     "VdemandStepMv", "BoostStepPct", "RatioStep", "MaxPowerW", "MinPowerW", "MinNvvddA", "MinMsvddA", "FlattenMaxUtilPct" })
            Assert.DoesNotContain(gone, names);
    }
}
