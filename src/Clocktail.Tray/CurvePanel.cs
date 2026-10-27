using System.Drawing.Drawing2D;
using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal sealed class CurvePanel(IReadOnlyList<StatusCurvePoint> points, Palette palette) : Control
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        AccessibleName = "Verified effective voltage-frequency curve, millivolts versus MHz. " +
            (points.Count == 0 ? "Unavailable." : $"{points.Count} points. Effective frequencies {points.Min(p => p.EffectiveMhz):0} to {points.Max(p => p.EffectiveMhz):0} MHz.");
        var g = e.Graphics;
        g.Clear(palette.Surface);
        using var ink = new SolidBrush(palette.Muted);
        if (points.Count < 2)
        {
            g.DrawString("Effective curve unavailable.\nUse Check now for explicit readback.", Font, ink, 22, 26);
            return;
        }
        var plot = new RectangleF(55, 25, Math.Max(1, Width - 80), Math.Max(1, Height - 85));
        double minX = points.Min(p => p.Millivolts), maxX = points.Max(p => p.Millivolts);
        double minY = Math.Floor(points.Min(p => Math.Min(p.EffectiveMhz, p.StockMhz ?? p.EffectiveMhz)) / 100) * 100;
        double maxY = Math.Ceiling(points.Max(p => p.EffectiveMhz) / 100) * 100;
        if (maxX <= minX) maxX = minX + 1;
        if (maxY <= minY) maxY = minY + 1;
        PointF At(StatusCurvePoint p, double y) => new(plot.Left + (float)((p.Millivolts - minX) / (maxX - minX)) * plot.Width,
            plot.Bottom - (float)((y - minY) / (maxY - minY)) * plot.Height);
        using var rule = new Pen(palette.Rule);
        for (int i = 0; i <= 3; i++)
        {
            float y = plot.Top + plot.Height * i / 3;
            g.DrawLine(rule, plot.Left, y, plot.Right, y);
            g.DrawString($"{maxY - (maxY - minY) * i / 3:0}", Font, ink, 3, y - 7);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var applied = new Pen(palette.Accent, 2);
        g.DrawLines(applied, points.Select(p => At(p, p.EffectiveMhz)).ToArray());
        if (points.All(p => p.StockMhz != null))
        {
            using var stock = new Pen(palette.Muted, 1) { DashStyle = DashStyle.Dash };
            g.DrawLines(stock, points.Select(p => At(p, p.StockMhz!.Value)).ToArray());
        }
        g.DrawString("MHz", Font, ink, 3, 4);
        g.DrawString($"{minX:0} mV", Font, ink, plot.Left, plot.Bottom + 5);
        g.DrawString($"{maxX:0} mV", Font, ink, plot.Right - 55, plot.Bottom + 5);
        g.DrawString("Applied effective curve   ·   Dashed: stock reference when known", Font, ink, 15, Height - 27);
    }
}
