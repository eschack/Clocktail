namespace Clocktail.Domain.Broker;

public enum StatusFetchKind { Cached, Light, Full }
public interface ITrayStatusSource
{
    Task<TrayStatus?> Fetch(StatusFetchKind kind, string? gpuId, CancellationToken token);
    string? CompatibilityNotice => null;
}

/// <summary>One request at a time. The desktop timer merely wakes this monotonic, headless-testable policy.</summary>
public sealed class TrayRefreshController(ITrayStatusSource source, TimeProvider? time = null) : IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _clock = time ?? TimeProvider.System;
    private readonly CancellationTokenSource _stop = new();
    private long _due, _inventoryDue;
    private int _backoff = 15, _selection, _manualSerial;
    private bool _quiet, _locked, _disposed, _manual, _discover = true, _continuity, _catchUp;
    public TrayStatusCache Cache { get; } = new();
    public string? GpuId { get; private set; }
    public bool Busy { get; private set; }
    public bool Quiet => _quiet;
    public bool Locked => _locked;
    public bool Legacy => Cache.Snapshot?.Version == 1;
    public string? Notice => source.CompatibilityNotice ?? (Legacy ? "Older broker: automatic refresh unavailable; Check now uses manual full verification." : null);
    public StatusDevice? Device => Cache.Snapshot?.Devices.FirstOrDefault(d => d.StableId == GpuId);
    public event Action? Changed;
    public TimeSpan? Delay
    {
        get
        {
            if (_disposed || _quiet || _locked || Busy || Legacy && !_manual) return null;
            return _clock.GetTimestamp() >= _due ? TimeSpan.Zero : _clock.GetElapsedTime(_clock.GetTimestamp(), _due);
        }
    }
    public void CheckNow() { if (_disposed || _quiet || _locked) return; _manual = true; _manualSerial++; DueNow(); }
    public void Select(string id)
    {
        if (id == GpuId) return;
        GpuId = id; _selection++;
        if (Legacy) { Changed?.Invoke(); return; }
        _discover = true; _continuity = true;
        Cache.Disconnect("Refreshing selected GPU"); DueNow(); Changed?.Invoke();
    }
    public void SetQuiet(bool value)
    {
        if (_quiet == value || _disposed) return;
        _quiet = value;
        if (!value) { _catchUp = true; DueNow(); }
        Changed?.Invoke();
    }
    public void SetLocked(bool value)
    {
        if (_locked == value || _disposed) return;
        _locked = value;
        if (!value) { _continuity = true; _catchUp = true; DueNow(); }
        Changed?.Invoke();
    }
    public void Resume() { if (_disposed) return; _continuity = true; _catchUp = true; DueNow(); }
    private void DueNow() => _due = _clock.GetTimestamp();
    private void Schedule(TimeSpan delay) => _due = _clock.GetTimestamp() + (long)(delay.TotalSeconds * _clock.TimestampFrequency);
    private bool Paused => _disposed || _quiet || _locked;
    private bool Permitted(StatusDevice? d) => d is { OperationInProgress: false, Errors.Count: 0, Refresh: { Settled: true, MeasurementBusy: false } r }
        && (r.Restoration.Startup == RestorationState.Ready || _manual && r.Restoration.Startup == RestorationState.Failed)
        && r.Restoration.Recovery != RestorationState.Pending && (_manual || r.Restoration.Recovery != RestorationState.Failed);
    private bool NeedsFull(StatusDevice? d) => _manual || _continuity || d?.Verification == null || d.Refresh is { } r &&
        (r.VerifiedGeneration != r.Generation || r.FullRequiredReason is "Full curve verification required" or "Controls changed outside Clocktail; full verification required" or "State changed during readback");

    public async Task Tick()
    {
        if (Paused || Busy || Delay is null || Delay > TimeSpan.Zero) return;
        Busy = true;
        int selection = _selection;
        int manual = _manualSerial;
        bool discovered = false;
        try
        {
            if (_discover || !Cache.Connected || Cache.Snapshot == null || _clock.GetTimestamp() >= _inventoryDue)
            {
                if (!await Fetch(StatusFetchKind.Cached, selection)) return;
                _discover = false;
                discovered = true;
                _inventoryDue = _clock.GetTimestamp() + 60 * _clock.TimestampFrequency;
                if (GpuId == null) _continuity = !Legacy;
                if (GpuId == null || !Cache.Snapshot!.Devices.Any(d => d.StableId == GpuId)) GpuId = Cache.Snapshot!.Devices.FirstOrDefault()?.StableId;
            }
            if (Paused || selection != _selection) return;
            if (GpuId == null)
            {
                if (Legacy) _manual = false;
                else
                {
                    _discover = true;
                    Schedule(TimeSpan.FromSeconds(_backoff)); _backoff = Math.Min(60, _backoff * 2);
                }
                return;
            }
            if (Legacy)
            {
                if (_manual) { _manual = false; await Fetch(StatusFetchKind.Full, selection); }
                return;
            }
            // A guard previously observed as busy is checked with records only; no hardware probing during a benchmark.
            if (!Permitted(Device))
            {
                if (!discovered && !await Fetch(StatusFetchKind.Cached, selection) || Paused || selection != _selection) return;
                if (!Permitted(Device)) { Schedule(TimeSpan.FromSeconds(_backoff)); _backoff = Math.Min(60, _backoff * 2); return; }
            }
            if (!NeedsFull(Device) || _catchUp)
            {
                _catchUp = false;
                if (!await Fetch(StatusFetchKind.Light, selection) || Paused || selection != _selection) return;
            }
            if (!Paused && selection == _selection && Permitted(Device) && NeedsFull(Device))
            {
                _manual = false; _continuity = false;
                if (!await Fetch(StatusFetchKind.Full, selection)) return;
            }
            _backoff = 15;
            Schedule(RefreshEvery);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            if (!_disposed) { Cache.Disconnect(e.Message); _discover = true; Schedule(TimeSpan.FromSeconds(_backoff)); _backoff = Math.Min(60, _backoff * 2); }
        }
        finally
        {
            Busy = false;
            if (!_disposed)
            {
                if (selection != _selection || manual != _manualSerial) DueNow();
                Changed?.Invoke();
            }
        }
    }
    private async Task<bool> Fetch(StatusFetchKind kind, int selection)
    {
        if (Paused || selection != _selection) return false;
        var response = await source.Fetch(kind, kind == StatusFetchKind.Cached ? null : GpuId, _stop.Token);
        if (_disposed || selection != _selection) return false;
        if (response == null)
        {
            Cache.Disconnect("The broker is not answering; reconnect will use bounded backoff.");
            _discover = true; Schedule(TimeSpan.FromSeconds(_backoff)); _backoff = Math.Min(60, _backoff * 2);
            return false;
        }
        var old = Cache.Snapshot;
        if (kind != StatusFetchKind.Cached && old != null && old.InstanceId == response.InstanceId)
            response = response with { Devices = old.Devices.Select(d => response.Devices.FirstOrDefault(n => n.StableId == d.StableId) ?? d).ToArray() };
        if (!Cache.Accept(response)) throw new InvalidDataException(Cache.Error ?? "An out-of-order status reply was discarded.");
        Cache.CompatibilityNotice = Notice;
        if (old != null && old.InstanceId != response.InstanceId) { _continuity = true; _discover = true; }
        // Publish invalidation before a potentially slow follow-up full read, so old green evidence disappears now.
        Changed?.Invoke();
        return true;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); _stop.Dispose(); }
}
