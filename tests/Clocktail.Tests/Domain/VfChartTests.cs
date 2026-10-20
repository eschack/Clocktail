using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

public class VfChartTests
{
    /// <summary>A 5090-shaped curve: the 180 MHz floor to 740 mV, a steep climb, then a slope to 1200 mV; now = stock +500
    /// MHz cut flat at 3082 MHz.</summary>
    private static (List<(double Mv, double Mhz)> Now, List<(double Mv, double Mhz)> Stock) Curve()
    {
        var stock = new List<(double, double)>();
        for (double mv = 450; mv <= 1200; mv += 5)
            stock.Add((mv, mv <= 740 ? 180 : mv <= 850 ? 180 + (mv - 740) * 16 : 1940 + (mv - 850) * 3.3));
        var now = stock.Select(p => (p.Item1, p.Item2 <= 180 ? p.Item2 : Math.Min(p.Item2 + 500, 3082))).ToList();
        return (now, stock);
    }

    [Fact]
    public void Spans_from_half_the_top_to_the_limit()
    {
        var (now, stock) = Curve();
        var (x0, x1, y0, y1) = VfChart.Bounds(now, stock, 1095);
        Assert.Equal(1095, x1);
        Assert.InRange(x0, 790, 800);   // where now first reaches 3082 / 2: the floor and most of the climb are left out
        Assert.Equal(0, y0 % 100);
        Assert.Equal(3100, y1);
    }

    [Fact]
    public void Draws_braille_rows_axis_and_labels()
    {
        var (now, stock) = Curve();
        var lines = VfChart.Draw(now, stock, at: (1000, 3000), maxMv: 1095, width: 40, height: 8);
        Assert.Equal(8 + 2, lines.Count);
        Assert.StartsWith(" 3100┤", lines[0]);
        Assert.All(lines.Take(8), l => Assert.All(l[6..], ch => Assert.True(ch is >= (char)0x2800 and <= (char)0x28ff || ch == VfChart.Running)));
        Assert.Single(string.Concat(lines), ch => ch == VfChart.Running);
        Assert.EndsWith("limit 1095 mV", lines[^1]);
        // The flat top: the top row carries dots across its right half.
        Assert.True(lines[0][^10..].All(ch => ch != (char)0x2800));
    }

    [Fact]
    public void An_idle_point_below_the_curve_is_not_drawn()
    {
        var (now, stock) = Curve();
        var lines = VfChart.Draw(now, stock, at: (805, 285), maxMv: 1095);
        Assert.DoesNotContain(string.Concat(lines), ch => ch == VfChart.Running);
        var (x0, x1, y0, y1) = VfChart.Bounds(now, stock, 1095);
        Assert.False(VfChart.Contains((805, 285), x0, x1, y0, y1));
    }

    [Fact]
    public void Too_few_points_draw_nothing() =>
        Assert.Empty(VfChart.Draw([(800, 1000)], null, null, 1095));
}
