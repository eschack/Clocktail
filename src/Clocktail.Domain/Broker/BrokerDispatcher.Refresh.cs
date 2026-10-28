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
    private readonly ConcurrentDictionary<string, string> _appliedObservations = new(StringComparer.Ordinal);
    private void InvalidateStatus() { Interlocked.Increment(ref _statusEpoch); StatusMutationCompleted(); }
    private void StatusMutationCompleted() => Volatile.Write(ref _statusChangedStamp, StatusClock.GetTimestamp());
    private bool StatusSettled => Volatile.Read(ref _statusChangedStamp) is var stamp &&
        (stamp == long.MinValue || StatusClock.GetElapsedTime(stamp) >= TimeSpan.FromSeconds(2));

    private BrokerResponse StatusV2(BrokerRequest req)
    {
        bool dynamic = req.Value == "3";
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
                    long busyGeneration = Volatile.Read(ref _statusEpoch);
                    var busyApplied = new AppliedSettings(_statusInstance, busyGeneration, prior.Applied?.InventoryAt, false, prior.Applied?.CandidateCount ?? 0, 0,
                        ["Operation in progress; current rows unconfirmed."], (prior.Applied?.Rows ?? []).Select(r => r with { CurrentValue = null, LastValue = r.CurrentValue ?? r.LastValue, Modification = AppliedModification.Pending, Error = "Operation in progress" }).ToArray(), id);
                    devices.Add(prior with { OperationInProgress = true, Refresh = new(busyGeneration, null, lifecycle, false, measurement, null, "Operation in progress"), Applied = dynamic ? busyApplied : null });
                    continue;
                }
                var errors = new List<string>();
                string profilePath = DailyProfile.PathBeside(target.Journal.Path), experimentPath = Experiment.PathBeside(target.Journal.Path);
                var profile = ReadStatusFile<DailyProfile>(profilePath, errors);
                bool savedKnown = !errors.Any(e => e.StartsWith(profilePath, StringComparison.Ordinal));
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
                AppliedSettings? applied = null;
                if (req.Kind != "cached" && canRead)
                {
                    var reader = new StatusControlReader(_hw, target.Session, StatusClock);
                    var wanted = (profile?.Writes ?? []).Concat(profile?.Adopted ?? []).Concat(StatusControlReader.Common).Where(w => w.Kind != "vf")
                        .DistinctBy(w => (w.Kind, w.Key)).ToArray();
                    var settings = wanted.Take(dynamic ? 144 : 64).Select(reader.Setting).ToArray();
                    if (dynamic) applied = Applied(target, profile, experiment, generation, reader, req.Kind == "verify", null, savedKnown);
                    string controlContext = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
                    bool changed = _controlContexts.TryGetValue(id, out var lastControls) && lastControls != controlContext;
                    if (dynamic) foreach (var (key, observation) in reader.Observed)
                    {
                        string signature = JsonSerializer.Serialize(observation);
                        string observationKey = id + "/" + key;
                        if (_appliedObservations.TryGetValue(observationKey, out var oldValue) && oldValue != signature) changed = true;
                        _appliedObservations[observationKey] = signature;
                    }
                    if (dynamic) foreach (var (key, identity) in reader.Topologies)
                    {
                        string topologyKey = id + "/topology/" + key;
                        if (_appliedObservations.TryGetValue(topologyKey, out var oldIdentity) && oldIdentity != identity)
                        { changed = true; _appliedReferenceReset[id] = _host.Now; }
                        _appliedObservations[topologyKey] = identity;
                    }
                    if (changed
                        && Interlocked.CompareExchange(ref _statusEpoch, generation + 1, generation) == generation) generation++;
                    _controlContexts[id] = controlContext;
                    controls = new(_host.Now, settings, StatusControlReader.Compare(profile, settings),
                        settings.Any(s => s.Error != null) ? "Some current controls are unavailable; see readback diagnostics." : null);
                    if (wanted.Length > settings.Length) controls = controls with { Comparison = DailyComparison.Unknown, Error = "Required control limit reached; complete comparison unavailable." };
                    _lightStatus[id] = (generation, controls);
                    if (req.Kind == "verify")
                        _fullStatus[id] = (generation, context, VerifyEffectiveStatus(target,
                            dynamic && profile != null ? profile with { Writes = profile.Writes.Concat(profile.Adopted ?? []).DistinctBy(w => (w.Kind, w.Key)).ToArray() } : profile, controls, reader));
                    if (dynamic) applied = Applied(target, profile, experiment, generation, reader, false, req.Kind == "verify" ? _fullStatus[id].Value : null, savedKnown);
                }
                _fullStatus.TryGetValue(id, out var full);
                bool applicable = full.Value != null && full.Generation == generation && full.Context == context;
                pending ??= !applicable ? "Full curve verification required" : controls?.Comparison == DailyComparison.Different && full.Value!.Comparison != DailyComparison.Different
                    ? "Controls changed outside Clocktail; full verification required" : null;
                // A lifecycle invalidation may arrive from a watcher while driver getters are running.
                if (generation != Volatile.Read(ref _statusEpoch)) { generation = Volatile.Read(ref _statusEpoch); pending = "State changed during readback"; controls = null; lifecycle = StatusLifecycle.Read(id); }
                if (dynamic && (applied == null || applied.Generation != generation)) applied = Applied(target, profile, experiment, generation, null, false, null, savedKnown);
                var refresh = new StatusRefresh(generation, full.Value == null ? null : full.Generation, lifecycle, settled, measurement, controls, pending);
                var device = new StatusDevice(id, target.Name, target.Driver, target.Vbios, profile,
                    !errors.Any(e => e.StartsWith(profilePath, StringComparison.Ordinal)), !errors.Any(e => e.StartsWith(experimentPath, StringComparison.Ordinal)),
                    experiment?.Game, experiment?.Trial?.Change, busy, full.Value, CachedTelemetry(id), errors, refresh, applied);
                _statusDevices[id] = device;
                devices.Add(device);
            }
            var status = new TrayStatus(dynamic ? 3 : 2, _statusInstance, Interlocked.Increment(ref _statusRevision), _host.Now, devices,
                dynamic ? ["light-status", "full-verification", "restoration-state", "mutation-generation", "applied-settings"] : ["light-status", "full-verification", "restoration-state", "mutation-generation"]);
            return dynamic ? AppliedResponse(status) : Ok(status);
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
