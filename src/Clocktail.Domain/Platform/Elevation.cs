using System.Runtime.Versioning;
using System.Security.Principal;

namespace Clocktail.Domain.Platform;

/// <summary>Whether this process may make the private driver writes (NVAPI answers -137 to a non-administrator).</summary>
public static class Elevation
{
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True on Windows only when not elevated; false off Windows (the driver and its -137 rule are Windows-only).</summary>
    public static bool NeedsElevationHere() => OperatingSystem.IsWindows() && !IsAdministrator();
}
