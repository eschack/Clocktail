using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal sealed class StatusForm : Form
{
    private readonly bool _hover;
    private readonly bool _offscreen;
    private readonly Palette _palette;
    private readonly Panel _scroll = new BufferedPanel { Name = "DetailsScroll", Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _content = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1 };
    private bool _pinned;
    private bool _building;
    private Button? _pinButton;
    private Rectangle? _workArea;
    public event Action? CheckRequested;
    public event Action? QuietRequested;
    public event Action<string>? DeviceSelected;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CurveSelected { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DiagnosticsExpanded { get; set; }
    private readonly Dictionary<float, Font> _fonts = new();

    public StatusForm(bool hover, Palette? palette = null, bool offscreen = false)
    {
        _hover = hover;
        _offscreen = offscreen;
        _palette = palette ?? Palette.Current;
        Text = hover ? "Clocktail cached hover" : "Clocktail Tray";
        AccessibleName = Text;
        Font = new Font("Segoe UI", Math.Max(9.75f, SystemFonts.MessageBoxFont?.Size ?? 9f));
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        TopMost = true;
        BackColor = _palette.Surface;
        ForeColor = _palette.Ink;
        Padding = new Padding(1);
        ClientSize = new Size(hover ? 366 : 570, hover ? 330 : 720);
        _content.Padding = new Padding(hover ? 19 : 22, 16, hover ? 19 : 22, 16);
        _content.BackColor = _palette.Surface;
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.BackColor = _palette.Surface;
        _scroll.Controls.Add(_content);
        Controls.Add(_scroll);
        if (hover)
        {
            // Nonactivation and hit testing make this read-only. Disabling the form would gray every label.
            TabStop = false;
            _scroll.TabStop = false;
            _content.TabStop = false;
            _scroll.AutoScroll = false;
        }
        else
        {
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
            Deactivate += (_, _) => { if (!_pinned) Hide(); };
        }
    }

    protected override bool ShowWithoutActivation => _hover || _offscreen;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            if (_hover || _offscreen) cp.ExStyle |= 0x08000000 | 0x20; // NOACTIVATE, TRANSPARENT
            return cp;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if ((_hover || _offscreen) && m.Msg == 0x21) { m.Result = 3; return; } // MA_NOACTIVATE
        if (_hover && m.Msg == 0x84) { m.Result = -1; return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND, ignored on Windows versions without this attribute
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_palette.Rule);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void Present(TrayStatusCache cache, string? gpuId, bool quiet, bool demo, bool busy)
    {
        using var redraw = new ScrollLayoutUpdate(_scroll);
        _building = true;
        SuspendLayout();
        _content.SuspendLayout();
        foreach (Control old in _content.Controls.Cast<Control>().ToArray()) old.Dispose();
        _content.Controls.Clear();
        _content.RowStyles.Clear();
        _content.RowCount = 0;
        var snapshot = cache.Snapshot;
        var d = snapshot?.Devices.FirstOrDefault(g => g.StableId == gpuId) ?? snapshot?.Devices.FirstOrDefault();
        var state = TrayStatus.State(d, cache.Connected, snapshot?.CapturedAt ?? DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        var description = Presentation.Describe(state);
        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var brand = Row();
        brand.WrapContents = false;
        brand.Controls.Add(new GlyphView(Glyph.Gauge, _palette.Accent, 27) { BackColor = _palette.AccentSurface, AccessibleName = "Clocktail gauge" });
        brand.Controls.Add(Label("Clocktail", 16, false, 3));
        if (!_hover) brand.Controls.Add(Label("Tray", 11, true, 5));
        var windowControls = Row();
        windowControls.WrapContents = false;
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(windowControls, 1, 0);
        if (_hover) windowControls.Controls.Add(Label(cache.Connected ? "Last check OK" : "Unavailable", 11, true, 5));
        if (!_hover)
        {
            _pinButton = Button("", TogglePin, Glyph.Pin);
            _pinButton.Name = "PinButton";
            _pinButton.AutoSize = false;
            _pinButton.Size = new Size(32, 32);
            _pinButton.FlatAppearance.BorderSize = 0;
            UpdatePinButton();
            windowControls.Controls.Add(_pinButton);
            var close = Button("", Hide, Glyph.Close);
            close.AutoSize = false; close.Size = new Size(32, 32);
            close.AccessibleName = "Close details panel";
            close.FlatAppearance.BorderSize = 0;
            close.BackColor = _palette.Surface;
            windowControls.Controls.Add(close);
        }
        Add(header);
        Add(Label(d?.Name ?? "No GPU status", _hover ? 13 : 18, false, 12));
        if (demo) Add(Label("Synthetic preview · No GPU connection", 11, true));
        else Add(Label(cache.Connected ? "Broker answered last check · Read-only companion" : "Broker disconnected", 11, true));

        if (!_hover && snapshot is { Devices.Count: > 1 })
        {
            var select = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Selected GPU" };
            foreach (var gpu in snapshot.Devices) select.Items.Add(new DeviceChoice(gpu.StableId, gpu.Name));
            select.SelectedIndex = snapshot.Devices.ToList().FindIndex(g => g.StableId == d?.StableId);
            select.SelectedIndexChanged += (_, _) => { if (!_building && select.SelectedItem is DeviceChoice choice) DeviceSelected?.Invoke(choice.Id); };
            Add(select);
        }

        var band = Stack();
        bool matched = state == TrayState.DailyMatched;
        band.BackColor = matched ? _palette.AccentSurface : _palette.Secondary;
        band.Padding = new Padding(12);
        band.Margin = new Padding(0, 16, 0, 14);
        var title = Label(_hover ? state == TrayState.DailyMatched ? "Daily  ·  Applied" : description.Title : description.Title, _hover ? 18 : 13, false);
        if (matched) title.ForeColor = _palette.Accent;
        var statusLine = Row();
        statusLine.Controls.Add(new GlyphView(matched ? _hover ? Glyph.Check : Glyph.CheckCheck : Glyph.Attention, matched ? _palette.Accent : _palette.Muted, 20));
        var statusText = Stack();
        title.MaximumSize = new Size(_hover ? 260 : 440, 0);
        statusText.Controls.Add(title);
        if (!_hover)
        {
            var detail = Label(description.Detail, 11, true, 3);
            detail.MaximumSize = new Size(440, 0);
            statusText.Controls.Add(detail);
        }
        statusLine.Controls.Add(statusText);
        band.Controls.Add(statusLine);
        Add(band);
        if (_hover)
        {
            var offsets = Grid(3);
            foreach (var control in Presentation.Rows.Take(3))
            {
                var column = Stack();
                column.Controls.Add(Label(control.Label.Replace(" offset", ""), 11, true));
                column.Controls.Add(Label(Presentation.Applied(d, control.Kind, control.Key), 12, false, 4));
                offsets.Controls.Add(column);
            }
            Add(offsets);
            var readings = Row(); readings.Margin = new Padding(0, 12, 0, 0);
            readings.Controls.Add(new GlyphView(Glyph.Thermometer, _palette.Muted, 16));
            readings.Controls.Add(Label($"{d?.Telemetry?.TempC?.ToString("0") ?? "—"} °C GPU", 11, true));
            readings.Controls.Add(new GlyphView(Glyph.Zap, _palette.Muted, 16) { Margin = new Padding(10, 0, 4, 0) });
            readings.Controls.Add(Label($"{d?.Telemetry?.Watts?.ToString("0") ?? "—"} W board", 11, true));
            Add(readings);
            Add(Label($"Power limit: {Presentation.Applied(d, "power", "fe")}", 11, true, 3));
        }
        else
        {
            var live = Grid(3);
            string[] names = ["GPU temperature", "Board power", "GPU activity"];
            string[] values = [$"{d?.Telemetry?.TempC?.ToString("0") ?? "—"} °C", $"{d?.Telemetry?.Watts?.ToString("0") ?? "—"} W", d?.Telemetry?.UtilPercent is { } util ? $"{util}%" : "—"];
            for (int i = 0; i < 3; i++)
            {
                var col = Stack(); col.Controls.Add(Label(names[i], 11, true)); col.Controls.Add(Label(values[i], 25, false, 5)); live.Controls.Add(col);
            }
            Add(live);
            Add(Label(Presentation.Age(d?.Telemetry?.At, "Telemetry sampled"), 11, true, 3));
            var tabs = new QuietTabControl(_palette) { Dock = DockStyle.Top, Height = 264, AccessibleName = "Settings view", Margin = new Padding(0, 16, 0, 8) };
            var settings = new TabPage("Applied settings") { BackColor = _palette.Surface, ForeColor = _palette.Ink, Padding = new Padding(8), AutoScroll = false };
            var table = Grid(3); table.Dock = DockStyle.Top;
            foreach (var heading in new[] { "Control", "Applied (readback)", "Saved daily" }) table.Controls.Add(Label(heading, 11, true));
            foreach (var control in Presentation.Rows)
            {
                table.Controls.Add(Label(control.Label, 11, true, 13));
                table.Controls.Add(Label(Presentation.Applied(d, control.Kind, control.Key), 12, false, 13));
                table.Controls.Add(Label(Presentation.Saved(d, control.Kind, control.Key), 11, true, 13));
            }
            settings.Controls.Add(table);
            tabs.Height = Math.Max(264, table.GetPreferredSize(new Size(500, 0)).Height + settings.Padding.Vertical + 35);
            var plot = new TabPage("V/F curve") { BackColor = _palette.Surface, ForeColor = _palette.Ink };
            plot.Controls.Add(new CurvePanel(d?.Verification?.Curve ?? [], _palette) { Dock = DockStyle.Fill });
            tabs.TabPages.Add(settings); tabs.TabPages.Add(plot);
            tabs.SelectedIndex = CurveSelected ? 1 : 0;
            tabs.SelectedIndexChanged += (_, _) => CurveSelected = tabs.SelectedIndex == 1;
            Add(tabs);
            var session = Row(); session.Margin = new Padding(0, 6, 0, 0);
            session.Controls.Add(new GlyphView(d?.ExperimentKnown == true && d.TrialChange == null ? Glyph.CircleCheck : Glyph.Attention, _palette.Muted, 16));
            session.Controls.Add(Label(d?.ExperimentKnown != true ? "Trial status unavailable" : d.TrialChange != null ? $"Temporary trial: {d.TrialChange}" : "No temporary trial recorded", 11, true));
            Add(session);
            Add(Label(d?.SavedDailyKnown != true ? "Daily restoration status unavailable" : d.SavedDaily == null ? "No saved daily profile" : d.SavedDaily.Suspended ? "Daily restoration suspended" : "Daily restoration configured · outcome unconfirmed", 11, true, 4));
            var actions = Row(); actions.Margin = new Padding(0, 16, 0, 8);
            var check = Button(busy ? "Checking…" : "Check now", () => CheckRequested?.Invoke(), Glyph.Refresh);
            check.Name = "CheckNowButton";
            check.Enabled = !busy && !quiet;
            check.AccessibleDescription = quiet ? "Unavailable while benchmark quiet mode is enabled." : busy ? "A status check is already in progress." : "Verify the selected GPU settings.";
            actions.Controls.Add(check);
            actions.Controls.Add(Button(quiet ? "Leave quiet mode" : "Quiet for benchmark", () => QuietRequested?.Invoke(), Glyph.Moon));
            var diagnostics = Button(DiagnosticsExpanded ? "Hide diagnostics" : "Diagnostics", () => { DiagnosticsExpanded = !DiagnosticsExpanded; Present(cache, gpuId, quiet, demo, busy); }, DiagnosticsExpanded ? Glyph.ChevronUp : Glyph.ChevronDown);
            diagnostics.Name = "DiagnosticsToggleButton";
            diagnostics.TextImageRelation = TextImageRelation.TextBeforeImage;
            actions.Controls.Add(diagnostics);
            Add(actions);
            if (DiagnosticsExpanded)
            {
                Add(DiagnosticsBlock(Diagnostics(cache, d)));
            }
        }
        Add(Label(Presentation.Age(d?.Verification?.At, "Verified"), 11, true, 12));
        Add(Label(quiet ? "Quiet · checks and hover paused until you leave quiet mode" : "Cached display · no background GPU polling", 11, true, 4));
        if (cache.Error != null) Add(Label(cache.Error, 11, true, 6));
        _content.ResumeLayout(true);
        ResumeLayout(true);
        ResizeToContent(redraw);
        _building = false;
    }

    internal void FitToWorkArea(Rectangle area)
    {
        _workArea = area;
        using var redraw = new ScrollLayoutUpdate(_scroll);
        MaximumSize = area.Size;
        ResizeToContent(redraw);
    }

    private void ResizeToContent(ScrollLayoutUpdate redraw)
    {
        int oldBottom = Bottom;
        var area = _workArea ?? Screen.FromControl(this).WorkingArea;
        int margin = Math.Max(8, 8 * DeviceDpi / 96);
        // Measure at full width, without an old scrollbar narrowing and rewrapping the content.
        _scroll.AutoScroll = false;
        _scroll.AutoScrollPosition = Point.Empty;
        redraw.LayoutNow();
        int measured = _content.GetPreferredSize(new Size(_scroll.ClientSize.Width, 0)).Height + Padding.Vertical;
        int baseline = (_hover ? 280 : 720) * DeviceDpi / 96;
        int desired = Math.Max(baseline, measured);
        int height = Math.Min(desired, Math.Max(1, area.Height - margin * 2));
        ClientSize = new Size(ClientSize.Width, height);
        _scroll.AutoScroll = !_hover && measured > height;
        redraw.LayoutNow();
        if (Visible && !_offscreen)
        {
            // Keep the tray-facing bottom edge in place while diagnostics expands upward, then clamp to the monitor.
            Location = new Point(Math.Clamp(Left, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin)),
                Math.Clamp(oldBottom - Height, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - Height - margin)));
        }
    }

    public static string Diagnostics(TrayStatusCache cache, StatusDevice? d) =>
        $"Tray: {Application.ProductVersion}\r\nBroker instance: {cache.Snapshot?.InstanceId ?? "unavailable"}\r\nStatus protocol: {cache.Snapshot?.Version}\r\nGPU: {d?.StableId}\r\nDriver: {d?.Driver ?? "unavailable"}\r\nVBIOS: {d?.Vbios ?? "unavailable"}\r\nPolicy: managed by the agent / unavailable\r\nRecovery outcome: unavailable\r\n{d?.Verification?.Error}\r\n{string.Join("\r\n", d?.Errors ?? [])}\r\n{cache.Error}";

    private Label Label(string text, float px, bool muted, int top = 0) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(_hover ? 326 : 520, 0),
        Font = TextFont(px),
        ForeColor = muted ? _palette.Muted : _palette.Ink, BackColor = Color.Transparent,
        Margin = new Padding(0, top, 4, 0), AccessibleName = text
    };
    private Button Button(string text, Action click, Glyph? glyph = null)
    {
        var button = glyph is { } icon ? new GlyphButton(icon, _palette) : new Button();
        button.Text = text; button.AutoSize = true; button.FlatStyle = FlatStyle.Flat; button.BackColor = _palette.Secondary;
        button.ForeColor = _palette.Ink; button.Padding = new Padding(6, 4, 6, 4); button.Margin = new Padding(0, 0, 8, 0); button.AccessibleName = text;
        button.FlatAppearance.BorderColor = _palette.Rule;
        button.Click += (_, _) => click(); return button;
    }
    private Control DiagnosticsBlock(string text)
    {
        var box = new TextBox
        {
            Name = "DiagnosticsText", Multiline = true, ReadOnly = true, WordWrap = true,
            BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None, Dock = DockStyle.Top,
            BackColor = _palette.Secondary, ForeColor = _palette.Ink, AccessibleName = "Broker diagnostics",
            Text = text.TrimEnd(), Margin = Padding.Empty
        };
        var block = new BufferedPanel { Name = "DiagnosticsBlock", Dock = DockStyle.Top, Padding = new Padding(10),
            BackColor = _palette.Secondary, Margin = new Padding(0, 4, 0, 0) };
        block.Controls.Add(box);
        void MeasureText()
        {
            int width = Math.Max(40, block.ClientSize.Width - block.Padding.Horizontal);
            int height = TextRenderer.MeasureText(box.Text + "\r\n", box.Font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 4;
            box.Height = height;
            block.Height = height + block.Padding.Vertical;
        }
        block.SizeChanged += (_, _) => MeasureText();
        MeasureText();
        return block;
    }
    private void TogglePin()
    {
        _pinned = !_pinned;
        UpdatePinButton(); // Keep the content, focus, tab selection and scroll position intact.
    }
    private void UpdatePinButton()
    {
        if (_pinButton == null) return;
        _pinButton.AccessibleName = _pinned ? "Unpin details panel" : "Pin details panel";
        _pinButton.AccessibleDescription = _pinned ? "Pinned. Stays open when another window is active." : "Unpinned. Hides when another window is active.";
        _pinButton.BackColor = _pinned ? _palette.AccentSurface : _palette.Surface;
    }
    private static FlowLayoutPanel Row() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
    private static FlowLayoutPanel Stack() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = columns, Margin = Padding.Empty };
        for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return grid;
    }
    private void Add(Control control) { _content.RowCount++; _content.Controls.Add(control, 0, _content.RowCount - 1); }
    private Font TextFont(float px)
    {
        if (!_fonts.TryGetValue(px, out var font)) _fonts[px] = font = new Font(Font.FontFamily, Math.Max(8.5f, px * .75f) * (Font.Size / 9.75f));
        return font;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var font in _fonts.Values) font.Dispose(); }
    }
    private sealed record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
}
