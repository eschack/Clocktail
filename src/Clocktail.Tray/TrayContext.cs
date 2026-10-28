using Microsoft.Win32;
using Clocktail.Domain.Broker;
using System.Text.Json;

namespace Clocktail.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly StatusForm _hover = new(true);
    private readonly StatusForm _details = new(false);
    private readonly System.Windows.Forms.Timer _hoverLeave = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _ageDeadline = new();
    private readonly TrayStatusCache _cache;
    private readonly ITrayStatusSource _source;
    private readonly TrayRefreshController _refresh;
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly bool _demo;
    private Rectangle _hoverAnchor;
    private string? _gpuId => _refresh.GpuId ?? _cache.Snapshot?.Devices.FirstOrDefault()?.StableId;
    private bool _quiet => _refresh.Quiet;
    private bool _busy => _manualCheck;
    private bool _locked => _refresh.Locked;
    private bool _disposed, _sessionLocked, _suspended, _manualCheck;
    private string? _lastPresentation;
    private readonly Control _ui = new();
    private Icon? _ownedIcon;
    private (TrayState, bool)? _iconState;

    public TrayContext(string? demo)
    {
        _demo = demo != null;
        _source = demo == null ? new BrokerTrayStatusSource() : new DemoSource(demo);
        _refresh = new(_source);
        _cache = _refresh.Cache;
        _refresh.SetQuiet(demo == "quiet");
        if (demo != null)
        {
            if (((DemoSource)_source).Create() is { } sample) _cache.Accept(sample);
            else _cache.Disconnect("Synthetic disconnected preview");
        }
        _ui.CreateControl();
        _icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Clocktail · status unverified", Visible = true, ContextMenuStrip = _menu };
        Menu("Open details", OpenDetails);
        Menu("Check now", () => _ = Fetch(true));
        Menu("Quiet for benchmark", ToggleQuiet);
        _menu.Items.Add(new ToolStripSeparator());
        Menu("Open broker log", OpenLog);
        Menu("Copy diagnostics", () => Clipboard.SetText(StatusForm.Diagnostics(_cache, Device)));
        Menu("About", () => MessageBox.Show($"Clocktail Tray {Application.ProductVersion}\nOptional read-only companion.\nLaunch does not install, register startup or apply settings.\nPolicy is managed by the agent.", "Clocktail Tray"));
        Menu("Exit tray", ExitThread);
        _menu.Opening += (_, _) =>
        {
            HideHover();
            _menu.Items[0].Enabled = !_quiet && !_locked;
            _menu.Items[1].Enabled = !_busy && !_refresh.Busy && !_quiet && !_locked;
            ((ToolStripMenuItem)_menu.Items[2]).Checked = _quiet;
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenDetails(); };
        _icon.MouseMove += (_, _) =>
        {
            if (_hover.Visible || _details.Visible || _menu.Visible || _quiet || _locked) return;
            var cursor = Cursor.Position;
            int extent = Math.Max(16, SystemInformation.SmallIconSize.Width);
            _hoverAnchor = new Rectangle(cursor.X - extent, cursor.Y - extent, extent * 2, extent * 2);
            _hover.Present(_cache, _gpuId, _quiet, _demo, _busy);
            Place(_hover, cursor);
            _hover.Show(); // WS_EX_NOACTIVATE + ShowWithoutActivation; never Activate or Focus
            _hoverLeave.Start();
            ScheduleExpiry();
        };
        _hoverLeave.Tick += (_, _) => { if (!_hoverAnchor.Contains(Cursor.Position)) HideHover(); };
        _ageDeadline.Tick += (_, _) =>
        {
            _ageDeadline.Stop();
            // One expiry transition, not a polling/repaint loop. It also removes a green stale tray accent.
            if (!_quiet) Render();
        };
        _details.VisibleChanged += (_, _) => { if (_details.Visible) ScheduleExpiry(); };
        _details.CheckRequested += () => _ = Fetch(true);
        _details.QuietRequested += ToggleQuiet;
        _details.DeviceSelected += id => _refresh.Select(id);
        _refresh.Changed += () => { if (!_disposed) { Render(); ScheduleRefresh(); } };
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await Fetch(false); };
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Application.Idle += FirstIdle;
    }

    private StatusDevice? Device => _cache.Snapshot?.Devices.FirstOrDefault(d => d.StableId == _gpuId) ?? _cache.Snapshot?.Devices.FirstOrDefault();
    private async void FirstIdle(object? sender, EventArgs e) { Application.Idle -= FirstIdle; await Fetch(false); Render(); }
    private void Menu(string title, Action action) => _menu.Items.Add(title, null, (_, _) => action());

    private async Task Fetch(bool verify)
    {
        if (_disposed) return;
        _refreshTimer.Stop();
        if (verify) { _refresh.CheckNow(); _manualCheck = true; Render(); }
        try { await _refresh.Tick(); }
        finally { if (verify) { _manualCheck = false; Render(); } }
        if (!_disposed) ScheduleRefresh();
    }
    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        if (_disposed || _refresh.Delay is not { } delay) return;
        _refreshTimer.Interval = Math.Clamp((int)Math.Ceiling(delay.TotalMilliseconds), 25, 60000);
        _refreshTimer.Start();
    }

    private void Render()
    {
        if (_disposed) return;
        var state = TrayStatus.State(Device, _cache.Connected, _cache.Snapshot?.CapturedAt ?? DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        if (_hover.Visible || _details.Visible)
        {
            var d = Device;
            string signature = JsonSerializer.Serialize(new { state, _quiet, _busy, _cache.Error, _cache.CompatibilityNotice,
                d?.StableId, d?.Name, d?.SavedDaily?.Writes, d?.SavedDaily?.Suspended, d?.TrialChange, d?.SessionGame, d?.Errors,
                devices = _cache.Snapshot?.Devices.Select(g => new { g.StableId, g.Name }),
                controls = d?.Refresh?.Controls is { } c ? new { c.Settings, c.Comparison, c.Error } : null,
                lifecycle = d?.Refresh?.Restoration,
                telemetry = new { d?.Telemetry?.TempC, d?.Telemetry?.Watts, d?.Telemetry?.UtilPercent },
                curve = d?.Verification is { } v ? new { v.Settings, v.Curve, v.Comparison, v.Error } : null,
                applied = d?.Applied is { } a ? new { a.Generation, a.Complete, a.OmittedCount, a.Coverage, rows = a.Rows.Select(r => new
                    { r.Id, r.Kind, r.Key, r.Child, r.Group, r.Label, r.Type, r.Saved, r.SavedValue, r.DailyEligible, r.Suspended, r.CurrentValue, r.LastValue, r.SampleGeneration, r.Error, r.ReferenceValue, r.Reference, r.Modification, r.Daily, r.ReadClass, r.Context }) } : null,
                diagnostics = _details.Visible && _details.DiagnosticsExpanded ? StatusForm.Diagnostics(_cache, d) : null });
            if (signature != _lastPresentation)
            {
                _lastPresentation = signature;
                if (_hover.Visible) _hover.Present(_cache, _gpuId, _quiet, _demo, _busy);
                if (_details.Visible) _details.Present(_cache, _gpuId, _quiet, _demo, _busy);
            }
            else
            {
                if (_hover.Visible) _hover.UpdateAges(_cache, _gpuId);
                if (_details.Visible) _details.UpdateAges(_cache, _gpuId);
            }
        }
        _icon.Text = "Clocktail · " + (_quiet ? "Quiet" : state.ToString());
        if (_iconState != (state, _quiet))
        {
            var old = _ownedIcon;
            _ownedIcon = AppIcon.Create(state, _quiet);
            _icon.Icon = _ownedIcon;
            _iconState = (state, _quiet);
            old?.Dispose();
        }
        ScheduleExpiry();
    }
    private void ScheduleExpiry()
    {
        _ageDeadline.Stop();
        if (_quiet || _locked || !_cache.Connected || _cache.Snapshot == null) return;
        var deadline = _cache.Snapshot.CapturedAt + TrayStatus.FreshFor;
        var evidence = Device?.Refresh is { } fresh ? fresh.Controls?.At : Device?.Verification?.At;
        if (evidence is { } at && at + TrayStatus.FreshFor < deadline) deadline = at + TrayStatus.FreshFor;
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        _ageDeadline.Interval = Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds) + 25, 50, 31000);
        _ageDeadline.Start();
    }
    private void OpenDetails()
    {
        if (_locked || _quiet) return;
        HideHover();
        _details.Present(_cache, _gpuId, _quiet, _demo, _busy);
        Place(_details, Cursor.Position);
        _details.Show();
        _details.Activate(); // user clicked Open details
    }
    private void HideHover() { _hover.Hide(); _hoverLeave.Stop(); }
    private void ToggleQuiet()
    {
        bool quiet = !_quiet;
        HideHover();
        if (quiet) { _details.Hide(); _ageDeadline.Stop(); }
        _refresh.SetQuiet(quiet);
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_disposed || !_ui.IsHandleCreated) return;
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) _sessionLocked = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect) _sessionLocked = false;
            _refresh.SetLocked(_sessionLocked || _suspended);
            if (_locked) { HideHover(); _details.Hide(); }
        });
    }
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposed || !_ui.IsHandleCreated) return;
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Mode == PowerModes.Suspend) { _suspended = true; HideHover(); _details.Hide(); }
            else if (e.Mode == PowerModes.Resume) { _suspended = false; _refresh.Resume(); }
            _refresh.SetLocked(_sessionLocked || _suspended);
            ScheduleRefresh();
        });
    }
    internal static void Place(Form form, Point anchor)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        if (form is StatusForm status) status.FitToWorkArea(area);
        else form.MaximumSize = area.Size;
        var desired = form.Size;
        int x = anchor.X + 12, y = anchor.Y - desired.Height - 12;
        form.Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - desired.Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - desired.Height)));
    }
    private void OpenLog()
    {
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", "broker.log");
        if (File.Exists(log)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(log) { UseShellExecute = true });
        else MessageBox.Show("No broker log is available for this user.", "Clocktail Tray");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Application.Idle -= FirstIdle;
            SystemEvents.SessionSwitch -= SessionSwitch;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            _refresh.Dispose(); _refreshTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose(); _menu.Dispose(); _hoverLeave.Dispose(); _ageDeadline.Dispose();
            _ownedIcon?.Dispose();
            _hover.Dispose(); _details.Dispose(); _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
