using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal static class PreviewRenderer
{
    /// <summary>Software-only visual check: renders a nonactivating window offscreen, without a pipe or tray icon.</summary>
    public static void Render(string directory, string state)
    {
        Directory.CreateDirectory(directory);
        var cache = new TrayStatusCache();
        var source = new DemoSource(state);
        if (source.Create() is { } snapshot && !cache.Accept(snapshot)) throw new InvalidOperationException(cache.Error);
        else if (cache.Snapshot == null) cache.Disconnect("Synthetic disconnected preview");
        cache.CompatibilityNotice = source.CompatibilityNotice;
        var expected = state switch
        {
            "startup" => TrayState.StartupPending, "recovery" => TrayState.Recovering,
            "changed" or "different" => TrayState.SettingsDiffer, "trial" => TrayState.TrialActive,
            "stale" => TrayState.Stale, "unavailable" => TrayState.Unavailable, "suspended" => TrayState.DailySuspended,
            _ => TrayState.DailyMatched
        };
        var device = cache.Snapshot?.Devices.FirstOrDefault();
        var actual = TrayStatus.State(device, cache.Connected, cache.Snapshot?.CapturedAt ?? DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        if (actual != expected) throw new InvalidOperationException($"Synthetic {state}: expected {expected}, got {actual}.");
        File.WriteAllText(Path.Combine(directory, $"{state}-state.json"), JsonSerializer.Serialize(new
        { expected = expected.ToString(), actual = actual.ToString(), controls_at = device?.Refresh?.Controls?.At, full_at = device?.Verification?.At }));
        NativeTheme.Preview(dark: true);
        foreach (var view in new[] { "hover", "details", "curve", "diagnostics" })
        {
            nint foreground = GetForegroundWindow();
            using var form = new StatusForm(view == "hover", Palette.Dark, offscreen: true) { CurveSelected = view == "curve", DiagnosticsExpanded = view == "diagnostics", Location = new Point(-32000, -32000) };
            form.FitToWorkArea(new Rectangle(0, 0, 1920, 1200));
            form.Present(cache, "demo-gpu", state == "quiet", true, false);
            form.Show();
            form.PerformLayout();
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(directory, $"{state}-{view}.png"), ImageFormat.Png);
            if (view == "hover" && (!form.Enabled || Descendants(form).OfType<Label>().Any(label => !label.Enabled)))
                throw new InvalidOperationException("The hover card is inheriting disabled text rendering.");
            if (view == "hover")
            {
                if ((GetWindowLong(form.Handle, -20) & 0x08000000) == 0 || SendMessage(form.Handle, 0x84, 0, 0) != -1)
                    throw new InvalidOperationException("The hover card lost its nonactivating, input-transparent behavior.");
                File.WriteAllText(Path.Combine(directory, $"{state}-hover-check.json"), JsonSerializer.Serialize(new
                { labels_enabled = true, nonactivating_style = true, mouse_hit_transparent = true }));
            }
            if (view == "details") VerifyPinToggle(form, directory, state);
            if (view == "diagnostics") VerifyDiagnostics(form, directory, state, state == "quiet");
            bool unchanged = foreground == GetForegroundWindow();
            File.WriteAllText(Path.Combine(directory, $"{state}-{view}-focus.json"), JsonSerializer.Serialize(new { foreground_unchanged = unchanged }));
            form.Hide();
            if (!unchanged) throw new InvalidOperationException("The offscreen preview changed the foreground window.");
        }
        NativeTheme.Preview(dark: false);
        using var light = new StatusForm(false, Palette.Light, offscreen: true) { Location = new Point(-32000, -32000) };
        light.FitToWorkArea(new Rectangle(0, 0, 1920, 1200));
        light.Present(cache, "demo-gpu", state == "quiet", true, false);
        light.Show(); light.PerformLayout();
        using var lightBitmap = new Bitmap(light.ClientSize.Width, light.ClientSize.Height);
        light.DrawToBitmap(lightBitmap, light.ClientRectangle);
        lightBitmap.Save(Path.Combine(directory, $"{state}-light.png"), ImageFormat.Png);
        light.DiagnosticsExpanded = true;
        light.Present(cache, "demo-gpu", state == "quiet", true, false);
        light.PerformLayout();
        using var lightDiagnostics = new Bitmap(light.ClientSize.Width, light.ClientSize.Height);
        light.DrawToBitmap(lightDiagnostics, light.ClientRectangle);
        lightDiagnostics.Save(Path.Combine(directory, $"{state}-light-diagnostics.png"), ImageFormat.Png);
        light.Hide();
    }
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wparam, nint lparam);

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void VerifyPinToggle(Form form, string directory, string state)
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var pin = form.Controls.Find("PinButton", true).OfType<Button>().Single();
        var scroll = form.Controls.Find("DetailsScroll", true).Single();
        var controls = Descendants(form).ToArray();
        var handles = controls.Where(c => c.IsHandleCreated).ToDictionary(c => c, c => c.Handle);
        int scrollbars = GetWindowLong(scroll.Handle, -16) & 0x00300000;
        if (state == "matched" && scrollbars != 0) throw new InvalidOperationException("Default details should fit without scrollbars.");
        int layoutEvents = 0;
        scroll.Layout += (_, _) => layoutEvents++;
        for (int i = 0; i < 20; i++)
        {
            pin.PerformClick();
            Application.DoEvents();
            if (!controls.SequenceEqual(Descendants(form)) || handles.Any(pair => pair.Key.IsDisposed || pair.Key.Handle != pair.Value)
                || (GetWindowLong(scroll.Handle, -16) & 0x00300000) != scrollbars || layoutEvents != 0)
                throw new InvalidOperationException("Pin/unpin rebuilt or relaid out the scrolling panel.");
        }
        File.WriteAllText(Path.Combine(directory, $"{state}-pin-toggle.json"), JsonSerializer.Serialize(new
        {
            toggles = 20, control_tree_preserved = true, native_handles_preserved = true, scrollbar_styles_preserved = true, scroll_layout_events = layoutEvents
        }));
    }

    private static void VerifyDiagnostics(Form form, string directory, string state, bool quiet)
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var scrolling = Descendants(form).Where(c => c.Visible && c.IsHandleCreated && (GetWindowLong(c.Handle, -16) & 0x00300000) != 0).ToArray();
        if (scrolling.Length != 0) throw new InvalidOperationException("Expanded diagnostics should fit without scrolling in a tall work area.");
        int expandedHeight = form.Height, collapsedHeight = 0;
        var check = form.Controls.Find("CheckNowButton", true).OfType<Button>().Single();
        bool checkEnabled = check.Enabled;
        int clicks = 0;
        check.Click += (_, _) => clicks++;
        if (quiet)
        {
            if (check.Enabled) throw new InvalidOperationException("Quiet mode enabled Check now.");
            check.PerformClick();
            if (clicks != 0) throw new InvalidOperationException("A disabled check button accepted a click.");
        }
        var scroll = (Panel)form.Controls.Find("DetailsScroll", true).Single();
        scroll.AutoScrollPosition = new Point(0, scroll.DisplayRectangle.Height);
        using var bottom = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bottom, form.ClientRectangle);
        bottom.Save(Path.Combine(directory, $"{state}-diagnostics-bottom.png"), ImageFormat.Png);
        for (int i = 0; i < 4; i++)
        {
            form.Controls.Find("DiagnosticsToggleButton", true).OfType<Button>().Single().PerformClick();
            Application.DoEvents();
            if (((StatusForm)form).DiagnosticsExpanded)
            {
                if (form.Height <= collapsedHeight) throw new InvalidOperationException("Opening diagnostics did not expand the panel.");
            }
            else collapsedHeight = form.Height;
            var bars = Descendants(form).Where(c => c.Visible && c.IsHandleCreated && (GetWindowLong(c.Handle, -16) & 0x00300000) != 0).ToArray();
            if (bars.Length > 1 || bars.Any(c => c.Name != "DetailsScroll"))
                throw new InvalidOperationException("Toggling diagnostics introduced nested scrollbars.");
        }
        var panel = (StatusForm)form;
        panel.FitToWorkArea(new Rectangle(0, 0, 800, 600));
        Application.DoEvents();
        var fallback = Descendants(form).Where(c => c.Visible && c.IsHandleCreated && (GetWindowLong(c.Handle, -16) & 0x00300000) != 0).ToArray();
        if (form.Height > 600 || fallback.Length != 1 || fallback[0].Name != "DetailsScroll")
            throw new InvalidOperationException("A short monitor must clamp the panel with one fallback scroll area.");
        scroll.AutoScrollPosition = new Point(0, scroll.DisplayRectangle.Height);
        using var small = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(small, form.ClientRectangle);
        small.Save(Path.Combine(directory, $"{state}-small-work-area.png"), ImageFormat.Png);
        panel.FitToWorkArea(new Rectangle(0, 0, 1920, 1200));
        File.WriteAllText(Path.Combine(directory, $"{state}-diagnostics-check.json"), JsonSerializer.Serialize(new
        {
            visible_scroll_areas = scrolling.Length, expanded_height = expandedHeight, collapsed_height = collapsedHeight,
            short_monitor_fallback_scroll_areas = fallback.Length, no_nested_scrollbars = true,
            check_enabled = checkEnabled, disabled_clicks = clicks, diagnostics_toggles = 4
        }));
    }
}
