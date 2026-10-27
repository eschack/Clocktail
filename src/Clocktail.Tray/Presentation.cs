using System.Globalization;
using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal static class Presentation
{
    public static readonly (string Label, string Kind, string Key)[] Rows =
    [("Core offset", "pstates20", "core"), ("Memory offset", "pstates20", "memory"),
     ("XBAR offset", "clockdomain", "xbar"), ("Power limit", "power", "fe"),
     ("Voltage boost (legacy)", "boost", "legacy"), ("XBAR voltage demand", "vdemand", "xbar")];

    public static (string Title, string Detail) Describe(TrayState state) => state switch
    {
        TrayState.DailyMatched => ("Daily profile is applied", "Verified settings match your saved daily targets."),
        TrayState.TrialActive => ("Temporary trial is active", "The card may differ from your saved daily profile."),
        TrayState.DailySuspended => ("Daily restoration is suspended", "Saved targets are retained; applied settings are shown separately."),
        TrayState.SettingsDiffer => ("Settings differ from saved daily", "Check the comparison below before taking further action."),
        TrayState.Stale => ("Cached status is stale", "Use Check now for a fresh verification when you are ready."),
        TrayState.Applying => ("Broker operation in progress", "Prior verification has been invalidated. Check again afterward."),
        TrayState.Failed => ("Status needs attention", "Some broker records could not be read. See Diagnostics."),
        TrayState.Unavailable => ("Broker status unavailable", "Use Check now to reconnect. Cached readings are unconfirmed."),
        _ => ("Applied settings are unverified", "Saved targets do not establish what is currently on the GPU.")
    };

    public static string Applied(StatusDevice? d, string kind, string key) => Format(kind,
        d?.Verification?.Settings.FirstOrDefault(s => s.Kind == kind && s.Key == key)?.Value);
    public static string Saved(StatusDevice? d, string kind, string key)
    {
        if (d == null || !d.SavedDailyKnown) return "Unknown";
        var write = d.SavedDaily?.Writes.FirstOrDefault(w => w.Kind == kind && w.Key == key);
        return write == null ? "Not saved" : Format(kind, write.Value);
    }
    private static string Format(string kind, string? value)
    {
        if (value == null) return "Unavailable";
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        return kind switch
        {
            "power" => $"{number / 1000:0.##} W",
            "boost" => $"{number:0.##}%",
            "vdemand" => $"{number / 1000:+0.##;-0.##;0} mV",
            _ => $"{number / 1000:+0.##;-0.##;0} MHz"
        };
    }
    public static string Age(DateTimeOffset? at, string prefix)
    {
        if (at == null) return $"{prefix}: unavailable";
        var age = DateTimeOffset.UtcNow - at.Value;
        if (age < TimeSpan.Zero) return $"{prefix}: clock mismatch";
        return $"{prefix} {Math.Floor(age.TotalSeconds):0} s ago";
    }
}

internal sealed record Palette(Color Surface, Color Secondary, Color Ink, Color Muted, Color Rule, Color Accent, Color AccentSurface)
{
    public static Palette Current
    {
        get
        {
            if (SystemInformation.HighContrast) return new(SystemColors.Window, SystemColors.Control, SystemColors.WindowText,
                SystemColors.WindowText, SystemColors.WindowText, SystemColors.HighlightText, SystemColors.Highlight);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            bool light = key?.GetValue("AppsUseLightTheme") is int v && v != 0;
            return light ? Light : Dark;
        }
    }
    public static Palette Dark => new(C("202226"), C("282b30"), C("e8ebf0"), C("a1a8b3"), C("373b42"), C("59cdb2"), C("243c36"));
    public static Palette Light => new(C("fcfcfd"), C("f1f3f5"), C("24282f"), C("63707c"), C("e2e6e9"), C("137966"), C("e8f5f0"));
    private static Color C(string hex) => ColorTranslator.FromHtml("#" + hex);
}
