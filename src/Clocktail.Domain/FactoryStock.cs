using System.Globalization;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Domain;

/// <summary>
/// One setting whose NVIDIA default is known without a record, as the card holds it now: the value stock is
/// (<see cref="Stock"/>), the value now, what the card held before Clocktail first wrote it (<see cref="Original"/>, the
/// journal's) and what the daily profile keeps there (<see cref="Daily"/>, only while it is not suspended), and the value
/// a keep adopted from another tool there (<see cref="Adopted"/>). Values are in the write path's own encoding, so equal
/// strings mean equal settings.
/// </summary>
public sealed record FactoryCell(string Kind, string Key, string Name, string Stock, string? Now, string? Original, string? Daily,
    bool Sibling = false, string? Adopted = null)
{
    public string Id => $"{Kind}/{Key}";

    /// <summary>What Clocktail found on the card: the original it holds, else the value now.</summary>
    public string? Found => Original ?? Now;

    /// <summary>
    /// Another tool's setting (MSI Afterburner, NVIDIA App, nvidia-smi): what Clocktail found is not stock, and neither the
    /// daily profile nor a keep that adopted it accounts for it. Either it is on the card now and Clocktail never wrote it,
    /// or Clocktail wrote over it and owes it back (its original is not stock). A trial over a value the daily profile
    /// keeps or adopted is Clocktail's own: the daily profile is its baseline. <see cref="Sibling"/>: another cell of the
    /// same setting speaks for it.
    /// </summary>
    public bool OtherTool => !Sibling && Found is { } f && f != Stock && f != Daily && f != Adopted;

    /// <summary><paramref name="value"/> (this cell's encoding) in words: "+1000 MHz", "450 W", "70 points, -79 to +510 MHz".</summary>
    public string Describe(string? value) => FactoryStock.Describe(Kind, Key, value);
}

/// <summary>
/// The card against NVIDIA's own settings. Clocktail's journal knows what the card held before Clocktail first wrote a
/// setting, which is stock only when nothing else had changed it: an MSI Afterburner curve on before Clocktail's first
/// write was that setting's "original", reported as stock and given back as stock (a tester, 2026-10-24: a weak model took
/// the undervolt for the default curve, and the stock tool could not clear it). For these settings NVIDIA's default is
/// known without a record: no clock lock, every V/F point offset 0, the P0 core and memory offsets 0, the XBAR, SYS and
/// Video offsets and every voltage demand 0, no voltage boost, the board's default power limit, the fans on their curve.
/// The rails' offsets, the firmware fan curve and the zero-RPM band, the XBAR/core ratio and the current limits are
/// the VBIOS's own and are not known here; the journal stays their only record.
/// </summary>
public static class FactoryStock
{
    /// <summary>
    /// Every setting of the list above this card and driver have, in the order a write to stock goes: clock locks first, the
    /// curve before the P0 offsets (a P0 write over a V/F curve is refused), the clock offsets before the voltage demands
    /// that carry them, the boost, the wall, the fans. A setting that cannot be read here is left out.
    /// </summary>
    /// <param name="original">The journal's original for kind/key, null where Clocktail holds nothing.</param>
    /// <param name="daily">The daily profile's value for kind/key, null where it keeps none or is suspended.</param>
    /// <param name="adopted">The value a keep adopted from another tool for kind/key (DailyProfile.Adopted), null where none.</param>
    public static IReadOnlyList<FactoryCell> Survey(NvClient hw, GpuSession gpu, Func<string, string, string?> original, Func<string, string, string?> daily,
        Func<string, string, string?>? adopted = null)
    {
        var cells = new List<FactoryCell>();
        void Add(string kind, string key, string name, Func<string> stock)
        {
            try
            {
                string value = stock();
                var cell = WriteRegistry.Find(kind)!.Prepare(hw, gpu, key, value, WriteOptions.Recovery).Cell;
                if (OwnedWriter.Look(hw, gpu, cell) is not { } now) return;
                // No FE request limits nothing below the board's default: stock, and one the driver can neither make nor delete here.
                if (kind == PowerCap.Kind && now == PowerCap.Absent) return;
                cells.Add(new FactoryCell(kind, cell.Key, name, value, now, original(kind, cell.Key), daily(kind, cell.Key), Adopted: adopted?.Invoke(kind, cell.Key)));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidDataException
                                          or InvalidOperationException or NotSupportedException or NvApiException) { }
        }

        Add(ClockLimitWrites.Kind, ClockLimitWrites.MaxKey, "clock cap", () => "0");
        Add(ClockLimitWrites.Kind, ClockLimitWrites.MinKey, "clock floor", () => "0");
        Add(ClockLimitWrites.Kind, ClockLimitWrites.VoltageKey, "V/F point lock", () => "0");
        Add(VfWrites.Kind, VfWrites.CoreKey, "V/F curve", () => VfOffsets.Encode(new int[VfReader.Read(hw, gpu).EditableCount]));
        Add(ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Core), "core offset (P0)", () => "0");
        Add(ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Memory), "memory offset", () => "0");
        foreach (var d in new[] { ClockDomainId.Xbar, ClockDomainId.Sys, ClockDomainId.Video })
            Add(ClockDomainOffsets.Kind, ClockDomainOffsets.Key(d), $"{Domain(d)} offset", () => "0");
        foreach (var d in new[] { ClockDomainId.Graphics, ClockDomainId.Xbar, ClockDomainId.Sys, ClockDomainId.Video })
            Add(ClockVdemand.Kind, ClockVdemand.Key(d), $"{Domain(d)} voltage demand", () => "0");
        // The boost's own path first (BoostKey): a write to stock goes through it, as set_voltage_boost's does.
        foreach (var key in BoostKey(hw, gpu) == "independent" ? new[] { "independent", "legacy" } : ["legacy", "independent"])
            Add(VoltBoost.Kind, key, key == "independent" ? "voltage boost" : "voltage boost (legacy)", () => "0");
        Add(PowerCap.Kind, PowerCap.Key(PowerRequestKind.Ordinary), "power limit",
            () => PowerRequestsReader.ReadBoard(hw, gpu).DefaultMw.ToString(CultureInfo.InvariantCulture));
        IEnumerable<uint> fans;
        try { fans = FanReader.Snapshot(hw, gpu).Channels.Select(c => c.Id).ToList(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NvApiException) { fans = []; }
        foreach (var id in fans)
            Add(FanControl.Kind, FanControl.Key(id), $"fan {id}", () => "auto");
        return Boost(cells);
    }

    /// <summary>
    /// The voltage boost is one setting the driver shows through two APIs: on the reference card the daily profile's 100 %
    /// through the legacy byte reads 100 % on the independent API too (2026-10-24), which the survey took for another tool's.
    /// A cell of it Clocktail neither holds nor keeps is spoken for by the other one when that one is held or kept, and
    /// another tool's boost is named once, by the first. Both stay in the list: a write to stock looks at each again.
    /// </summary>
    public static List<FactoryCell> Boost(List<FactoryCell> cells)
    {
        var boost = cells.Where(c => c.Kind == VoltBoost.Kind).ToList();
        if (boost.Count < 2) return cells;
        static bool Ours(FactoryCell c) => c.Original != null || c.Daily != null || c.Adopted != null;
        return [.. cells.Select(c => c.Kind != VoltBoost.Kind || Ours(c) ? c
            : boost.Any(o => o != c && Ours(o)) || boost.TakeWhile(o => o != c).Any(o => o.OtherTool) ? c with { Sibling = true } : c)];
    }

    /// <summary>The boost's key on this card, the path set_voltage_boost writes (BoostObservation.DefaultRule): the legacy
    /// byte on an RTX 5090, whose NVVDD is kind 3. Null when the card has no boost path.</summary>
    public static string? BoostKey(NvClient hw, GpuSession gpu)
    {
        try
        {
            return BoostReader.Observe(hw, gpu).DefaultRule(railsWritable: true) switch
            {
                BoostPath.Independent => "independent",
                BoostPath.Legacy => "legacy",
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NvApiException) { return null; }
    }

    private static string Domain(ClockDomainId d) => d switch
    {
        ClockDomainId.Graphics => "core",
        ClockDomainId.Xbar => "XBAR",
        ClockDomainId.Sys => "SYS",
        ClockDomainId.Video => "Video",
        _ => d.ToString(),
    };

    /// <summary>A value of kind/key in words; "?" when there is none or it does not parse.</summary>
    public static string Describe(string kind, string key, string? value)
    {
        if (value == null) return "?";
        try
        {
            return kind switch
            {
                VfWrites.Kind => Curve(VfOffsets.Decode(value)),
                ClockOffsets.Kind or ClockDomainOffsets.Kind => Signed(long.Parse(value, CultureInfo.InvariantCulture) / 1000.0, "MHz"),
                ClockVdemand.Kind => Signed(long.Parse(value, CultureInfo.InvariantCulture) / 1000.0, "mV"),
                VoltBoost.Kind => $"{value} %",
                PowerCap.Kind => PowerCap.Decode(value) is { } mw ? $"{(mw / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} W" : "none",
                FanControl.Kind => value == "auto" ? "on its curve" : $"held at a fixed duty ({value})",
                ClockLimitWrites.Kind => Lock(key, uint.Parse(value, CultureInfo.InvariantCulture)),
                _ => value,
            };
        }
        catch (Exception e) when (e is FormatException or OverflowException) { return value; }
    }

    private static string Signed(double v, string unit) => $"{v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} {unit}";

    private static string Curve(int[] offsets)
    {
        int set = offsets.Count(o => o != 0);
        if (set == 0) return "stock";
        double lo = offsets.Min() / 1000.0, hi = offsets.Max() / 1000.0;
        string range = lo < 0 && hi > 0 ? $"{lo.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} to {Signed(hi, "MHz")}" : Signed(hi > 0 ? hi : lo, "MHz");
        return $"{set} of {offsets.Length} points offset, {range}";
    }

    private static string Lock(string key, uint value) => value == 0 ? "none" : key switch
    {
        ClockLimitWrites.VoltageKey when value == 1_500_000 => "Boost Lock",
        ClockLimitWrites.VoltageKey => $"locked to the point at {(value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} mV",
        ClockLimitWrites.MinKey => $"at least {(value / 1000.0).ToString("0", CultureInfo.InvariantCulture)} MHz",
        _ => $"at most {(value / 1000.0).ToString("0", CultureInfo.InvariantCulture)} MHz",
    };
}
