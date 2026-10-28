using System.Globalization;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Domain.Broker;

public enum AppliedType { Unknown, OffsetMhz, VoltageMv, Percent, Watts, RailSlots, VfCurve, FanCurve, FanBand, FanMode, Ratio, CurrentAmps, ClockMhz, LockMv }
public enum AppliedReference { Unknown, Firmware, DocumentedNeutral, CapturedOriginal, AdoptedBaseline }
public enum AppliedModification { Unchanged, Modified, Unconfirmed, Unavailable, Pending }
public enum AppliedReadClass { Light, Full }
public sealed record AppliedDescriptor(string Kind, string Group, int Order, AppliedType Type, string Keys);
public sealed record AppliedRow(string Id, string Kind, string Key, string? Child, string Group, string Label, AppliedType Type,
    bool Saved, string? SavedValue, bool DailyEligible, bool Suspended, string? CurrentValue, string? LastValue,
    DateTimeOffset? SampledAt, DateTimeOffset? AttemptAt, long? SampleGeneration, string? Error,
    string? ReferenceValue, AppliedReference Reference, AppliedModification Modification, DailyComparison Daily,
    AppliedReadClass ReadClass, string? Context = null);
public sealed record AppliedSettings(string InstanceId, long Generation, DateTimeOffset? InventoryAt, bool Complete,
    int CandidateCount, int OmittedCount, IReadOnlyList<string> Coverage, IReadOnlyList<AppliedRow> Rows, string? GpuId = null,
    int? TotalRows = null, string? NextCursor = null);

/// <summary>Linked to WriteRegistry by an exhaustive test. No setters, plans or prose-based key discovery.</summary>
public static class AppliedCatalog
{
    public const int MaxCells = 512, MaxRows = 768, MaxValueChars = 4096;
    public static readonly AppliedDescriptor[] All =
    [new("pstates20", "Core and memory", 0, AppliedType.OffsetMhz, "core,memory"),
     new("vf", "Core and memory", 1, AppliedType.VfCurve, "core"),
     new("power", "Power and rails", 10, AppliedType.Watts, "fe,f8"),
     new("rails", "Power and rails", 11, AppliedType.RailSlots, "nvvdd,msvdd"),
     new("boost", "Power and rails", 12, AppliedType.Percent, "legacy,independent"),
     new("ocp", "Power and rails", 13, AppliedType.CurrentAmps, "nvvdd,msvdd"),
     new("clockdomain", "Uncore", 20, AppliedType.OffsetMhz, "xbar,sys,video"),
     new("vdemand", "Uncore", 21, AppliedType.VoltageMv, "graphics,xbar,sys,video"),
     new("clockratio", "Uncore", 22, AppliedType.Ratio, "msvdd"),
     new("fan", "Fans and limits", 30, AppliedType.FanMode, "channel ID from fan topology"),
     new("fancurve", "Fans and limits", 31, AppliedType.FanCurve, "editable policy ID from topology"),
     new("fanstop", "Fans and limits", 32, AppliedType.FanBand, "editable policy ID from topology"),
     new("clocklimit", "Fans and limits", 33, AppliedType.ClockMhz, "max,min,voltage")];
    public static AppliedDescriptor? Find(string kind) => All.FirstOrDefault(d => d.Kind == kind);
    public static bool KeyKnown(string kind, string key) => Find(kind) is { } d && (kind is "fan" or "fancurve" or "fanstop"
        ? uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && (kind == "fan" || id < 32)
        : d.Keys.Split(',').Contains(key));
    public static AppliedType Type(string kind, string key, string? child = null) => child != null ? AppliedType.VoltageMv
        : kind == "clocklimit" && key == "voltage" ? AppliedType.LockMv : Find(kind)?.Type ?? AppliedType.Unknown;
    public static string Label(string kind, string key, string? child = null) => (kind switch
    {
        "pstates20" => key == "core" ? "Core offset (P0)" : "Memory offset",
        "vf" => "V/F point curve", "power" => key == "fe" ? "Ordinary power request (FE)" : "Additional power request (F8)",
        "rails" => key.ToUpperInvariant() + " rail", "boost" => "Voltage boost · " + key,
        "ocp" => key.ToUpperInvariant() + " current limit", "clockdomain" => key.ToUpperInvariant() + " clock offset",
        "vdemand" => key.ToUpperInvariant() + " voltage demand", "clockratio" => "XBAR/core relationship",
        "fan" => "Fan channel " + key, "fancurve" => "Fan policy " + key + " curve",
        "fanstop" => "Fan policy " + key + " stop/start", "clocklimit" => key switch { "max" => "Maximum clock", "min" => "Minimum clock", _ => "Voltage / Boost Lock" },
        _ => kind + "/" + key
    }) + (child == null ? "" : " · " + child.ToUpperInvariant());

    public static Dictionary<string, string> Slots(string value) => value.Split(',', StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries)).ToDictionary(p => p.Length == 2 ? p[0].ToLowerInvariant() : throw new FormatException("Expected named rail slots"), p => p[1]);
    public static bool? Equal(string kind, string key, string? observed, string? target)
    {
        if (observed == null || target == null) return null;
        try
        {
            if (kind == "rails") { var a = Slots(observed); var b = Slots(target); return b.Count > 0 && b.All(p => new[] { "rel", "alt", "ov", "vmin" }.Contains(p.Key) && a.TryGetValue(p.Key, out var v) && Number(v) == Number(p.Value)); }
            if (kind == "vf") return VfOffsets.Decode(observed).SequenceEqual(VfOffsets.Decode(target));
            if (kind == "fancurve")
            {
                var a = FanCurveText.ParseExact(observed); var b = FanCurveText.Parse(target);
                return a.Count == b.Count && a.Zip(b).All(p => p.First.Temp == p.Second.Temp && p.First.Duty == p.Second.Duty && (p.Second.Rpm == null || p.First.Rpm == p.Second.Rpm));
            }
            if (kind == "fanstop") return FanStopWrites.Parse(observed) is { } band && band == FanStopWrites.Parse(target);
            if (kind == "fan") return FanState.Parse(observed) == FanState.Parse(target);
            if (kind == "clocklimit") return Number(observed == "off" ? "0" : observed) == Number(target == "off" ? "0" : target);
            if (kind == "clockratio") return RatioRaw(observed) == RatioRaw(target);
            if (kind == "power" && target.StartsWith("non-limiting:", StringComparison.Ordinal)) return observed == "absent" || Number(observed) >= Number(target[13..]);
            if (kind == "power" && (observed == "absent" || target == "absent")) return observed == target;
            if (Find(kind) == null) return null;
            return Number(observed) == Number(target);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException) { return null; }
    }
    private static decimal Number(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static decimal RatioRaw(string s) => s.Contains('.') ? decimal.Round(Number(s) * 65536) : Number(s);
    public static string Format(AppliedType type, string? value)
    {
        if (value == null) return "Unavailable";
        try
        {
            string N(decimal n, string f = "0.###") => n.ToString(f, CultureInfo.InvariantCulture);
            if (type == AppliedType.Unknown) return value;
            if (type == AppliedType.Watts && value == "absent") return "Absent · no request";
            if (type == AppliedType.Watts && value.StartsWith("non-limiting:")) return "Non-limiting (≥ " + N(Number(value[13..]) / 1000) + " W)";
            if (type is AppliedType.ClockMhz or AppliedType.LockMv && value is "0" or "off") return "Off · released";
            return type switch
            {
                AppliedType.OffsetMhz => N(Number(value) / 1000, "+0.###;-0.###;0") + " MHz",
                AppliedType.VoltageMv => N(Number(value) / 1000, "+0.###;-0.###;0") + " mV",
                AppliedType.Watts => N(Number(value) / 1000) + " W", AppliedType.Percent => N(Number(value)) + "%",
                AppliedType.CurrentAmps => N(Number(value) / 1000) + " A", AppliedType.Ratio => N(RatioRaw(value) / 65536, "0.0000") + "×",
                AppliedType.ClockMhz => N(Number(value) / 1000) + " MHz", AppliedType.LockMv => Number(value) == 1500000 ? "Boost Lock · 1500 mV" : N(Number(value) / 1000) + " mV",
                AppliedType.FanMode => FanState.Parse(value) is { Manual: true } fan ? "Manual · level " + fan.Level : "Automatic",
                AppliedType.FanBand => value.Replace(':', '/') + " °C",
                AppliedType.FanCurve => "3 points · " + string.Join(" → ", FanCurveText.Parse(value).Select(p => N(p.Temp / 256m) + " °C / " + N(p.Duty * 100m / 65536) + "%")),
                AppliedType.VfCurve => Curve(value), AppliedType.RailSlots => string.Join(" · ", Slots(value).Select(p => p.Key.ToUpperInvariant() + " " + N(Number(p.Value) / 1000) + " mV")),
                _ => value
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException) { return "Unrecognized value · " + value; }
    }
    private static string Curve(string value)
    {
        var points = VfOffsets.Decode(value);
        return points.Distinct().Count() == 1 ? $"Uniform · {points.Length} points · {Format(AppliedType.OffsetMhz, points[0].ToString(CultureInfo.InvariantCulture))}"
            : $"Shaped · {points.Length} points · {points.Min() / 1000.0:0.###}…{points.Max() / 1000.0:0.###} MHz";
    }
}
