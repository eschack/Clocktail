using System.Globalization;

namespace Clocktail.Domain.Rails;

/// <summary>One slot of a hand-written rail request: Relative adds <paramref name="Uv"/> to the current offset, otherwise it is the offset.</summary>
public readonly record struct RailEdit(RailSlot Slot, int Uv, bool Relative);

/// <summary>
/// Hand-written rail requests resolved against the card's current offsets. An absolute offset reads like a delta
/// and is not one: stock MSVDD REL is already −50 mV on the 5090, so "msvdd:rel=-25" raised it by 25 mV (row 11v,
/// 2026-10-18). The CLI therefore shows every slot as before → after and refuses a raise unless it is asked for.
/// </summary>
public static class RailEdits
{
    public static RailOffsets Apply(RailOffsets current, IEnumerable<RailEdit> edits)
    {
        var o = new[] { current.RelUv, current.AltUv, current.OvUv, current.VminUv };
        foreach (var e in edits)
            o[(int)e.Slot] = e.Relative ? checked(o[(int)e.Slot] + e.Uv) : e.Uv;
        return new RailOffsets(o[0], o[1], o[2], o[3]);
    }

    /// <summary>The request value for the named slots only ("rel=…,vmin=…", µV), so slots not named stay as they are.</summary>
    public static string Encode(RailOffsets target, IEnumerable<RailEdit> edits) =>
        string.Join(",", edits.Select(e => e.Slot).Distinct().Select(s =>
            string.Create(CultureInfo.InvariantCulture, $"{s.ToString().ToLowerInvariant()}={target[s]}")));

    /// <summary>Each named slot as "rel -50 -> -25 mV (delta +25)", ASCII so any console shows it.</summary>
    public static IEnumerable<string> Describe(RailOffsets before, RailOffsets after, IEnumerable<RailEdit> edits) =>
        edits.Select(e => e.Slot).Distinct().Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.ToString().ToLowerInvariant()} {before[s] / 1000m} -> {after[s] / 1000m} mV (delta {(after[s] - before[s]) / 1000m:+0.###;-0.###;0})"));

    /// <summary>The slots whose offset goes up: every rail offset is a voltage, so up is a raise.</summary>
    public static IReadOnlyList<RailSlot> Raised(RailOffsets before, RailOffsets after) =>
        Enum.GetValues<RailSlot>().Where(s => after[s] > before[s]).ToList();
}
