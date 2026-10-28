using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tray;

/// <summary>Entirely synthetic; no pipes, hardware, recovery or persistent profile state.</summary>
internal sealed class DemoSource(string state) : ITrayStatusSource
{
    public static readonly string[] States = ["matched", "trial", "stale", "unavailable", "suspended", "different", "quiet", "startup", "older-curve", "changed", "recovery", "incompatible", "extra-controls", "trial-controls", "shaped-vf", "long-fans", "many-settings", "empty", "partial", "unavailable-saved", "unknown-profile"];
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
        if (state != "incompatible")
        {
            var rows = new List<AppliedRow>();
            void Row(string kind, string key, string? current, string? savedValue, string? reference, AppliedReference source = AppliedReference.DocumentedNeutral, string? child = null, string? error = null)
            {
                var type = AppliedCatalog.Type(kind, key, child); var descriptor = AppliedCatalog.Find(kind)!;
                bool? same = AppliedCatalog.Equal(child == null ? kind : "vdemand", key, current, reference);
                rows.Add(new(kind + "/" + key + (child == null ? "" : "/" + child), kind, key, child, descriptor.Group, AppliedCatalog.Label(kind, key, child), type,
                    savedValue != null, savedValue, !DailyProfile.TrialOnly(kind, key), state == "suspended", current, current ?? savedValue,
                    kind == "vf" ? _fullAt : at, now, refresh.Generation, error, reference, reference == null ? AppliedReference.Unknown : source,
                    error != null ? AppliedModification.Unavailable : same is true ? AppliedModification.Unchanged : same is false ? AppliedModification.Modified : AppliedModification.Unconfirmed,
                    savedValue == null ? DailyComparison.Unknown : AppliedCatalog.Equal(kind, key, current, savedValue) is true ? DailyComparison.Matched : current == null ? DailyComparison.Unknown : DailyComparison.Different,
                    kind == "vf" ? AppliedReadClass.Full : AppliedReadClass.Light,
                    kind == "vf" && savedValue == null ? "Not explicitly saved; effective target follows saved core." : null));
            }
            foreach (var w in saved) Row(w.Kind, w.Key, settings.First(s => s.Kind == w.Kind && s.Key == w.Key).Value, w.Value, w.Kind == "power" ? "575000" : "0", w.Kind == "power" ? AppliedReference.Firmware : AppliedReference.DocumentedNeutral);
            Row("vf", "core", state == "shaped-vf" ? "0*60,15000*67" : "0*127", state == "shaped-vf" ? "0*60,15000*67" : null, "0*127");
            if (state == "extra-controls")
            {
                Row("clockdomain", "video", "100000", null, "0"); Row("clockdomain", "sys", "-15000", null, "0");
                Row("clockratio", "msvdd", "62000", null, "58976", AppliedReference.CapturedOriginal);
                foreach (var slot in new[] { "rel", "alt", "ov", "vmin" }) Row("rails", "msvdd", slot == "rel" ? "-50000" : "10000", null, "5000", AppliedReference.CapturedOriginal, slot);
                Row("boost", "independent", "10", null, "0");
            }
            if (state is "trial-controls" or "many-settings")
            {
                for (int i = 0; i < (state == "many-settings" ? 32 : 1); i++) Row("fan", (100 + i).ToString(), "manual:50", null, "auto");
                Row("ocp", "nvvdd", "350000", null, "600000", AppliedReference.Firmware);
                Row("power", "f8", "300000", null, "non-limiting:600000");
                Row("clocklimit", "min", "1500000", null, "0"); Row("clocklimit", "voltage", "1500000", null, "0");
            }
            if (state is "long-fans" or "many-settings")
            {
                Row("fancurve", "0", "50.125:20.123:1100,79.25:60.5:2400,86.5:100:3500", "50.125:20.123:1100,79.25:60.5:2400,86.5:100:3500", "50:20:1100,83:50:1775,88:100:2875", AppliedReference.CapturedOriginal);
                Row("fanstop", "0", "43:47", null, "43:50", AppliedReference.CapturedOriginal);
            }
            if (state == "unavailable-saved")
            {
                rows.RemoveAll(r => r.Kind == "power"); Row("power", "fe", null, "450000", "575000", AppliedReference.Firmware, error: "Synthetic getter unavailable");
                device = device with { Refresh = refresh with { Controls = refresh.Controls! with { Comparison = DailyComparison.Unknown, Error = "Synthetic saved target unavailable" } } };
            }
            if (state is "empty" or "partial") { rows.Clear(); device = device with { SavedDaily = null }; }
            else if (device.SavedDaily is { } daily) device = device with { SavedDaily = daily with { Writes = rows.Where(r => r.Saved && r.Child == null).Select(r => new ProfileWrite(r.Kind, r.Key, r.SavedValue!)).ToArray() } };
            if (state is "trial-controls" or "many-settings") device = device with { TrialChange = "Synthetic temporary fan/current/lock overrides" };
            device = device with { Applied = new("synthetic-demo", refresh.Generation, _fullAt, state is not ("partial" or "unavailable-saved"), rows.Count, 0,
                state == "partial" ? ["Supported topology unavailable; coverage incomplete."] : ["New external changes in unscanned controls are found at the next full discovery."],
                rows.OrderBy(r => AppliedCatalog.Find(r.Kind)?.Order ?? 99).ThenBy(r => r.Key).ThenBy(r => r.Child).ToArray(), "demo-gpu") };
            if (state == "unknown-profile") device = device with
            {
                SavedDailyKnown = false, SavedDaily = null, Errors = ["Synthetic saved record unavailable"],
                Refresh = refresh with { Generation = 2, FullRequiredReason = "Saved record unavailable", Controls = null },
                Applied = device.Applied! with { Generation = 2, Complete = false, Coverage = ["Saved targets are last known; the current record is unavailable."],
                    Rows = device.Applied.Rows.Select(r => r with { CurrentValue = null, LastValue = r.CurrentValue, Modification = AppliedModification.Pending, Daily = DailyComparison.Unknown, Context = "Saved values are last known and unconfirmed" }).ToArray() }
            };
        }
        return new(state == "incompatible" ? 1 : 3, "synthetic-demo", ++_revision, state == "stale" ? at : now, [device], ["light-status", "full-verification", "restoration-state", "mutation-generation", "applied-settings"]);
    }
}
