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
    public void The_frame_is_on_a_readable_scale_from_the_tuned_part_to_the_limit()
    {
        var (now, stock) = Curve();
        var f = VfChart.Bounds(now, stock, 1095)!;
        Assert.Equal(100, f.MhzPerLine);
        Assert.Equal(5, f.MvPerChar);
        Assert.Equal(800, f.X0);        // 60 % of the top is reached at about 815 mV; down to the numbered 50 mV step
        Assert.Equal(1095, f.X1);
        Assert.Equal(3100, f.Y1);
        Assert.Equal(0, f.Y0 % 100);
        Assert.Equal(60, f.Chars);      // 800..1095 at 5 mV a character
    }

    [Fact]
    public void Every_line_carries_its_clock_and_the_axis_its_voltages()
    {
        var (now, stock) = Curve();
        var f = VfChart.Bounds(now, stock, 1095)!;
        var lines = VfChart.Draw(now, stock, at: (1000, 3000), maxMv: 1095);
        Assert.Equal(f.Lines + 2, lines.Count);
        for (int r = 0; r < f.Lines; r++)
            Assert.StartsWith($"{f.Y1 - r * 100,5}┤", lines[r]);
        Assert.All(lines.Take(f.Lines), l => Assert.All(l[6..], ch => Assert.True(ch is >= (char)0x2800 and <= (char)0x28ff || ch == VfChart.Running)));
        Assert.Single(string.Concat(lines), ch => ch == VfChart.Running);
        // A tick every 25 mV, the right edge a corner.
        Assert.Equal("     └┬────┬", lines[^2][..12]);
        Assert.EndsWith("┘", lines[^2]);
        foreach (var mv in new[] { "800", "850", "900", "950", "1000", "1050", "1095 mV" })
            Assert.Contains(mv, lines[^1]);
        // The flat top: the top line carries dots across its right end.
        Assert.True(lines[0][^10..].All(ch => ch != (char)0x2800));
    }

    [Fact]
    public void A_number_sits_under_its_tick()
    {
        var (now, stock) = Curve();
        var lines = VfChart.Draw(now, stock, null, 1095);
        int tick = lines[^2].IndexOf('┬', 7);           // the second tick: 825 mV; the one after, 850
        tick = lines[^2].IndexOf('┬', tick + 1);
        Assert.Equal("850", lines[^1].Substring(tick - 1, 3));
    }

    [Fact]
    public void An_idle_point_below_the_curve_is_not_drawn()
    {
        var (now, stock) = Curve();
        var lines = VfChart.Draw(now, stock, at: (805, 285), maxMv: 1095);
        Assert.DoesNotContain(string.Concat(lines), ch => ch == VfChart.Running);
        Assert.False(VfChart.Bounds(now, stock, 1095)!.Contains((805, 285)));
    }

    [Fact]
    public void A_wide_curve_takes_a_coarser_scale()
    {
        var pts = Enumerable.Range(0, 120).Select(i => (500.0 + i * 5, 1000.0 + i * 20)).ToList();
        var f = VfChart.Bounds(pts, null, null)!;
        Assert.True(f.MvPerChar > 5);
        Assert.True(f.Chars <= 72);
    }

    [Fact]
    public void Too_few_points_draw_nothing() =>
        Assert.Empty(VfChart.Draw([(800, 1000)], null, null, 1095));
}
