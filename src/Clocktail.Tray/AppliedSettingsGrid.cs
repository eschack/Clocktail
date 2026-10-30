using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

/// <summary>Stable native rows. Timestamp changes update cell text without reconstructing controls or selection.</summary>
internal sealed class AppliedSettingsGrid : DataGridView
{
    private readonly Dictionary<string, DataGridViewRow> _rows = new(StringComparer.Ordinal);
    private readonly Palette _palette;
    private readonly ToolTip _cellTip = new() { OwnerDraw = true, IsBalloon = false };
    private string? _tipRowId;
    private bool _disposing;
    private bool _updating;
    private AppliedSettings? _snapshot;
    private bool _savedKnown = true;
    public AppliedSettingsGrid(Palette palette)
    {
        _palette = palette;
        // DataGridView's own tooltip window does not inherit the cell palette.
        ShowCellToolTips = false;
        _cellTip.BackColor = palette.Secondary; _cellTip.ForeColor = palette.Ink;
        _cellTip.Draw += (_, e) => PaintCellToolTip(e.Graphics, e.Bounds, e.ToolTipText ?? "", e.Font ?? Font, SystemInformation.HighContrast);
        CellMouseEnter += (_, e) =>
        {
            if (_disposing) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || Rows[e.RowIndex].Tag is not AppliedRow row) { ClearCellToolTip(); return; }
            _tipRowId = row.Id;
            _cellTip.SetToolTip(this, Details(row)); // Native hover timing; no extra polling or focusable window.
        };
        CellMouseLeave += (_, _) => ClearCellToolTip();
        MouseLeave += (_, _) => ClearCellToolTip();
        VisibleChanged += (_, _) => { if (!Visible) ClearCellToolTip(); };
        Name = "AppliedSettingsGrid"; AccessibleName = "Saved and currently modified settings";
        AccessibleDescription = "Read-only controls, current readback and saved daily targets. Control Enter opens source, age, reference and canonical details.";
        Dock = DockStyle.Top; ReadOnly = true; AllowUserToAddRows = false; AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false; AllowUserToOrderColumns = false; RowHeadersVisible = false;
        AutoGenerateColumns = false; MultiSelect = false; SelectionMode = DataGridViewSelectionMode.CellSelect;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        ScrollBars = ScrollBars.None; BorderStyle = BorderStyle.None; BackgroundColor = palette.Surface;
        GridColor = palette.Rule; CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        EnableHeadersVisualStyles = false; ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        ColumnHeadersDefaultCellStyle = new() { BackColor = palette.Surface, ForeColor = palette.Muted, Padding = new Padding(4, 7, 4, 7) };
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        DefaultCellStyle = new() { BackColor = palette.Surface, ForeColor = palette.Ink, SelectionBackColor = SystemInformation.HighContrast ? SystemColors.Highlight : palette.Surface,
            SelectionForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : palette.Ink, WrapMode = DataGridViewTriState.True, Padding = new Padding(4, 7, 4, 7) };
        foreach (var (label, width) in new[] { ("Control", 34f), ("Applied (readback)", 38f), ("Saved daily", 28f) })
            Columns.Add(new DataGridViewTextBoxColumn { HeaderText = label, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowDetails(); };
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { ShowDetails(); e.Handled = true; } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Setting details", null, (_, _) => ShowDetails());
        menu.Items.Add("Copy setting details", null, (_, _) => { if (CurrentRow?.Tag is AppliedRow row) Clipboard.SetText(Details(row)); });
        ContextMenuStrip = menu;
        CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0) CurrentCell = Rows[e.RowIndex].Cells[e.ColumnIndex]; };
        RowsAdded += (_, _) => FitHeight(); RowsRemoved += (_, _) => FitHeight(); RowHeightChanged += (_, _) => FitHeight();
    }
    private void FitHeight() { if (!_updating) Height = Math.Max(48, ColumnHeadersHeight + Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 4); }
    public void UpdateRows(AppliedSettings snapshot, bool savedKnown = true, bool measurementBusy = false)
    {
        _updating = true;
        try
        {
        _savedKnown = savedKnown;
        if (_snapshot?.GpuId != snapshot.GpuId) { ClearCellToolTip(); Rows.Clear(); _rows.Clear(); }
        _snapshot = snapshot;
        string? selected = (CurrentRow?.Tag as AppliedRow)?.Id; int column = CurrentCell?.ColumnIndex ?? 0;
        var wanted = snapshot.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (_tipRowId != null && !wanted.Contains(_tipRowId)) ClearCellToolTip();
        SuspendLayout();
        foreach (var id in _rows.Keys.Where(id => !wanted.Contains(id)).ToArray()) { Rows.Remove(_rows[id]); _rows.Remove(id); }
        for (int i = 0; i < snapshot.Rows.Count; i++)
        {
            var value = snapshot.Rows[i];
            if (!_rows.TryGetValue(value.Id, out var row))
            {
                row = new DataGridViewRow(); row.CreateCells(this); Rows.Insert(i, row); row = Rows[i]; _rows[value.Id] = row;
            }
            else if (row.Index != i) { Rows.Remove(row); Rows.Insert(i, row); row = Rows[i]; _rows[value.Id] = row; }
            row.Tag = value;
            string group = i == 0 || snapshot.Rows[i - 1].Group != value.Group ? value.Group + "\n" : "";
            string current = value.CurrentValue == null && value.LastValue != null
                ? "Unavailable · last observed\n" + AppliedCatalog.Format(value.Type, value.LastValue) : AppliedCatalog.Format(value.Type, value.CurrentValue);
            string Brief(string text, int length = 180) => text.Length <= length ? text : text[..(length - 20)] + "… (open details)";
            if (measurementBusy) current = (value.CurrentValue ?? value.LastValue) is { } last
                ? "Last known · unconfirmed\n" + AppliedCatalog.Format(value.Type, last) : "No prior readback";
            current = Brief(current);
            string state = value.Modification switch
            {
                AppliedModification.Modified => value.Saved ? "Modified" : "Unsaved change",
                AppliedModification.Pending => "Pending readback", AppliedModification.Unavailable => "Unavailable",
                AppliedModification.Unconfirmed => "Modification unconfirmed", _ => "At reference"
            };
            if (value.Saved) state = value.Daily switch { DailyComparison.Matched => "Matches daily", DailyComparison.Different => "Differs from daily", _ => "Daily comparison unknown" };
            if (value.Saved && value.Modification is AppliedModification.Pending or AppliedModification.Unconfirmed)
                state += value.Modification == AppliedModification.Pending ? " · Pending readback" : " · Modification unconfirmed";
            if (measurementBusy) state = "Verification paused";
            if (!value.DailyEligible) state += " · Trial only";
            string[] cells = [group + Brief(value.Label, 96), current + "\n" + state + " · " + Presentation.Age(value.SampledAt, "").Trim(),
                !_savedKnown ? value.Saved ? "Unknown · last saved\n" + Brief(AppliedCatalog.Format(value.Type, value.SavedValue)) : "Unknown · saved record unavailable"
                : value.Saved ? Brief(AppliedCatalog.Format(value.Type, value.SavedValue)) + (value.Suspended ? "\nDaily suspended" : "")
                : value.DailyEligible ? value.Kind == "vf" && value.Context?.Contains("follows saved core") == true ? "Not explicitly saved\nFollows saved core" : "Not saved" : "Not saveable as daily\nTrial only"];
            for (int c = 0; c < cells.Length; c++) if (!Equals(row.Cells[c].Value, cells[c])) row.Cells[c].Value = cells[c];
        }
        if (selected != null && _rows.TryGetValue(selected, out var selectedRow)) CurrentCell = selectedRow.Cells[column];
        else if (!ContainsFocus) ClearSelection();
        }
        finally { ResumeLayout(true); _updating = false; FitHeight(); }
    }
    internal ToolTip CellDetailToolTip => _cellTip;
    private void ClearCellToolTip()
    {
        if (_disposing || _tipRowId == null) return;
        _tipRowId = null;
        _cellTip.Hide(this); _cellTip.SetToolTip(this, null);
    }
    internal (Color Surface, Color Ink, Color Rule) CellToolTipColors(bool highContrast) => highContrast
        ? (SystemColors.Info, SystemColors.InfoText, SystemColors.WindowText)
        : (_palette.Secondary, _palette.Ink, _palette.Rule);
    internal void PaintCellToolTip(Graphics graphics, Rectangle bounds, string text, Font font, bool highContrast)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;
        var colors = CellToolTipColors(highContrast);
        using var surface = new SolidBrush(colors.Surface);
        using var border = new Pen(colors.Rule);
        graphics.FillRectangle(surface, bounds);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var content = Rectangle.Inflate(bounds, -2, -2);
        TextRenderer.DrawText(graphics, text, font, content, colors.Ink,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
    private string Details(AppliedRow row) => $"{row.Label}\r\nIdentity: {row.Id}\r\nApplied: {AppliedCatalog.Format(row.Type, row.CurrentValue)}\r\nCanonical: {row.CurrentValue ?? "unavailable"}\r\nLast observed: {row.LastValue}\r\nSample: {row.SampledAt:O}; generation {row.SampleGeneration}\r\nAttempt: {row.AttemptAt:O}\r\nSaved: {(row.Saved ? row.SavedValue ?? "unavailable" : "Not saved")}\r\nReference: {row.Reference} · {AppliedCatalog.Format(row.Type, row.ReferenceValue)}\r\nReference canonical: {row.ReferenceValue}\r\nState: {row.Modification}; daily: {row.Daily}\r\nRead class: {row.ReadClass}\r\n{row.Context}\r\n{row.Error}\r\n\r\nCoverage:\r\n{string.Join("\r\n", _snapshot?.Coverage ?? [])}";
    private void ShowDetails()
    {
        if (CurrentRow?.Tag is not AppliedRow row) return;
        using var dialog = new Form { Text = row.Label, ClientSize = new Size(640, 440), StartPosition = FormStartPosition.CenterParent,
            BackColor = _palette.Surface, ForeColor = _palette.Ink, ShowInTaskbar = false };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill,
            Text = Details(row), BackColor = _palette.Surface, ForeColor = _palette.Ink, AccessibleName = "Canonical setting and provenance details" });
        dialog.ShowDialog(FindForm());
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _disposing = true; _cellTip.Dispose(); ContextMenuStrip?.Dispose(); }
        base.Dispose(disposing);
    }
}
