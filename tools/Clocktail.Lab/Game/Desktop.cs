using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Clocktail.Lab.Game;

/// <summary>The game's window: its size on the primary screen, in physical pixels (Q2RTX declares itself per-monitor DPI
/// aware, so its vid_geometry is in physical pixels too).</summary>
public sealed record WindowPlan(int ScreenWidth, int ScreenHeight, int Width, int Height, int X, int Y, int RefreshHz)
{
    /// <summary>Q2RTX's vid_geometry: "WxH+X+Y", the client area.</summary>
    public string Geometry => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}+{X}+{Y}");
    public string Words => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height} window on a {ScreenWidth}x{ScreenHeight} screen ({100.0 * Width * Height / ScreenWidth / ScreenHeight:0.#} % of screen area)");

    /// <summary>At least pct % per axis and 90 % of screen area (90 % on each axis alone covers only 81 %). Even sizes,
    /// centred, within the game's own limits (320x240 .. 8192x8192).</summary>
    public static WindowPlan For(int screenW, int screenH, int pct, int refreshHz)
    {
        if (screenW < 640 || screenH < 480) throw new InvalidOperationException($"the primary screen reads {screenW}x{screenH}: too small for the test");
        pct = Math.Clamp(pct, 90, 100);
        double fraction = Math.Max(pct / 100.0, Math.Sqrt(0.9));
        int w = Math.Min(8192, (int)Math.Ceiling(screenW * fraction / 2) * 2), h = Math.Min(8192, (int)Math.Ceiling(screenH * fraction / 2) * 2);
        w = Math.Min(w, screenW - screenW % 2);
        h = Math.Min(h, screenH - screenH % 2);
        if (w < screenW * 0.9 || h < screenH * 0.9 || (double)w * h < (double)screenW * screenH * 0.9)
            throw new NotSupportedException("the game's 8192-pixel size limit cannot cover 90 % of this screen");
        return new WindowPlan(screenW, screenH, w, h, (screenW - w) / 2, (screenH - h) / 2, refreshHz);
    }
}

/// <summary>
/// The desktop the game runs on: the primary screen's mode, the game's window by its process, and the foreground. Windows
/// lets a program without a window take the foreground only in some cases; the game was started by this job, and the
/// thread-input attach below is the usual way for a helper to hand its own child the foreground. Nothing is typed or
/// clicked: no input is injected.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Desktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint owner, string text, string title, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private const int EnumCurrentSettings = -1, SwRestore = 9;
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;

    /// <summary>This process sees physical pixels (the game's window sizes read as the game set them).</summary>
    public static void PhysicalPixels()
    {
        try { SetProcessDpiAwarenessContext(-4); } catch (EntryPointNotFoundException) { }   // per-monitor v2
    }

    /// <summary>The primary screen's mode: width, height (physical pixels) and refresh rate.</summary>
    public static (int Width, int Height, int Hz) PrimaryScreen()
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref dm)) throw new InvalidOperationException("the primary screen's mode could not be read");
        return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency is > 1 and < 1000 ? dm.dmDisplayFrequency : 60);
    }

    /// <summary>The game's main window: a visible top-level SDL window of its process; 0 when there is none (yet).</summary>
    public static nint WindowOf(int pid)
    {
        nint found = 0;
        EnumWindows((h, _) =>
        {
            if (GetWindowThreadProcessId(h, out uint p) != 0 && p == pid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, sb.Capacity);
                if (sb.ToString().StartsWith("SDL", StringComparison.Ordinal)) { found = h; return false; }
            }
            return true;
        }, 0);
        return found;
    }

    public static (int Width, int Height)? ClientSize(nint hwnd) => hwnd != 0 && GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : null;

    public static bool InFront(int pid)
    {
        var fg = GetForegroundWindow();
        return fg != 0 && GetWindowThreadProcessId(fg, out uint p) != 0 && p == pid;
    }

    /// <summary>The game's window to the front: restored if minimized, then the foreground, through the foreground thread's
    /// input queue when Windows does not hand it over directly. True when it is in front afterwards.</summary>
    public static bool ToFront(int pid)
    {
        nint h = WindowOf(pid);
        if (h == 0) return false;
        if (IsIconic(h)) ShowWindow(h, SwRestore);
        else if (InFront(pid)) return true;   // never taken again from itself
        if (SetForegroundWindow(h) && InFront(pid)) return true;
        nint fg = GetForegroundWindow();
        uint me = GetCurrentThreadId(), them = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        bool attached = them != 0 && them != me && AttachThreadInput(me, them, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { if (attached) AttachThreadInput(me, them, false); }
        return InFront(pid);
    }

    /// <summary>Keeps the PC and the display awake while the test runs (the screen saver is the game's to hold off: SDL
    /// blocks it while its window is in front); released by <see cref="LetSleep"/>.</summary>
    public static bool ConfirmModel(Clocktail.Hw.GpuTarget target)
    {
        if (target.Validated) return true;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", "game-model-" + target.DeviceId.ToString("x8") + ".confirmed");
        if (File.Exists(path)) return true;
        if (MessageBoxW(0, $"{target.Name} at {target.Pci} has not been validated on hardware.\nThe driver identity and curve-layout checks still apply.\nAllow game tests on this model?", "Clocktail: unvalidated GPU model", 0x00010000 | 0x00000100 | 0x00000030 | 0x00000004) != 6) return false;
        File.WriteAllText(path, "Confirmed by the person in the Clocktail window.\n");
        return true;
    }

    public static void StayAwake() => SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
    public static void LetSleep() => SetThreadExecutionState(EsContinuous);
}
