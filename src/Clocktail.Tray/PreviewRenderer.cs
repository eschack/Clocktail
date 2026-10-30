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
        if (state == "matched") VerifyRoutineRefresh(directory);
        var cache = new TrayStatusCache();
        var source = new DemoSource(state);
        if (source.Create() is { } snapshot && !cache.Accept(snapshot)) throw new InvalidOperationException(cache.Error);
        else if (cache.Snapshot == null) cache.Disconnect("Synthetic disconnected preview");
        cache.CompatibilityNotice = source.CompatibilityNotice;
        var expected = state switch
        {
            "measurement" or "measurement-empty" => TrayState.MeasurementPaused,
            "startup" => TrayState.StartupPending, "recovery" => TrayState.Recovering,
            "changed" or "different" => TrayState.SettingsDiffer, "trial" or "trial-controls" or "many-settings" => TrayState.TrialActive,
            "empty" or "partial" or "unavailable-saved" => TrayState.Unknown,
            "unknown-profile" => TrayState.Failed,
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
            Application.DoEvents(); // complete native grid handle/row sizing before interaction invariants are measured
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
            if (view == "details" && state is "measurement" or "measurement-empty")
            {
                var grid = form.Controls.Find("AppliedSettingsGrid", true).OfType<AppliedSettingsGrid>().Single();
                string expectedValue = state == "measurement-empty" ? "No prior readback" : "Last known · unconfirmed";
                if (grid.Rows.Cast<DataGridViewRow>().Any(r => !r.Cells[1].Value!.ToString()!.Contains(expectedValue))
                    || form.Controls.Find("CheckNowButton", true).OfType<Button>().Single().Enabled
                    || !grid.Rows.Cast<DataGridViewRow>().Single(r => r.Tag is AppliedRow { Kind: "pstates20", Key: "core" }).Cells[2].Value!.ToString()!.Contains("240"))
                    throw new InvalidOperationException("Measurement presentation lost prior evidence/saved targets or enabled verification.");
            }
            if (view == "details") VerifyPinToggle(form, directory, state);
            if (view == "details") VerifyCellToolTip(form, Palette.Dark, directory, state + "-dark");
            if (view == "details" && device?.Applied is { Rows.Count: > 0 }) VerifyDynamicUpdate((StatusForm)form, cache, directory, state);
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
        VerifyCellToolTip(light, Palette.Light, directory, state + "-light");
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
        var contrast = new Palette(Color.Black, Color.Black, Color.White, Color.White, Color.White, Color.Yellow, Color.FromArgb(0, 0, 96));
        using var highContrast = new StatusForm(false, contrast, offscreen: true) { Location = new Point(-32000, -32000) };
        highContrast.FitToWorkArea(new Rectangle(0, 0, 1920, 1200)); highContrast.Present(cache, "demo-gpu", state == "quiet", true, false);
        highContrast.Show(); Application.DoEvents(); highContrast.PerformLayout();
        using var contrastBitmap = new Bitmap(highContrast.ClientSize.Width, highContrast.ClientSize.Height);
        highContrast.DrawToBitmap(contrastBitmap, highContrast.ClientRectangle); contrastBitmap.Save(Path.Combine(directory, $"{state}-high-contrast.png"), ImageFormat.Png);
        highContrast.Hide();
    }
    private static void VerifyRoutineRefresh(string directory)
    {
        foreach (bool pinned in new[] { false, true })
        foreach (bool curve in new[] { false, true })
        {
            var cache = new TrayStatusCache(); cache.Accept(new DemoSource("matched").Create()!);
            using var form = new StatusForm(false, Palette.Dark, offscreen: true)
                { DiagnosticsExpanded = true, CurveSelected = curve, Location = new Point(-32000, -32000) };
            form.FitToWorkArea(new Rectangle(0, 0, 800, 600));
            form.Present(cache, "demo-gpu", false, true, false); form.Show(); Application.DoEvents();
            if (pinned) form.Controls.Find("PinButton", true).OfType<Button>().Single().PerformClick();
            var grid = form.Controls.Find("AppliedSettingsGrid", true).OfType<AppliedSettingsGrid>().Single();
            grid.CurrentCell = grid.Rows[0].Cells[1];
            var selected = grid.CurrentCell;
            var scroll = (Panel)form.Controls.Find("DetailsScroll", true).Single();
            scroll.AutoScrollPosition = new Point(0, 40);
            var position = scroll.AutoScrollPosition; var bounds = form.Bounds;
            var controls = Descendants(form).ToArray();
            var handles = controls.Where(c => c.IsHandleCreated).ToDictionary(c => c, c => c.Handle);
            foreach (int age in new[] { 9, 10, 99, 100, 9, 10, 99, 100 })
            {
                var snapshot = cache.Snapshot!; var d = snapshot.Devices[0]; long generation = d.Refresh!.Generation + 1;
                bool measurement = age == 99;
                var next = d with
                {
                    Telemetry = d.Telemetry! with { TempC = age, Watts = age + 20, At = DateTimeOffset.UtcNow.AddSeconds(-age) },
                    Refresh = d.Refresh with { Generation = generation, VerifiedGeneration = generation, MeasurementBusy = measurement },
                    Applied = d.Applied! with { Generation = generation, Rows = d.Applied.Rows.Select((r, i) => r with
                        { SampleGeneration = generation, SampledAt = DateTimeOffset.UtcNow.AddSeconds(-age), CurrentValue = i == 0 ? (age * 1000).ToString() : r.CurrentValue }).ToArray() }
                };
                if (!cache.Accept(snapshot with { Revision = snapshot.Revision + 1, CapturedAt = DateTimeOffset.UtcNow, Devices = [next] })) throw new InvalidOperationException(cache.Error);
                form.Present(cache, "demo-gpu", false, true, false); Application.DoEvents();
                if (!controls.SequenceEqual(Descendants(form)) || handles.Any(p => p.Key.IsDisposed || p.Key.Handle != p.Value)
                    || form.Bounds != bounds || scroll.AutoScrollPosition != position || grid.CurrentCell != selected || form.CurveSelected != curve || !form.DiagnosticsExpanded)
                    throw new InvalidOperationException("Routine telemetry/value/measurement refresh replaced controls or moved the pinned/unpinned details view.");
                var check = form.Controls.Find("CheckNowButton", true).OfType<Button>().Single();
                if (check.Enabled == measurement) throw new InvalidOperationException("Measurement did not gate Check now.");
                if (!SystemInformation.HighContrast && grid.DefaultCellStyle.SelectionBackColor != Palette.Dark.Surface)
                    throw new InvalidOperationException("Read-only selection uses a success color.");
            }
            // Inventory changes retain the existing form and controls; the outer panel handles real growth.
            var beforeRows = cache.Snapshot!; var original = beforeRows.Devices[0];
            var extra = original.Applied!.Rows[0] with { Id = "synthetic-extra", Label = new string('W', 220) };
            foreach (bool add in new[] { true, false })
            {
                var changed = original with { Applied = original.Applied with { Rows = add ? [.. original.Applied.Rows, extra] : original.Applied.Rows } };
                cache.Accept(beforeRows with { Revision = cache.Snapshot!.Revision + 1, Devices = [changed] });
                form.Present(cache, "demo-gpu", false, true, false); Application.DoEvents();
                if (form.Bounds != bounds || !controls.SequenceEqual(Descendants(form)) || grid.CurrentCell != selected)
                    throw new InvalidOperationException("Row growth/removal replaced controls or moved the window.");
            }
            // Exercise layout at an enlarged control scale without changing the user's Windows DPI settings.
            form.Scale(new SizeF(1.5f, 1.5f)); form.FitToWorkArea(new Rectangle(0, 0, 1200, 900)); Application.DoEvents();
            var scaledBounds = form.Bounds;
            form.Present(cache, "demo-gpu", false, true, false); Application.DoEvents();
            if (form.Bounds != scaledBounds || form.Height > 900) throw new InvalidOperationException("Scaled routine refresh changed/clipped monitor bounds.");
            typeof(Form).GetMethod("OnDeactivate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, [EventArgs.Empty]);
            if (form.Visible != pinned) throw new InvalidOperationException("Pinned/unpinned deactivation behavior changed.");
        }
        File.WriteAllText(Path.Combine(directory, "routine-refresh.json"), JsonSerializer.Serialize(new
        { pinned_and_unpinned = true, both_tabs = true, diagnostics = true, small_monitor = true, updates = 32, controls_handles_bounds_scroll_selection_preserved = true, deactivation_checked = true, row_growth_removal = true, enlarged_scale = true }));
    }
    private static void VerifyDynamicUpdate(StatusForm form, TrayStatusCache cache, string directory, string state)
    {
        var grid = form.Controls.Find("AppliedSettingsGrid", true).OfType<DataGridView>().Single();
        var tooltip = ((AppliedSettingsGrid)grid).CellDetailToolTip;
        grid.CurrentCell = grid.Rows[^1].Cells[1];
        var selected = grid.CurrentCell; var rowObjects = grid.Rows.Cast<DataGridViewRow>().ToArray();
        var controls = Descendants(form).ToArray();
        var handles = controls.Where(c => c.IsHandleCreated).ToDictionary(c => c, c => c.Handle);
        var scroll = (Panel)form.Controls.Find("DetailsScroll", true).Single();
        if (scroll.AutoScroll) scroll.AutoScrollPosition = new Point(0, scroll.DisplayRectangle.Height);
        var position = scroll.AutoScrollPosition;
        var snapshot = cache.Snapshot!; var device = snapshot.Devices[0];
        var next = device with { Applied = device.Applied! with { Rows = device.Applied.Rows.Select(r => r.ReadClass == AppliedReadClass.Light ? r with { SampledAt = DateTimeOffset.UtcNow.AddSeconds(-2), AttemptAt = DateTimeOffset.UtcNow.AddSeconds(-2) } : r).ToArray() } };
        if (!cache.Accept(snapshot with { Revision = snapshot.Revision + 1, Devices = [next] })) throw new InvalidOperationException("Age fixture rejected");
        form.UpdateAges(cache, "demo-gpu"); Application.DoEvents();
        if (!controls.SequenceEqual(Descendants(form)) || handles.Any(p => p.Key.IsDisposed || p.Key.Handle != p.Value)
            || grid.CurrentCell != selected || !rowObjects.SequenceEqual(grid.Rows.Cast<DataGridViewRow>()) || scroll.AutoScrollPosition != position
            || !ReferenceEquals(tooltip, ((AppliedSettingsGrid)grid).CellDetailToolTip))
            throw new InvalidOperationException("Age-only update changed native controls, row identity, selection or scroll.");
        form.Present(cache, "demo-gpu", state == "quiet", true, false); Application.DoEvents();
        if (!ReferenceEquals(grid, form.Controls.Find("AppliedSettingsGrid", true).Single()) || !rowObjects.SequenceEqual(grid.Rows.Cast<DataGridViewRow>())
            || grid.CurrentCell != selected || scroll.AutoScrollPosition != position || !ReferenceEquals(tooltip, ((AppliedSettingsGrid)grid).CellDetailToolTip))
            throw new InvalidOperationException("Data refresh discarded stable grid rows, selection or scroll.");
        File.WriteAllText(Path.Combine(directory, $"{state}-dynamic-update.json"), JsonSerializer.Serialize(new
        { age_only_tree_and_handles_preserved = true, stable_native_rows = true, selection_preserved = true, scroll_preserved = true, rows = grid.RowCount }));
    }
    private static void VerifyCellToolTip(Form form, Palette palette, string directory, string name)
    {
        var grid = form.Controls.Find("AppliedSettingsGrid", true).OfType<AppliedSettingsGrid>().FirstOrDefault();
        if (grid == null) return;
        if (grid.ShowCellToolTips || !grid.CellDetailToolTip.OwnerDraw) throw new InvalidOperationException("Applied settings reverted to unthemed native cell tooltips.");
        string? detail = null;
        if (grid.RowCount > 0)
        {
            var enter = typeof(DataGridView).GetMethod("OnCellMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var leave = typeof(DataGridView).GetMethod("OnCellMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            enter.Invoke(grid, [new DataGridViewCellEventArgs(0, 0)]);
            detail = grid.CellDetailToolTip.GetToolTip(grid);
            if (string.IsNullOrEmpty(detail) || !detail.Contains("Identity: ") || !detail.Contains("Reference canonical: ") || !detail.Contains("Coverage:"))
                throw new InvalidOperationException("Tooltip lost its existing canonical/provenance payload.");
            leave.Invoke(grid, [new DataGridViewCellEventArgs(0, 0)]);
            if (!string.IsNullOrEmpty(grid.CellDetailToolTip.GetToolTip(grid))) throw new InvalidOperationException("Tooltip survives leaving its cell.");
        }
        foreach (bool highContrast in new[] { false, true })
        {
            var expected = highContrast ? (Surface: SystemColors.Info, Ink: SystemColors.InfoText, Rule: SystemColors.WindowText)
                : (Surface: palette.Secondary, Ink: palette.Ink, Rule: palette.Rule);
            if (grid.CellToolTipColors(highContrast) != expected) throw new InvalidOperationException("Tooltip text/surface/border colors ignore the selected palette or Windows high contrast.");
            using var sample = new Bitmap(48, 32);
            using (var graphics = Graphics.FromImage(sample)) grid.PaintCellToolTip(graphics, new Rectangle(Point.Empty, sample.Size), "", grid.Font, highContrast);
            if (sample.GetPixel(4, 4).ToArgb() != expected.Surface.ToArgb() || sample.GetPixel(0, 0).ToArgb() != expected.Rule.ToArgb())
                throw new InvalidOperationException("Tooltip draw path did not paint its themed surface and border.");
        }
        if (detail != null)
        {
            var size = TextRenderer.MeasureText(detail, grid.Font, new Size(680, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            using var painted = new Bitmap(size.Width + 4, size.Height + 4);
            using (var graphics = Graphics.FromImage(painted)) grid.PaintCellToolTip(graphics, new Rectangle(Point.Empty, painted.Size), detail, grid.Font, false);
            painted.Save(Path.Combine(directory, $"{name}-tooltip-paint.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(directory, $"{name}-tooltip-check.json"), JsonSerializer.Serialize(new
        { native_cell_tooltips_disabled = true, palette_draw_path_checked = true, windows_high_contrast_colors_checked = true, payload_and_cell_exit_checked = detail != null }));
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
        if (scrolling.Length > 1 || scrolling.Any(c => c.Name != "DetailsScroll")) throw new InvalidOperationException("Expanded diagnostics introduced nested scrollbars.");
        if (scrolling.Length > 0 && form.Height < 1180) throw new InvalidOperationException("Content should fit in a tall work area or clamp to its one fallback scroll area.");
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
                if (form.Height <= collapsedHeight && form.Height < 1180) throw new InvalidOperationException("Opening diagnostics did not expand the panel.");
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
