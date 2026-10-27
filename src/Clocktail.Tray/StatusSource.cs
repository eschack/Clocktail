using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tray;

internal interface IStatusSource
{
    Task<TrayStatus?> Fetch(bool verify, string? gpuId, CancellationToken token);
}

internal sealed class PipeStatusSource : IStatusSource
{
    public async Task<TrayStatus?> Fetch(bool verify, string? gpuId, CancellationToken token)
    {
        var result = await BrokerClient.TryCallAsync(new(BrokerRequest.OpStatus, GpuId: verify ? gpuId : null,
            Kind: verify ? "verify" : "cached", Value: "1"), token);
        if (result == null) return null;
        if (!result.Ok) throw new InvalidOperationException(result.Code == BrokerCode.UnknownOp
            ? "This broker does not support tray status. Update the broker separately." : result.Error ?? "Status is unavailable.");
        var snapshot = result.Result?.Deserialize<TrayStatus>() ?? throw new InvalidDataException("The broker returned no status snapshot.");
        var validator = new TrayStatusCache();
        if (!validator.Accept(snapshot)) throw new InvalidDataException(validator.Error);
        return snapshot;
    }
}

/// <summary>Entirely synthetic. Demo mode never constructs the pipe source or opens a GPU.</summary>
internal sealed class DemoSource(string state) : IStatusSource
{
    public static readonly string[] States = ["matched", "trial", "stale", "unavailable", "suspended", "different", "quiet"];
    private long _revision;
    public Task<TrayStatus?> Fetch(bool verify, string? gpuId, CancellationToken token) => Task.FromResult(Create());
    public TrayStatus? Create()
    {
        if (state == "unavailable") return null;
        var now = DateTimeOffset.UtcNow;
        var at = now.AddSeconds(state == "stale" ? -125 : -8);
        ProfileWrite[] saved = [new("pstates20", "core", "240000"), new("pstates20", "memory", "3000000"),
            new("clockdomain", "xbar", "300000"), new("power", "fe", "450000")];
        var settings = saved.Select(w => new StatusSetting(w.Kind, w.Key, w.Kind == "power" ? "mW" : "kHz", w.Value)).ToList();
        settings.Add(new("boost", "legacy", "%", "0"));
        settings.Add(new("vdemand", "xbar", "µV", "0"));
        if (state == "different") settings[0] = settings[0] with { Value = "0" };
        var curve = Enumerable.Range(0, 18).Select(i => new StatusCurvePoint(850 + i * 10, 2700 + i * 30, 2460 + i * 30)).ToArray();
        var device = new StatusDevice("demo-gpu", "Example GeForce RTX GPU", "Example driver", "Example firmware",
            new(saved, now.AddDays(-1), Suspended: state == "suspended"), true, true, state == "trial" ? "Example game" : null,
            state == "trial" ? "+15 MHz temporary curve trial" : null, false,
            new(at, settings, curve, state == "different" ? DailyComparison.Different : DailyComparison.Matched, null),
            new(at, 0, 300, 850, 31, 33, 0, null), []);
        return new(1, "synthetic-demo", ++_revision, state == "stale" ? at : now, [device]);
    }
}
