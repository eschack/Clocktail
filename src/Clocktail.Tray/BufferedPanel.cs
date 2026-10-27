namespace Clocktail.Tray;

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ApplyThemingImplicitly, true);
    }
}

/// <summary>Commit a full status presentation in one layout/paint, including native scrollbars.</summary>
internal sealed class ScrollLayoutUpdate : IDisposable
{
    private readonly Control _control;
    private readonly bool _redraw;
    public ScrollLayoutUpdate(Control control)
    {
        _control = control;
        _redraw = control.IsHandleCreated && control.Visible;
        if (_redraw) SendMessage(control.Handle, 0xB, 0, 0); // WM_SETREDRAW
        control.SuspendLayout();
    }
    public void Dispose()
    {
        _control.ResumeLayout(true);
        if (_redraw && !_control.IsDisposed)
        {
            SendMessage(_control.Handle, 0xB, 1, 0);
            RedrawWindow(_control.Handle, 0, 0, 0x581); // INVALIDATE | ALLCHILDREN | UPDATENOW | FRAME
        }
    }
    public void LayoutNow()
    {
        _control.ResumeLayout(true);
        _control.SuspendLayout();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wparam, nint lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
}
