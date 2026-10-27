using System.Drawing.Drawing2D;

namespace Clocktail.Tray;

internal enum Glyph { Gauge, Pin, Close, Check, CheckCheck, Thermometer, Zap, CircleCheck, Refresh, Moon, ChevronDown, ChevronUp, Attention }

/// <summary>Locally drawn, DPI-independent line icons; no symbol-font fallback or network assets.</summary>
internal static class LineIcons
{
    public static void Draw(Graphics graphics, Glyph glyph, RectangleF bounds, Color color)
    {
        var saved = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(params PointF[] points) => graphics.DrawLines(pen, points);
        switch (glyph)
        {
            case Glyph.Gauge:
                graphics.DrawArc(pen, 3, 3, 18, 18, 145, 250);
                Line(new(12, 13), new(17, 8));
                break;
            case Glyph.Pin:
                Line(new(8, 3), new(16, 3), new(15, 9), new(19, 13), new(19, 15), new(5, 15), new(5, 13), new(9, 9), new(8, 3));
                Line(new(12, 15), new(12, 21));
                break;
            case Glyph.Close:
                Line(new(6, 6), new(18, 18)); Line(new(18, 6), new(6, 18));
                break;
            case Glyph.Check:
                Line(new(5, 12), new(10, 17), new(20, 7));
                break;
            case Glyph.CheckCheck:
                Line(new(2, 12), new(7, 17), new(17, 7)); Line(new(12, 15), new(14, 17), new(23, 8));
                break;
            case Glyph.CircleCheck:
                graphics.DrawEllipse(pen, 3, 3, 18, 18); Line(new(7, 12), new(10.5f, 15.5f), new(17, 9));
                break;
            case Glyph.Thermometer:
                using (var outline = new GraphicsPath())
                {
                    outline.AddArc(9, 2, 6, 6, 180, 180);
                    outline.AddLine(15, 5, 15, 14);
                    outline.AddArc(7, 12, 10, 10, 307, 286);
                    outline.AddLine(9, 14, 9, 5);
                    graphics.DrawPath(pen, outline);
                }
                Line(new(12, 8), new(12, 17));
                break;
            case Glyph.Zap:
                Line(new(13, 2), new(4, 13), new(11, 13), new(10, 22), new(21, 10), new(14, 10), new(13, 2));
                break;
            case Glyph.Refresh:
                graphics.DrawArc(pen, 3, 4, 17, 16, 55, 270);
                Line(new(20, 4), new(20, 9), new(15, 9));
                break;
            case Glyph.Moon:
                using (var moon = new GraphicsPath())
                {
                    moon.AddArc(3, 3, 18, 18, 15, 270);
                    moon.AddBezier(new PointF(9.7f, 3.3f), new PointF(5, 13), new PointF(14, 20), new PointF(20.7f, 14.3f));
                    graphics.DrawPath(pen, moon);
                }
                break;
            case Glyph.ChevronDown: Line(new(6, 9), new(12, 15), new(18, 9)); break;
            case Glyph.ChevronUp: Line(new(6, 15), new(12, 9), new(18, 15)); break;
            case Glyph.Attention:
                graphics.DrawEllipse(pen, 3, 3, 18, 18); Line(new(12, 7), new(12, 12));
                graphics.DrawEllipse(pen, 11.8f, 16.3f, .4f, .4f);
                break;
        }
        graphics.Restore(saved);
    }

    public static Bitmap Bitmap(Glyph glyph, int pixels, Color color)
    {
        var bitmap = new Bitmap(pixels, pixels);
        using var graphics = Graphics.FromImage(bitmap);
        Draw(graphics, glyph, new(0, 0, pixels, pixels), color);
        return bitmap;
    }
}

internal sealed class GlyphView(Glyph glyph, Color ink) : Control
{
    public GlyphView(Glyph glyph, Color ink, int size) : this(glyph, ink)
    {
        Size = new(size, size);
        Margin = new(0, 0, 8, 0);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int inset = BackColor == Color.Transparent ? 1 : Math.Max(3, Width / 5);
        LineIcons.Draw(e.Graphics, glyph, new(inset, inset, Width - inset * 2, Height - inset * 2), ink);
    }
}

internal sealed class GlyphButton : Button
{
    private readonly Glyph _glyph;
    private readonly Color _ink;
    private readonly Palette _palette;
    public GlyphButton(Glyph glyph, Palette palette)
    {
        _glyph = glyph; _ink = palette.Muted; _palette = palette;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        TextImageRelation = TextImageRelation.ImageBeforeText;
        UpdateImage();
    }
    private void UpdateImage()
    {
        var previous = Image;
        Image = LineIcons.Bitmap(_glyph, Math.Max(16, 16 * DeviceDpi / 96), _ink);
        previous?.Dispose();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateImage(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        // ButtonBase's disabled renderer substitutes system colors for ForeColor and grays the bitmap.
        // Retain disabled input/accessibility semantics, drawing a legible muted label and icon ourselves.
        e.Graphics.Clear(BackColor);
        if (FlatAppearance.BorderSize > 0)
        {
            using var border = new Pen(_palette.Rule, FlatAppearance.BorderSize);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        int iconSize = Math.Max(16, 16 * DeviceDpi / 96), gap = Text.Length == 0 ? 0 : 6 * DeviceDpi / 96;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, flags).Width;
        int start = Math.Max(Padding.Left, (ClientSize.Width - iconSize - gap - textWidth) / 2);
        LineIcons.Draw(e.Graphics, _glyph, new(start, (ClientSize.Height - iconSize) / 2, iconSize, iconSize), _palette.Muted);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(start + iconSize + gap, 0,
            Math.Max(1, ClientSize.Width - start - iconSize - gap - Padding.Right), ClientSize.Height), _palette.Muted, flags);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { var image = Image; Image = null; image?.Dispose(); }
        base.Dispose(disposing);
    }
}
