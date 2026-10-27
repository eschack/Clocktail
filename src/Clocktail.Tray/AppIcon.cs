using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal static class AppIcon
{
    public static Icon Create(TrayState state, bool quiet)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var face = new SolidBrush(Color.FromArgb(32, 34, 38));
        graphics.FillEllipse(face, 1, 1, 30, 30);
        using var dial = new Pen(Color.FromArgb(232, 235, 240), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(dial, 6, 6, 20, 20, 145, 250);
        graphics.DrawLine(dial, 16, 17, 22, 10);
        using var accent = new SolidBrush(quiet ? Color.LightSteelBlue : state == TrayState.DailyMatched ? Color.FromArgb(89, 205, 178)
            : state is TrayState.Unknown or TrayState.Unavailable or TrayState.Stale ? Color.Gray : Color.Goldenrod);
        graphics.FillEllipse(accent, 21, 22, 9, 9);
        nint handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
