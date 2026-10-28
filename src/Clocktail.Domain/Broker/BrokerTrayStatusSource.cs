using System.Text.Json;

namespace Clocktail.Domain.Broker;

public sealed class BrokerTrayStatusSource : ITrayStatusSource
{
    private readonly Func<BrokerRequest, CancellationToken, Task<BrokerResponse?>> _call;
    private int _version = 3;
    private volatile bool _paused;
    public void SetPaused(bool paused) => _paused = paused;
    public string? CompatibilityNotice { get; private set; }
    public BrokerTrayStatusSource(Func<BrokerRequest, CancellationToken, Task<BrokerResponse?>>? call = null)
        => _call = call ?? ((request, token) => BrokerClient.TryCallAsync(request, token));
    public async Task<TrayStatus?> Fetch(StatusFetchKind kind, string? gpuId, CancellationToken token)
    {
        string op = kind switch { StatusFetchKind.Cached => "cached", StatusFetchKind.Light => "light", _ => "verify" };
        if (_version == 1 && kind == StatusFetchKind.Light) throw new InvalidOperationException("Light status is unavailable on this broker.");
        var result = await _call(new(BrokerRequest.OpStatus, GpuId: kind == StatusFetchKind.Cached ? null : gpuId, Kind: op, Value: _version.ToString()), token).ConfigureAwait(false);
        if (result == null) return null;
        if (_version > 1 && kind == StatusFetchKind.Cached && (!result.Ok && (result.Code == BrokerCode.UnknownOp || result.Code == BrokerCode.BadRequest && result.Error?.Contains("version", StringComparison.OrdinalIgnoreCase) == true)
            || result.Ok && result.Result is { } json && json.TryGetProperty("Version", out var version) && version.GetInt32() < _version))
        {
            _version--;
            CompatibilityNotice = _version == 1 ? "Older broker: automatic refresh is disabled. Check now performs manual full verification. Fixed legacy table; dynamic coverage unavailable."
                : "Older broker: fixed legacy table. Full saved/modified settings coverage requires protocol 3.";
            return await Fetch(StatusFetchKind.Cached, null, token).ConfigureAwait(false);
        }
        if (!result.Ok) throw new InvalidOperationException(result.Code == BrokerCode.UnknownOp
            ? "This broker has no typed tray status; update the broker separately." : result.Error ?? "Status unavailable.");
        var snapshot = result.Result?.Deserialize<TrayStatus>() ?? throw new InvalidDataException("The broker returned no status snapshot.");
        if (_version >= 2 && (snapshot.Version != _version || snapshot.Capabilities == null ||
            new[] { "light-status", "full-verification", "restoration-state", "mutation-generation" }.Any(c => !snapshot.Capabilities.Contains(c))))
            throw new InvalidDataException("The broker lacks the required automatic-refresh capabilities.");
        if (_version == 3 && !snapshot.Capabilities!.Contains("applied-settings")) throw new InvalidDataException("Dynamic settings capability is unavailable.");
        var validator = new TrayStatusCache();
        if (!validator.Accept(snapshot)) throw new InvalidDataException(validator.Error);
        if (_version == 3) snapshot = await CollectRows(snapshot, token).ConfigureAwait(false);
        return snapshot;
    }
    private async Task<TrayStatus> CollectRows(TrayStatus snapshot, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await CollectRowsCore(snapshot, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_paused) { throw new TimeoutException("Applied row collection deadline exceeded"); }
    }
    private async Task<TrayStatus> CollectRowsCore(TrayStatus snapshot, CancellationToken token)
    {
        int frames = 0; var devices = new List<StatusDevice>();
        foreach (var device in snapshot.Devices)
        {
            var applied = device.Applied!; var rows = applied.Rows.ToList(); string? cursor = applied.NextCursor;
            int total = applied.TotalRows ?? rows.Count;
            if (total < rows.Count || total > AppliedCatalog.MaxRows) throw new InvalidDataException("Invalid applied row count");
            while (cursor != null)
            {
                if (_paused) throw new OperationCanceledException("Applied row paging paused");
                if (++frames > 256) throw new InvalidDataException("Applied row collection exceeds the bounded page count");
                var response = await _call(new(BrokerRequest.OpStatus, GpuId: device.StableId, Kind: "rows", Key: cursor, Value: "3"), token).ConfigureAwait(false);
                if (response is not { Ok: true, Result: { } json }) throw new InvalidDataException(response?.Error ?? "Applied row page unavailable");
                var page = json.Deserialize<AppliedSettings>() ?? throw new InvalidDataException("Missing row page");
                if (page.InstanceId != applied.InstanceId || page.GpuId != applied.GpuId || page.Generation != applied.Generation || page.InventoryAt != applied.InventoryAt
                    || page.TotalRows != total || page.Rows == null || page.Rows.Count == 0 || rows.Count + page.Rows.Count > total || page.NextCursor == cursor)
                    throw new InvalidDataException("Applied row page context changed; refresh again");
                var check = snapshot with { Devices = [device with { Applied = page }] };
                if (!new TrayStatusCache().Accept(check)) throw new InvalidDataException("Malformed applied row page");
                rows.AddRange(page.Rows); cursor = page.NextCursor;
            }
            if (rows.Count != total || rows.Select(r => r.Id).Distinct().Count() != total) throw new InvalidDataException("Incomplete or duplicate applied rows");
            devices.Add(device with { Applied = applied with { Rows = rows, NextCursor = null } });
        }
        return snapshot with { Devices = devices };
    }
}
