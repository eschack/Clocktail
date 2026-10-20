using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class TemperatureMarkTests
{
    [Theory]
    [InlineData(36, 54.0, "✅")]
    [InlineData(84, 6.0, "✅")]
    [InlineData(84, 4.0, "⚠️")]    // the driver's margin says it is close before the fixed mark does
    [InlineData(85, null, "⚠️")]
    [InlineData(88, 0.0, "⛔")]     // no margin left: NVIDIA's own slowdown point
    [InlineData(90, null, "⛔")]
    public void Marks_come_from_the_margin_and_the_fixed_points(double temp, double? margin, string mark) =>
        Assert.Equal(mark, SettingsTools.TemperatureMark(temp, margin));
}
