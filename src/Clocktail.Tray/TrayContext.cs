using Microsoft.Win32;
using Clocktail.Domain.Broker;

namespace Clocktail.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly StatusForm _hover = new(true);
    private readonly StatusForm _details = new(false);
    private readonly System.Windows.Forms.Timer _hoverLeave = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _ageDeadline = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TrayStatusCache _cache = new();
    private readonly IStatusSource _source;
    private readonly bool _demo;
    private Rectangle _hoverAnchor;
    private string? _gpuId;
    private bool _quiet, _busy, _disposed, _locked;
    private readonly Control _ui = new();
    private Icon? _ownedIcon;
    private (TrayState, bool)? _iconState;

    public TrayContext(string? demo)
    {
        _demo = demo != null;
        _quiet = demo == "quiet";
        _source = demo == null ? new PipeStatusSource() : new DemoSource(demo);
        if (demo != null)
        {
            if (((DemoSource)_source).Create() is { } sample) _cache.Accept(sample);
            else _cache.Disconnect("Synthetic disconnected preview");
            _gpuId = Device?.StableId;
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
            _menu.Items[1].Enabled = !_busy && !_quiet && !_locked;
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
        _details.DeviceSelected += id => { _gpuId = id; Render(); };
        SystemEvents.SessionSwitch += SessionSwitch;
        Application.Idle += FirstIdle;
    }

    private StatusDevice? Device => _cache.Snapshot?.Devices.FirstOrDefault(d => d.StableId == _gpuId) ?? _cache.Snapshot?.Devices.FirstOrDefault();
    private async void FirstIdle(object? sender, EventArgs e) { Application.Idle -= FirstIdle; if (!_quiet && !_demo) await Fetch(false); else Render(); }
    private void Menu(string title, Action action) => _menu.Items.Add(title, null, (_, _) => action());

    private async Task Fetch(bool verify)
    {
        if (_disposed || _busy || _quiet || _locked) return;
        _busy = true;
        Render();
        try
        {
            // Startup/reconnect only reads files + the existing flight-recorder cache. Verification is an explicit click.
            if (_cache.Snapshot == null || !_cache.Connected)
            {
                var initial = await _source.Fetch(false, null, _lifetime.Token);
                if (_disposed) return;
                if (initial == null) { _cache.Disconnect("The broker is not answering. Check now to reconnect."); return; }
                _cache.Accept(initial);
                _gpuId = _cache.Snapshot?.Devices.FirstOrDefault()?.StableId;
            }
            if (verify && _gpuId != null)
            {
                var snapshot = await _source.Fetch(true, _gpuId, _lifetime.Token);
                if (_disposed) return;
                if (snapshot == null) _cache.Disconnect("The broker is not answering.");
                else
                {
                    _cache.Accept(snapshot);
                    // Re-read the inventory from broker files/cache, rather than re-date other GPUs' old session state.
                    var all = await _source.Fetch(false, null, _lifetime.Token);
                    if (!_disposed && all != null) _cache.Accept(all);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            if (!_disposed) _cache.Disconnect(e.Message);
        }
        finally { _busy = false; if (!_disposed) Render(); }
    }

    private void Render()
    {
        if (_disposed) return;
        if (_hover.Visible) _hover.Present(_cache, _gpuId, _quiet, _demo, _busy);
        if (_details.Visible) _details.Present(_cache, _gpuId, _quiet, _demo, _busy);
        var state = TrayStatus.State(Device, _cache.Connected, _cache.Snapshot?.CapturedAt ?? DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
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
        if (_quiet || !_cache.Connected || _cache.Snapshot == null) return;
        var deadline = _cache.Snapshot.CapturedAt + TrayStatus.FreshFor;
        if (Device?.Verification is { } v && v.At + TrayStatus.FreshFor < deadline) deadline = v.At + TrayStatus.FreshFor;
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        _ageDeadline.Interval = Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds) + 25, 50, 31000);
        _ageDeadline.Start();
    }
    private void OpenDetails()
    {
        if (_locked) return;
        HideHover();
        _details.Present(_cache, _gpuId, _quiet, _demo, _busy);
        Place(_details, Cursor.Position);
        _details.Show();
        _details.Activate(); // user clicked Open details
    }
    private void HideHover() { _hover.Hide(); _hoverLeave.Stop(); }
    private void ToggleQuiet()
    {
        _quiet = !_quiet;
        HideHover();
        if (_quiet) { _details.Hide(); _ageDeadline.Stop(); }
        Render();
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_disposed || !_ui.IsHandleCreated) return;
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            _locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect;
            if (_locked) { HideHover(); _details.Hide(); }
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
            _lifetime.Cancel();
            _icon.Visible = false;
            _icon.Dispose(); _menu.Dispose(); _hoverLeave.Dispose(); _ageDeadline.Dispose();
            _ownedIcon?.Dispose();
            _hover.Dispose(); _details.Dispose(); _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
