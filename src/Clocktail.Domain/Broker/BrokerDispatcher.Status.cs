using System.Collections.Concurrent;
using System.Diagnostics;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    private readonly string _statusInstance = Guid.NewGuid().ToString("N");
    private long _statusRevision, _statusEpoch;
    private readonly ConcurrentDictionary<string, (long Epoch, DailyProfile? Profile, StatusVerification Value)> _statusVerified = new();
    private readonly ConcurrentDictionary<string, StatusDevice> _statusDevices = new();

    /// <summary>The existing flight recorder's latest sample. Must never initiate a driver read.</summary>
    public Func<string, FlightSample?> CachedTelemetry { get; set; } = _ => null;

    private BrokerResponse Status(BrokerRequest req)
    {
        if (req.Value == "2") return StatusV2(req);
        if (req.Kind is not ("cached" or "verify")) return Fail("status kind must be cached or verify", BrokerCode.BadRequest);
        if (req.Value != null && req.Value != "1") return Fail("unsupported status version", BrokerCode.BadRequest);
        if (req.Kind == "verify" && req.GpuId == null) return Fail("verification needs a stable GPU id", BrokerCode.BadRequest);
        var ids = req.GpuId == null ? _gpus.Ids.Take(32).ToArray() : [req.GpuId];
        var devices = new List<StatusDevice>();
        // Never queue a status reader behind a mutation or hold the write window while a client waits.
        bool entered = Monitor.TryEnter(_operation);
        try
        {
            if (!entered && req.Kind == "verify") return Fail("the broker is busy; check again after the operation completes", BrokerCode.WindowBusy);
            if (req.Kind == "verify" && Volatile.Read(ref _closed)) return Fail(ClosedError, BrokerCode.WindowBusy);
            if (req.Kind == "verify" && (StatusMeasurementReason ?? _judgeBusyReason)() is { } busy) return Fail(JudgeBusy(busy), BrokerCode.JudgeBusy);
            foreach (var id in ids)
            {
                var target = _gpus.Resolve(id);
                if (target == null) return Fail("no GPU with the requested stable id", BrokerCode.NoGpu);
                if (!entered)
                {
                    var prior = _statusDevices.GetValueOrDefault(id);
                    devices.Add(prior is null
                        ? new(id, target.Name, target.Driver, target.Vbios, null, false, false, null, null, true, null, null, [])
                        : prior with { OperationInProgress = true, Verification = null, Refresh = null });
                    continue;
                }
                var errors = new List<string>();
                var profilePath = DailyProfile.PathBeside(target.Journal.Path);
                var experimentPath = Experiment.PathBeside(target.Journal.Path);
                var profile = ReadStatusFile<DailyProfile>(profilePath, errors);
                var experiment = ReadStatusFile<Experiment>(experimentPath, errors);
                long epoch = Volatile.Read(ref _statusEpoch);
                StatusVerification? verification = null;
                if (req.Kind == "verify")
                {
                    verification = VerifyStatus(target, profile);
                    _statusVerified[id] = (epoch, profile, verification);
                }
                else if (_statusVerified.TryGetValue(id, out var saved) && saved.Epoch == epoch
                    && System.Text.Json.JsonSerializer.Serialize(saved.Profile) == System.Text.Json.JsonSerializer.Serialize(profile))
                    verification = saved.Value;
                // This endpoint never calls Settle, Adopt, a setter, a transaction, or PresentMon.
                var device = new StatusDevice(id, target.Name, target.Driver, target.Vbios, profile,
                    !errors.Any(e => e.StartsWith(profilePath, StringComparison.Ordinal)),
                    !errors.Any(e => e.StartsWith(experimentPath, StringComparison.Ordinal)),
                    experiment?.Game, experiment?.Trial?.Change, false, verification, CachedTelemetry(id), errors);
                _statusDevices[id] = device;
                devices.Add(device);
            }
            return Ok(new TrayStatus(1, _statusInstance, Interlocked.Increment(ref _statusRevision), _host.Now, devices));
        }
        finally { if (entered) Monitor.Exit(_operation); }
    }

    private static T? ReadStatusFile<T>(string path, List<string> errors) where T : class
    {
        try
        {
            var bytes = AtomicFile.Read(path, 1024 * 1024);
            if (bytes == null) return null;
            var result = System.Text.Json.JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("empty record");
            if (result is DailyProfile p && (p.Writes == null || p.Writes.Count > 64 || p.Writes.Any(w => w == null || string.IsNullOrWhiteSpace(w.Kind) || string.IsNullOrWhiteSpace(w.Key) || w.Value == null)))
                throw new InvalidDataException("invalid or oversized saved targets");
            if (result is Experiment exp && (string.IsNullOrWhiteSpace(exp.Game) || exp.Trial is { Change: null }))
                throw new InvalidDataException("invalid experiment record");
            return result;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add($"{path}: {e.Message}");
            return null;
        }
    }

    private StatusVerification VerifyStatus(IGpuTarget target, DailyProfile? profile)
    {
        var settings = new List<StatusSetting>();
        var curve = new List<StatusCurvePoint>();
        var failures = new List<string>();
        var budget = Stopwatch.StartNew();
        VfCurveSnapshot? vf = null;
        try
        {
            vf = VfReader.Read(_hw, target.Session);
            curve.AddRange(vf.Points.Select(p => new StatusCurvePoint(p.VoltageUv / 1000.0, p.EffectiveKhz / 1000.0,
                vf.Mode == VfMode.Regional ? p.BaseKhz / 1000.0 : null)));
            settings.Add(new("vf", "core", "kHz per point", VfOffsets.Encode(vf.Offsets())));
        }
        catch (Exception e) when (StatusReadError(e)) { failures.Add($"Effective curve: {e.Message}"); }

        ProfileWrite[] common = [new("pstates20", "core", "0"), new("pstates20", "memory", "0"),
            new("clockdomain", "xbar", "0"), new("power", "fe", "0"), new("boost", "legacy", "0"), new("vdemand", "xbar", "0")];
        var wanted = (profile?.Writes ?? []).Concat(common).DistinctBy(w => (w.Kind, w.Key)).Where(w => w.Kind != "vf").Take(64);
        foreach (var w in wanted)
        {
            try
            {
                if (budget.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("verification read budget exceeded; check again later");
                OwnedCell cell;
                if (w.Kind == ClockOffsets.Kind && Enum.TryParse<ClockDomain>(w.Key, true, out var domain) && Enum.IsDefined(domain))
                    cell = ClockOffsets.Cell(domain);
                else
                    cell = (WriteRegistry.Find(w.Kind) ?? throw new InvalidDataException("unsupported saved control"))
                        .Prepare(_hw, target.Session, w.Key, w.Value, WriteOptions.None).Cell;
                // Prepare constructs a plan; this readback deliberately never invokes it.
                var buffer = cell.Spec.ReadBuffer();
                var read = _hw.Call(target.Session, cell.Spec.Get, buffer);
                var value = read.IsOk ? cell.StateIn(buffer) : null;
                settings.Add(new(w.Kind, w.Key, cell.Unit, value, value == null ? $"unreadable ({read})" : null));
            }
            catch (Exception e) when (StatusReadError(e)) { settings.Add(new(w.Kind, w.Key, "", null, e.Message)); }
        }

        var comparison = DailyComparison.Unknown;
        if (profile is { Writes.Count: > 0 } && profile.Writes.Count <= 64)
        {
            var observed = settings.ToDictionary(s => (s.Kind, s.Key));
            bool missing = profile.Writes.Any(w => !observed.TryGetValue((w.Kind, w.Key), out var s) || s.Value == null);
            bool different = profile.Writes.Any(w => observed.TryGetValue((w.Kind, w.Key), out var s) && s.Value != null && s.Value != w.Value);
            // A matching P0/global offset is insufficient: the reported effective curve must agree as well.
            bool coreTarget = profile.Writes.Any(w => w.Kind == "vf" && w.Key == "core" || w.Kind == "pstates20" && w.Key == "core");
            bool explicitCurve = profile.Writes.Any(w => w.Kind == "vf" && w.Key == "core");
            bool explicitCore = profile.Writes.Any(w => w.Kind == "pstates20" && w.Key == "core");
            bool consistent = vf is { Mode: VfMode.Regional, Points.Count: > 0 }
                && observed.TryGetValue(("pstates20", "core"), out var core) && int.TryParse(core.Value, out var global)
                && (explicitCurve
                    ? vf.Points.All(p => (long)p.BaseKhz + global + p.OffsetKhz == p.EffectiveKhz) && (explicitCore || global == 0)
                    : explicitCore && vf.Points.All(p => Math.Abs((long)p.EffectiveKhz - p.BaseKhz - global) < 1000));
            if (different) comparison = DailyComparison.Different;
            else if (!missing && coreTarget && consistent) comparison = DailyComparison.Matched;
            else failures.Add("A complete saved-target and effective-curve match could not be established.");
        }
        return new(_host.Now, settings, curve, comparison, failures.Count == 0 ? null : string.Join(" ", failures));
    }

    private static bool StatusReadError(Exception e) => e is InvalidOperationException or NotSupportedException or InvalidDataException
        or NvApiException or NvmlException or ArgumentException or FormatException or OverflowException;
}
