using System.Security.Principal;

namespace Clocktail.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        NativeTheme.FollowSystem();
        if (args.Contains("--help"))
        {
            MessageBox.Show("Clocktail.Tray [--demo matched|trial|stale|unavailable|suspended|different|quiet] [--render DIRECTORY]\n\nLaunch is read-only. No installation or autostart registration is performed.", "Clocktail Tray");
            return;
        }
        string? demo = Option(args, "--demo");
        string? render = Option(args, "--render");
        if (demo != null && !DemoSource.States.Contains(demo) || render != null && demo == null)
        {
            MessageBox.Show("Choose a documented --demo state. --render requires --demo.", "Clocktail Tray");
            return;
        }
        if (render != null) { PreviewRenderer.Render(render, demo!); return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Launch Clocktail Tray from a normal, unelevated desktop session.", "Clocktail Tray");
            return;
        }
        using var single = new Mutex(true, $"Local\\Clocktail.Tray.{identity.User?.Value}", out bool first);
        if (!first) return;
        try { using var context = new TrayContext(demo); Application.Run(context); }
        finally { single.ReleaseMutex(); }
    }

    private static string? Option(string[] args, string key)
    {
        int at = Array.IndexOf(args, key);
        return at < 0 ? null : at + 1 < args.Length ? args[at + 1] : "";
    }
}
