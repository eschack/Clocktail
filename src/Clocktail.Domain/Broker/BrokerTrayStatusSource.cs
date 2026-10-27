using System.Text.Json;

namespace Clocktail.Domain.Broker;

public sealed class BrokerTrayStatusSource : ITrayStatusSource
{
    private readonly Func<BrokerRequest, CancellationToken, Task<BrokerResponse?>> _call;
    private int _version = 2;
    public string? CompatibilityNotice { get; private set; }
    public BrokerTrayStatusSource(Func<BrokerRequest, CancellationToken, Task<BrokerResponse?>>? call = null)
        => _call = call ?? ((request, token) => BrokerClient.TryCallAsync(request, token));
    public async Task<TrayStatus?> Fetch(StatusFetchKind kind, string? gpuId, CancellationToken token)
    {
        string op = kind switch { StatusFetchKind.Cached => "cached", StatusFetchKind.Light => "light", _ => "verify" };
        if (_version == 1 && kind == StatusFetchKind.Light) throw new InvalidOperationException("Light status is unavailable on this broker.");
        var result = await _call(new(BrokerRequest.OpStatus, GpuId: kind == StatusFetchKind.Cached ? null : gpuId, Kind: op, Value: _version.ToString()), token).ConfigureAwait(false);
        if (result == null) return null;
        if (_version == 2 && kind == StatusFetchKind.Cached && (!result.Ok && result.Code is BrokerCode.BadRequest or BrokerCode.UnknownOp
            || result.Ok && result.Result is { } json && json.TryGetProperty("Version", out var version) && version.GetInt32() == 1))
        {
            _version = 1;
            CompatibilityNotice = "Older broker: automatic refresh is disabled. Check now performs manual full verification.";
            return await Fetch(StatusFetchKind.Cached, null, token).ConfigureAwait(false);
        }
        if (!result.Ok) throw new InvalidOperationException(result.Code == BrokerCode.UnknownOp
            ? "This broker has no typed tray status; update the broker separately." : result.Error ?? "Status unavailable.");
        var snapshot = result.Result?.Deserialize<TrayStatus>() ?? throw new InvalidDataException("The broker returned no status snapshot.");
        if (_version == 2 && (snapshot.Version != 2 || snapshot.Capabilities == null ||
            new[] { "light-status", "full-verification", "restoration-state", "mutation-generation" }.Any(c => !snapshot.Capabilities.Contains(c))))
            throw new InvalidDataException("The broker lacks the required automatic-refresh capabilities.");
        var validator = new TrayStatusCache();
        if (!validator.Accept(snapshot)) throw new InvalidDataException(validator.Error);
        return snapshot;
    }
}
