using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    public BrokerStatusLifecycle StatusLifecycle { get; } = new();
    public TimeProvider StatusClock { get; set; } = TimeProvider.System;
    public Func<string?>? StatusMeasurementReason { get; set; }
    private long _statusChangedStamp = long.MinValue;
    private readonly ConcurrentDictionary<string, string> _statusContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Generation, string Context, StatusVerification Value)> _fullStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Generation, StatusControls Value)> _lightStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _controlContexts = new(StringComparer.OrdinalIgnoreCase);
    private void InvalidateStatus() { Interlocked.Increment(ref _statusEpoch); StatusMutationCompleted(); }
    private void StatusMutationCompleted() => Volatile.Write(ref _statusChangedStamp, StatusClock.GetTimestamp());
    private bool StatusSettled => Volatile.Read(ref _statusChangedStamp) is var stamp &&
        (stamp == long.MinValue || StatusClock.GetElapsedTime(stamp) >= TimeSpan.FromSeconds(2));

    private BrokerResponse StatusV2(BrokerRequest req)
    {
        if (req.Kind is not ("cached" or "light" or "verify")) return Fail("status kind must be cached, light or verify", BrokerCode.BadRequest);
        if (req.Kind != "cached" && req.GpuId == null) return Fail("hardware status needs a stable GPU id", BrokerCode.BadRequest);
        var ids = req.GpuId == null ? _gpus.Ids.Take(32).ToArray() : [req.GpuId];
        var devices = new List<StatusDevice>();
        bool entered = Monitor.TryEnter(_operation);
        try
        {
            bool measurement = (StatusMeasurementReason ?? _judgeBusyReason)() != null;
            foreach (var requested in ids)
            {
                var target = _gpus.Resolve(requested);
                if (target == null) return Fail("no GPU with the requested stable id", BrokerCode.NoGpu);
                string id = target.StableId;
                var lifecycle = StatusLifecycle.Read(id);
                bool busy = !entered || Volatile.Read(ref _closed);
                if (!entered)
                {
                    var prior = _statusDevices.GetValueOrDefault(id) ?? new(id, target.Name, target.Driver, target.Vbios, null, false, false, null, null, true, null, null, []);
                    devices.Add(prior with { OperationInProgress = true, Refresh = new(Volatile.Read(ref _statusEpoch), null, lifecycle, false, measurement, null, "Operation in progress") });
                    continue;
                }
                var errors = new List<string>();
                string profilePath = DailyProfile.PathBeside(target.Journal.Path), experimentPath = Experiment.PathBeside(target.Journal.Path);
                var profile = ReadStatusFile<DailyProfile>(profilePath, errors);
                var experiment = ReadStatusFile<Experiment>(experimentPath, errors);
                string context = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { profile, experiment })));
                if (_statusContexts.TryGetValue(id, out var before) && before != context) InvalidateStatus();
                _statusContexts[id] = context;
                long generation = Volatile.Read(ref _statusEpoch);
                bool settled = StatusSettled;
                string? pending = busy ? "Operation in progress" : lifecycle.Startup is RestorationState.Pending or RestorationState.Unknown ? "Startup restoration pending"
                    : lifecycle.Recovery == RestorationState.Pending ? "Recovery pending" : measurement ? "Measurement in progress" : !settled ? "Waiting for tuning changes to settle"
                    : null;
                if (profile?.OtherVbios(target.Vbios) is { } binding) errors.Add(binding);
                bool canRead = pending == null && errors.Count == 0;
                StatusControls? controls = _lightStatus.TryGetValue(id, out var previous) && previous.Generation == generation ? previous.Value : null;
                if (req.Kind != "cached" && canRead)
                {
                    var reader = new StatusControlReader(_hw, target.Session, StatusClock);
                    var settings = (profile?.Writes ?? []).Concat(StatusControlReader.Common).Where(w => w.Kind != "vf")
                        .DistinctBy(w => (w.Kind, w.Key)).Take(64).Select(reader.Setting).ToArray();
                    string controlContext = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
                    if (_controlContexts.TryGetValue(id, out var lastControls) && lastControls != controlContext
                        && Interlocked.CompareExchange(ref _statusEpoch, generation + 1, generation) == generation) generation++;
                    _controlContexts[id] = controlContext;
                    controls = new(_host.Now, settings, StatusControlReader.Compare(profile, settings),
                        settings.Any(s => s.Error != null) ? "Some current controls are unavailable; see readback diagnostics." : null);
                    _lightStatus[id] = (generation, controls);
                    if (req.Kind == "verify")
                        _fullStatus[id] = (generation, context, VerifyEffectiveStatus(target, profile, controls, reader));
                }
                _fullStatus.TryGetValue(id, out var full);
                bool applicable = full.Value != null && full.Generation == generation && full.Context == context;
                pending ??= !applicable ? "Full curve verification required" : controls?.Comparison == DailyComparison.Different && full.Value!.Comparison != DailyComparison.Different
                    ? "Controls changed outside Clocktail; full verification required" : null;
                // A lifecycle invalidation may arrive from a watcher while driver getters are running.
                if (generation != Volatile.Read(ref _statusEpoch)) { generation = Volatile.Read(ref _statusEpoch); pending = "State changed during readback"; controls = null; lifecycle = StatusLifecycle.Read(id); }
                var refresh = new StatusRefresh(generation, full.Value == null ? null : full.Generation, lifecycle, settled, measurement, controls, pending);
                var device = new StatusDevice(id, target.Name, target.Driver, target.Vbios, profile,
                    !errors.Any(e => e.StartsWith(profilePath, StringComparison.Ordinal)), !errors.Any(e => e.StartsWith(experimentPath, StringComparison.Ordinal)),
                    experiment?.Game, experiment?.Trial?.Change, busy, full.Value, CachedTelemetry(id), errors, refresh);
                _statusDevices[id] = device;
                devices.Add(device);
            }
            return Ok(new TrayStatus(2, _statusInstance, Interlocked.Increment(ref _statusRevision), _host.Now, devices, ["light-status", "full-verification", "restoration-state", "mutation-generation"]));
        }
        finally { if (entered) Monitor.Exit(_operation); }
    }

    private StatusVerification VerifyEffectiveStatus(IGpuTarget target, DailyProfile? profile, StatusControls controls, StatusControlReader reader)
    {
        var settings = controls.Settings.ToList();
        var curve = new List<StatusCurvePoint>();
        string? error = null;
        var comparison = controls.Comparison;
        try
        {
            reader.CheckBudget();
            var vf = VfReader.Read(_hw, target.Session);
            settings.Add(new("vf", "core", "kHz per point", VfOffsets.Encode(vf.Offsets())));
            curve.AddRange(vf.Points.Select(p => new StatusCurvePoint(p.VoltageUv / 1000.0, p.EffectiveKhz / 1000.0, vf.Mode == VfMode.Regional ? p.BaseKhz / 1000.0 : null)));
            var explicitCurve = profile?.Writes.FirstOrDefault(w => w.Kind == "vf" && w.Key == "core");
            var explicitCore = profile?.Writes.FirstOrDefault(w => w.Kind == "pstates20" && w.Key == "core");
            bool globalKnown = int.TryParse(settings.FirstOrDefault(s => s.Kind == "pstates20" && s.Key == "core")?.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int global);
            bool consistent = vf.Mode == VfMode.Regional && vf.Points.Count > 0 && globalKnown && (explicitCurve != null
                ? vf.Points.All(p => (long)p.BaseKhz + global + p.OffsetKhz == p.EffectiveKhz) && (explicitCore != null || global == 0)
                // Match effective displacement, not the sum of mirrored P0 and point controls.
                : explicitCore != null && vf.Points.All(p => Math.Abs((long)p.EffectiveKhz - p.BaseKhz - global) < 1000));
            if (explicitCurve != null && VfOffsets.Encode(vf.Offsets()) != explicitCurve.Value) comparison = DailyComparison.Different;
            if (!consistent && comparison != DailyComparison.Different) { comparison = DailyComparison.Unknown; error = "Effective curve could not be confirmed against the saved core context."; }
        }
        catch (Exception e) when (StatusReadError(e)) { comparison = DailyComparison.Unknown; error = e.Message; }
        return new(_host.Now, settings, curve, comparison, error ?? controls.Error);
    }
}
