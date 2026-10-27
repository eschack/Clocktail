namespace Clocktail.Tray;

internal static class NativeTheme
{
    // .NET 10's native theme support also colors non-client controls such as scrollbars.
    // Keep the experimental API isolated; custom content continues to use our explicit palette.
#pragma warning disable WFO5001
    public static void FollowSystem() => Application.SetColorMode(SystemInformation.HighContrast ? SystemColorMode.Classic : SystemColorMode.System);
    public static void Preview(bool dark) => Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
}
