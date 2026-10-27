using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tray;

/// <summary>Entirely synthetic; no pipes, hardware, recovery or persistent profile state.</summary>
internal sealed class DemoSource(string state) : ITrayStatusSource
{
    public static readonly string[] States = ["matched", "trial", "stale", "unavailable", "suspended", "different", "quiet", "startup", "older-curve", "changed", "recovery", "incompatible"];
    private long _revision;
    private DateTimeOffset? _fullAt;
    public string? CompatibilityNotice => state == "incompatible" ? "Older broker: automatic refresh unavailable; manual Check now only." : null;
    public Task<TrayStatus?> Fetch(StatusFetchKind kind, string? gpuId, CancellationToken token)
    {
        if (kind == StatusFetchKind.Full && state != "older-curve") _fullAt = DateTimeOffset.UtcNow;
        return Task.FromResult(Create());
    }
    public TrayStatus? Create()
    {
        if (state == "unavailable") return null;
        var now = DateTimeOffset.UtcNow;
        var at = now.AddSeconds(state == "stale" ? -125 : -8);
        _fullAt ??= now.AddSeconds(state == "older-curve" ? -240 : -8);
        ProfileWrite[] saved = [new("pstates20", "core", "240000"), new("pstates20", "memory", "3000000"), new("clockdomain", "xbar", "300000"), new("power", "fe", "450000")];
        var settings = saved.Select(w => new StatusSetting(w.Kind, w.Key, w.Kind == "power" ? "mW" : "kHz", w.Value)).ToList();
        settings.Add(new("boost", "legacy", "%", "0")); settings.Add(new("vdemand", "xbar", "µV", "0"));
        bool different = state is "different" or "changed";
        if (different) settings[0] = settings[0] with { Value = "0" };
        var curve = Enumerable.Range(0, 18).Select(i => new StatusCurvePoint(850 + i * 10, 2700 + i * 30, 2460 + i * 30)).ToArray();
        var lifecycle = new RestorationStatus(state == "startup" ? RestorationState.Pending : RestorationState.Ready,
            state == "recovery" ? RestorationState.Pending : RestorationState.Unknown, state == "recovery" ? 2 : 1, null);
        var refresh = new StatusRefresh(state is "changed" or "recovery" ? 2 : 1, 1, lifecycle, true, false,
            new(at, settings, different ? DailyComparison.Different : DailyComparison.Matched, null), state == "changed" ? "Full curve verification required" : null);
        var device = new StatusDevice("demo-gpu", "Example GeForce RTX GPU", "Example driver", "Example firmware",
            new(saved, now.AddDays(-1), Suspended: state == "suspended"), true, true, state == "trial" ? "Example game" : null,
            state == "trial" ? "+15 MHz temporary curve trial" : null, false,
            new(_fullAt.Value, settings, curve, state == "different" ? DailyComparison.Different : DailyComparison.Matched, null),
            new(at, 0, 300, 850, 31, 33, 0, null), [], state == "incompatible" ? null : refresh);
        return new(state == "incompatible" ? 1 : 2, "synthetic-demo", ++_revision, state == "stale" ? at : now, [device], ["light-status", "full-verification", "restoration-state", "mutation-generation"]);
    }
}
