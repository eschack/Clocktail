using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

/// <summary>Stable native rows. Timestamp changes update cell text without reconstructing controls or selection.</summary>
internal sealed class AppliedSettingsGrid : DataGridView
{
    private readonly Dictionary<string, DataGridViewRow> _rows = new(StringComparer.Ordinal);
    private readonly Palette _palette;
    private AppliedSettings? _snapshot;
    private bool _savedKnown = true;
    public AppliedSettingsGrid(Palette palette)
    {
        _palette = palette;
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
        DefaultCellStyle = new() { BackColor = palette.Surface, ForeColor = palette.Ink, SelectionBackColor = palette.AccentSurface,
            SelectionForeColor = palette.Ink, WrapMode = DataGridViewTriState.True, Padding = new Padding(4, 7, 4, 7) };
        foreach (var (label, width) in new[] { ("Control", 34f), ("Applied (readback)", 38f), ("Saved daily", 28f) })
            Columns.Add(new DataGridViewTextBoxColumn { HeaderText = label, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowDetails(); };
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { ShowDetails(); e.Handled = true; } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Setting details", null, (_, _) => ShowDetails());
        menu.Items.Add("Copy setting details", null, (_, _) => { if (CurrentRow?.Tag is AppliedRow row) Clipboard.SetText(Details(row)); });
        ContextMenuStrip = menu;
        RowsAdded += (_, _) => FitHeight(); RowsRemoved += (_, _) => FitHeight(); RowHeightChanged += (_, _) => FitHeight();
    }
    private void FitHeight() => Height = Math.Max(48, ColumnHeadersHeight + Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 4);
    public void UpdateRows(AppliedSettings snapshot, bool savedKnown = true)
    {
        _savedKnown = savedKnown;
        if (_snapshot?.GpuId != snapshot.GpuId) { Rows.Clear(); _rows.Clear(); }
        _snapshot = snapshot;
        string? selected = (CurrentRow?.Tag as AppliedRow)?.Id; int column = CurrentCell?.ColumnIndex ?? 0;
        var wanted = snapshot.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        SuspendLayout();
        foreach (var id in _rows.Keys.Where(id => !wanted.Contains(id)).ToArray()) { Rows.Remove(_rows[id]); _rows.Remove(id); }
        for (int i = 0; i < snapshot.Rows.Count; i++)
        {
            var value = snapshot.Rows[i];
            if (!_rows.TryGetValue(value.Id, out var row))
            {
                row = new DataGridViewRow(); row.CreateCells(this); Rows.Insert(i, row); _rows[value.Id] = row;
            }
            else if (row.Index != i) { Rows.Remove(row); Rows.Insert(i, row); }
            row.Tag = value;
            string group = i == 0 || snapshot.Rows[i - 1].Group != value.Group ? value.Group + "\n" : "";
            string current = value.CurrentValue == null && value.LastValue != null
                ? "Unavailable · last observed\n" + AppliedCatalog.Format(value.Type, value.LastValue) : AppliedCatalog.Format(value.Type, value.CurrentValue);
            string Brief(string text, int length = 180) => text.Length <= length ? text : text[..(length - 20)] + "… (open details)";
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
            if (!value.DailyEligible) state += " · Trial only";
            string[] cells = [group + Brief(value.Label, 96), current + "\n" + state + " · " + Presentation.Age(value.SampledAt, "").Trim(),
                !_savedKnown ? value.Saved ? "Unknown · last saved\n" + Brief(AppliedCatalog.Format(value.Type, value.SavedValue)) : "Unknown · saved record unavailable"
                : value.Saved ? Brief(AppliedCatalog.Format(value.Type, value.SavedValue)) + (value.Suspended ? "\nDaily suspended" : "")
                : value.DailyEligible ? value.Kind == "vf" && value.Context?.Contains("follows saved core") == true ? "Not explicitly saved\nFollows saved core" : "Not saved" : "Not saveable as daily\nTrial only"];
            for (int c = 0; c < cells.Length; c++) if (!Equals(row.Cells[c].Value, cells[c])) row.Cells[c].Value = cells[c];
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = Details(value);
        }
        if (selected != null && _rows.TryGetValue(selected, out var selectedRow)) CurrentCell = selectedRow.Cells[column];
        else if (!ContainsFocus) ClearSelection();
        ResumeLayout(true); FitHeight();
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
    protected override void Dispose(bool disposing) { if (disposing) ContextMenuStrip?.Dispose(); base.Dispose(disposing); }
}
