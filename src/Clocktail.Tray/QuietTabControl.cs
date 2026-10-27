namespace Clocktail.Tray;

/// <summary>Keeps native tab selection/keyboard/accessibility, painting the chrome with the surrounding palette.</summary>
internal sealed class QuietTabControl : TabControl
{
    private readonly Palette _palette;
    public QuietTabControl(Palette palette)
    {
        _palette = palette;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_palette.Surface);
        for (int i = 0; i < TabPages.Count; i++)
        {
            var bounds = GetTabRect(i);
            TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, bounds,
                i == SelectedIndex ? _palette.Ink : _palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (i == SelectedIndex)
            {
                using var pen = new Pen(_palette.Accent, 2);
                e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, bounds, _palette.Ink, _palette.Surface);
            }
        }
    }
}
