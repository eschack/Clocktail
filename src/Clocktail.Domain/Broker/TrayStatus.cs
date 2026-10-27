using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public enum TrayState { Unknown, Unavailable, Stale, DailyMatched, DailySuspended, TrialActive, SettingsDiffer, Applying, Failed }
public enum DailyComparison { Unknown, Matched, Different }

/// <summary>Canonical registry encodings and units; null is unreadable, never an implicit zero.</summary>
public sealed record StatusSetting(string Kind, string Key, string Unit, string? Value, string? Error = null);
public sealed record StatusCurvePoint(double Millivolts, double EffectiveMhz, double? StockMhz);
public sealed record StatusVerification(DateTimeOffset At, IReadOnlyList<StatusSetting> Settings,
    IReadOnlyList<StatusCurvePoint> Curve, DailyComparison Comparison, string? Error);
public sealed record StatusDevice(string StableId, string Name, string? Driver, string? Vbios,
    DailyProfile? SavedDaily, bool SavedDailyKnown, bool ExperimentKnown, string? SessionGame,
    string? TrialChange, bool OperationInProgress, StatusVerification? Verification,
    FlightSample? Telemetry, IReadOnlyList<string> Errors);

/// <summary>A request snapshot, not a subscription. Revision orders responses within one broker instance only.</summary>
public sealed record TrayStatus(int Version, string InstanceId, long Revision, DateTimeOffset CapturedAt,
    IReadOnlyList<StatusDevice> Devices)
{
    public const int CurrentVersion = 1;
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    public static TrayState State(StatusDevice? device, bool connected, DateTimeOffset capturedAt, DateTimeOffset now)
    {
        if (!connected || device == null) return TrayState.Unavailable;
        // Age the whole snapshot as well as verification: a cached "no trial" is not a live assertion.
        if (now < capturedAt || now - capturedAt > FreshFor) return TrayState.Stale;
        if (device.OperationInProgress) return TrayState.Applying;
        if (device.Errors.Count > 0) return TrayState.Failed;
        if (!device.ExperimentKnown || !device.SavedDailyKnown) return TrayState.Unknown;
        if (device.TrialChange != null) return TrayState.TrialActive;
        if (device.SavedDaily?.Suspended == true) return TrayState.DailySuspended;
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
    public bool Accept(TrayStatus snapshot)
    {
        if (snapshot.Version != TrayStatus.CurrentVersion) { Disconnect("Broker status version is unsupported; update the broker and tray together."); return false; }
        if (string.IsNullOrWhiteSpace(snapshot.InstanceId) || snapshot.Revision < 1 || snapshot.Devices == null || snapshot.Devices.Count > 32
            || snapshot.Devices.Any(d => d == null || string.IsNullOrWhiteSpace(d.StableId) || d.Name == null || d.Errors == null
                || d.SavedDaily is { Writes: null }
                || d.Verification is { } v && (v.Settings == null || v.Curve == null || v.Settings.Count > 65 || v.Curve.Count > 255
                    || v.Settings.Any(s => s == null || s.Kind == null || s.Key == null)
                    || v.Curve.Any(p => p == null || !double.IsFinite(p.Millivolts) || !double.IsFinite(p.EffectiveMhz) || p.StockMhz is { } stock && !double.IsFinite(stock)))))
        { Disconnect("The broker returned malformed status data."); return false; }
        if (Snapshot is { } old && old.InstanceId == snapshot.InstanceId && old.Revision >= snapshot.Revision) return false;
        Snapshot = snapshot;
        Connected = true;
        Error = null;
        return true;
    }
    public void Disconnect(string error) { Connected = false; Error = error; }
}
