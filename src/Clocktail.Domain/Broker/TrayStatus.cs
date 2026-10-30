using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public enum TrayState { Unknown, Unavailable, Stale, DailyMatched, DailySuspended, TrialActive, SettingsDiffer, Applying, Failed, StartupPending, Recovering, VerificationPending, MeasurementPaused }
public enum DailyComparison { Unknown, Matched, Different }

/// <summary>Canonical registry encodings and units; null is unreadable, never an implicit zero.</summary>
public sealed record StatusSetting(string Kind, string Key, string Unit, string? Value, string? Error = null);
public sealed record StatusCurvePoint(double Millivolts, double EffectiveMhz, double? StockMhz);
public sealed record StatusVerification(DateTimeOffset At, IReadOnlyList<StatusSetting> Settings,
    IReadOnlyList<StatusCurvePoint> Curve, DailyComparison Comparison, string? Error);
public sealed record StatusControls(DateTimeOffset At, IReadOnlyList<StatusSetting> Settings, DailyComparison Comparison, string? Error);
public sealed record StatusRefresh(long Generation, long? VerifiedGeneration, RestorationStatus Restoration,
    bool Settled, bool MeasurementBusy, StatusControls? Controls, string? FullRequiredReason);
public sealed record StatusDevice(string StableId, string Name, string? Driver, string? Vbios,
    DailyProfile? SavedDaily, bool SavedDailyKnown, bool ExperimentKnown, string? SessionGame,
    string? TrialChange, bool OperationInProgress, StatusVerification? Verification,
    FlightSample? Telemetry, IReadOnlyList<string> Errors, StatusRefresh? Refresh = null, AppliedSettings? Applied = null);

/// <summary>A request snapshot, not a subscription. Revision orders responses within one broker instance only.</summary>
public sealed record TrayStatus(int Version, string InstanceId, long Revision, DateTimeOffset CapturedAt,
    IReadOnlyList<StatusDevice> Devices, IReadOnlyList<string>? Capabilities = null)
{
    public const int CurrentVersion = 3;
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    public static TrayState State(StatusDevice? device, bool connected, DateTimeOffset capturedAt, DateTimeOffset now)
    {
        if (!connected || device == null) return TrayState.Unavailable;
        // Age the whole snapshot as well as verification: a cached "no trial" is not a live assertion.
        if (now < capturedAt || now - capturedAt > FreshFor) return TrayState.Stale;
        if (device.OperationInProgress) return TrayState.Applying;
        if (device.Refresh is { } refresh)
        {
            if (refresh.Restoration.Startup is RestorationState.Pending or RestorationState.Unknown) return TrayState.StartupPending;
            if (refresh.Restoration.Recovery == RestorationState.Pending) return TrayState.Recovering;
            if (refresh.Restoration.Startup == RestorationState.Failed || refresh.Restoration.Recovery == RestorationState.Failed) return TrayState.Failed;
            if (device.Errors.Count > 0) return TrayState.Failed;
            if (refresh.MeasurementBusy) return TrayState.MeasurementPaused;
            if (!refresh.Settled) return TrayState.VerificationPending;
        }
        if (device.Errors.Count > 0) return TrayState.Failed;
        if (!device.ExperimentKnown || !device.SavedDailyKnown) return TrayState.Unknown;
        if (device.TrialChange != null) return TrayState.TrialActive;
        if (device.SavedDaily?.Suspended == true) return TrayState.DailySuspended;
        if (device.Refresh is { } current)
        {
            if (current.Controls is not { } controls) return TrayState.VerificationPending;
            if (now < controls.At || now - controls.At > FreshFor) return TrayState.Stale;
            if (controls.Comparison == DailyComparison.Different) return TrayState.SettingsDiffer;
            if (controls.Error != null || controls.Comparison == DailyComparison.Unknown) return TrayState.Unknown;
            if (current.FullRequiredReason != null || current.VerifiedGeneration != current.Generation) return TrayState.VerificationPending;
            if (device.Verification?.Comparison == DailyComparison.Different) return TrayState.SettingsDiffer;
            return device.Verification is { Comparison: DailyComparison.Matched, Error: null } full && full.At <= now && device.SavedDaily is { Writes.Count: > 0 }
                ? TrayState.DailyMatched : TrayState.Unknown;
        }
        if (device.Verification is not { } v) return TrayState.Unknown;
        if (now < v.At || now - v.At > FreshFor) return TrayState.Stale;
        if (v.Comparison == DailyComparison.Different) return TrayState.SettingsDiffer;
        if (v.Error != null) return TrayState.Unknown;
        return v.Comparison == DailyComparison.Matched && device.SavedDaily is { Writes.Count: > 0 }
            ? TrayState.DailyMatched : TrayState.Unknown;
    }
}

/// <summary>Reject out-of-order replies; a reconnect replaces every confirmation from the old instance.</summary>
public sealed class TrayStatusCache
{
    public TrayStatus? Snapshot { get; private set; }
    public bool Connected { get; private set; }
    public string? Error { get; private set; }
    public string? CompatibilityNotice { get; set; }
    private readonly HashSet<string> _retiredInstances = new();
    public bool Accept(TrayStatus snapshot)
    {
        if (snapshot.Version is not (1 or 2 or TrayStatus.CurrentVersion)) { Disconnect("Broker status version is unsupported; update the broker and tray together."); return false; }
        if (string.IsNullOrWhiteSpace(snapshot.InstanceId) || snapshot.Revision < 1 || snapshot.Devices == null || snapshot.Devices.Count > 32
            || snapshot.Devices.Select(d => d?.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Devices.Count
            || snapshot.Devices.Any(d => d == null || string.IsNullOrWhiteSpace(d.StableId) || d.Name == null || d.Errors == null
                || d.SavedDaily is { Writes: null }
                || snapshot.Version >= 2 && (d.Refresh == null || d.Refresh.Restoration == null || d.Refresh.Generation < 0
                    || d.Refresh.VerifiedGeneration is { } verified && (verified < 0 || verified > d.Refresh.Generation)
                    || d.Refresh.Restoration.Generation < 0 || !Enum.IsDefined(d.Refresh.Restoration.Startup) || !Enum.IsDefined(d.Refresh.Restoration.Recovery)
                    || d.Refresh.Controls is { } check && (!Enum.IsDefined(check.Comparison) || check.Settings == null || check.Settings.Count > (snapshot.Version == 3 ? 144 : 64) || check.Settings.Any(s => s == null || s.Kind == null || s.Key == null)))
                || snapshot.Version == 3 && !AppliedValid(d.Applied, snapshot.InstanceId, d.StableId, d.Refresh!.Generation)
                || d.Verification is { } v && (!Enum.IsDefined(v.Comparison) || v.Settings == null || v.Curve == null || v.Settings.Count > (snapshot.Version == 3 ? 145 : 65) || v.Curve.Count > 255
                    || v.Settings.Any(s => s == null || s.Kind == null || s.Key == null)
                    || v.Curve.Any(p => p == null || !double.IsFinite(p.Millivolts) || !double.IsFinite(p.EffectiveMhz) || p.StockMhz is { } stock && !double.IsFinite(stock)))))
        { Disconnect("The broker returned malformed status data."); return false; }
        if (_retiredInstances.Contains(snapshot.InstanceId)) return false;
        if (Snapshot is { } old)
        {
            if (old.InstanceId == snapshot.InstanceId && (old.Revision >= snapshot.Revision || snapshot.Devices.Any(d =>
                d.Refresh is { } r && old.Devices.FirstOrDefault(p => p.StableId == d.StableId)?.Refresh is { } prior && r.Generation < prior.Generation))) return false;
            if (old.InstanceId != snapshot.InstanceId)
            {
                if (_retiredInstances.Count >= 32) _retiredInstances.Clear();
                _retiredInstances.Add(old.InstanceId);
            }
        }
        Snapshot = snapshot;
        Connected = true;
        Error = null;
        return true;
    }
    public void Disconnect(string error) { Connected = false; Error = error; }
    private static bool AppliedValid(AppliedSettings? a, string instance, string gpuId, long generation) => a != null && a.InstanceId == instance && a.GpuId == gpuId && a.Generation == generation
        && a.CandidateCount >= 0 && a.OmittedCount >= 0 && a.Rows != null && a.Coverage != null && a.Coverage.Count <= 96
        && (a.TotalRows == null || a.TotalRows >= a.Rows.Count && a.TotalRows <= AppliedCatalog.MaxRows)
        && a.Rows.Count <= AppliedCatalog.MaxRows && a.Rows.All(r => r != null && r.Id != null && r.Kind != null && r.Key != null && r.Label != null
            && Enum.IsDefined(r.Type) && Enum.IsDefined(r.Reference) && Enum.IsDefined(r.Modification) && Enum.IsDefined(r.Daily) && Enum.IsDefined(r.ReadClass)
            && (r.CurrentValue?.Length ?? 0) <= AppliedCatalog.MaxValueChars && (r.SavedValue?.Length ?? 0) <= AppliedCatalog.MaxValueChars
            && (r.LastValue?.Length ?? 0) <= AppliedCatalog.MaxValueChars && (r.ReferenceValue?.Length ?? 0) <= AppliedCatalog.MaxValueChars
            && (r.SampleGeneration == null || r.SampleGeneration >= 0 && r.SampleGeneration <= generation)) && a.Rows.Select(r => r.Id).Distinct().Count() == a.Rows.Count;
}
