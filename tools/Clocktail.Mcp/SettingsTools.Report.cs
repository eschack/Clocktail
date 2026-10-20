using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// "How's my card doing": every setting as original (stock) → now with the change and a safety mark, the live readings
/// with theirs, and one verdict, as rows and as a Markdown table an agent shows as it is. The marks come from fixed rules,
/// not from the agent: ✅ within NVIDIA's own limits for the card (or the range measured on the reference card), ⚠️
/// outside them and worth a look, ⛔ act now. Safety, not stability: a ✅ setting can still crash a game, and stress_test
/// and play decide that. Built from card_settings and limiter, so the numbers are theirs.
/// </summary>
public sealed partial class SettingsTools
{
    public const string Safe = "✅", Look = "⚠️", Act = "⛔", Info = "ℹ️";
    /// <summary>Hotter than this is worth a look (the stock fan curve holds 83 °C); this hot, act.</summary>
    public const double WarmC = 85, HotC = 90;
    /// <summary>What was measured on the reference card: memory +358 MHz, MSVDD REL up to +20 mV, VMIN +50 mV, XBAR up
    /// to +400 MHz (above +250 only with +20 mV of voltage demand).</summary>
    public const double MeasuredMemoryMhz = 358, MeasuredMsvddRelMv = 20, MeasuredVminAboveMv = 50, MeasuredXbarMhz = 400, XbarCliffMhz = 250, XbarCliffDemandMv = 20;
    /// <summary>The ceiling no rail's MAX may pass.</summary>
    public const double CeilingMv = 1150;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The note on a setting whose stock value only the service knows (its journal of originals).</summary>
    private const string StockUnknown = "original unknown: the service keeps it";

    private sealed record ReportRow(string Icon, string Name, string Original, string Now, string Change, string Mark, string Note);

    [McpServerTool(Name = "card_report"), Description(
        "How's my card doing: THE call for 'how is my card', 'is this safe', 'what did we change', 'status'. Every setting " +
        "as original (stock) → now with the change and a safety mark, then live readings (temperature, power, clock and " +
        "voltage, what holds the clock), then one verdict. The marks come from fixed rules: ✅ within NVIDIA's own limits for " +
        "this card (or the range measured on the reference card), so nothing strains the hardware; ⚠️ outside them, worth a " +
        "look; ⛔ act now; ℹ️ information. Safety, not stability: whether it holds in every game is what stress_test and play " +
        "decide. Show 'table' to the person exactly as it is (Markdown with emoji), then add one or two sentences of your own. " +
        "Run it with a game or stress test loaded for meaningful live rows; idle is fine for the settings.")]
    public string CardReport() => Guard(() =>
    {
        using var settingsDoc = JsonDocument.Parse(CardSettings());
        using var limiterDoc = JsonDocument.Parse(WhatLimits());
        var s = settingsDoc.RootElement;
        var l = limiterDoc.RootElement;
        var rows = SettingRows(s);
        var live = LiveRows(s, l);
        var all = rows.Concat(live).ToList();
        var act = all.Where(r => r.Mark == Act).ToList();
        var look = all.Where(r => r.Mark == Look).ToList();
        string verdict = act.Count > 0
            ? $"{Act} Act now: {string.Join(", ", act.Select(r => r.Name.ToLowerInvariant()))}. {string.Join(" ", act.Select(r => r.Note))}"
            : look.Count > 0
                ? $"{Look} {look.Count} thing{(look.Count == 1 ? "" : "s")} to look at: {string.Join("; ", look.Select(r => $"{r.Name.ToLowerInvariant()} ({r.Note})"))}. Everything else is within NVIDIA's own limits."
                : $"{Safe} All good: every setting is within NVIDIA's own limits for this card, and it is running healthy.";
        return Out(new
        {
            gpu = Str(s, "gpu"),
            verdict,
            settings = rows.Select(Plain),
            live = live.Select(Plain),
            table = Table(Str(s, "gpu") ?? "GPU", rows, live, verdict),
        });
    });

    private static object Plain(ReportRow r) => new { setting = r.Name, original = r.Original, now = r.Now, change = r.Change, safe = r.Mark, note = r.Note };

    private static List<ReportRow> SettingRows(JsonElement s)
    {
        var rows = new List<ReportRow>();

        // Power limit: the board's own range is NVIDIA's limit.
        var pw = Obj(s, "power");
        double? limitW = Num(pw, "limit_w"), stockW = Num(pw, "stock_w"), maxW = Num(pw, "board_max_w"), minW = Num(pw, "board_min_w"), dailyW = Num(pw, "daily_w");
        bool powerOk = limitW is { } lw && (maxW is not { } mx || lw <= mx + 0.5) && (minW is not { } mn || lw >= mn - 0.5);
        rows.Add(new("⚡", "Power limit", V(stockW, "W"), V(limitW, "W"), Change(limitW, stockW, "W"), powerOk ? Safe : Look,
            Join(maxW is { } bm ? $"board max {F(bm)} W" : null,
                 dailyW is { } dw && limitW is { } lwNow && Math.Abs(dw - lwNow) > 0.5 ? $"daily {F(dw)} W; a trial set this one" : null,
                 powerOk ? null : "outside the board's own range")));

        // The V/F curve: offsets move clocks along the curve, never the voltage past MAX (the rails row has MAX).
        var vf = Obj(s, "vf_curve");
        double? maxOff = Num(vf, "max_offset_mhz"), minOff = Num(vf, "min_offset_mhz"), points = Num(vf, "offset_points");
        double? topMhz = Num(vf, "top", "mhz"), topMv = Num(vf, "top", "mv");
        bool flat = Bool(vf, "top", "flat") == true;
        string curveNow = points is null or 0 ? "stock"
            : (minOff < 0 && maxOff > 0 ? $"{Sg(minOff)} to {Sg(maxOff)} MHz" : $"up to {Sg(maxOff > 0 ? maxOff : minOff)} MHz") + $" on {F(points)} points";
        string curveChange = maxOff > 0 ? $"⬆️ up to {Sg(maxOff)} MHz" : minOff < 0 ? $"⬇️ {Sg(minOff)} MHz" : "—";
        rows.Add(new("📈", "V/F curve", "stock", curveNow, curveChange, vf == null ? Look : Safe,
            vf == null ? "unreadable" : Join(topMhz is { } tm && topMv is { } tv ? $"tops out at {F(tm)} MHz @ {F(tv)} mV{(flat ? " (flat top)" : "")}" : null,
                "offsets move clocks, never the voltage limit")));

        // The P0 offsets: the core's only when set (the curve is the tool), memory always.
        foreach (var (key, icon, name) in new[] { ("core", "🎯", "Core offset (P0)"), ("memory", "🧠", "Memory offset") })
        {
            var o = Obj(s, "clock_offsets", key);
            double? now = Num(o, "offset_mhz"), stock = Num(o, "stock_mhz") ?? 0, min = Num(o, "min_mhz"), max = Num(o, "max_mhz");
            if (key == "core" && (now ?? 0) == stock) continue;
            bool inRange = now is { } n && (min is not { } a || n >= a) && (max is not { } b || n <= b);
            string note = key == "memory"
                ? now > MeasuredMemoryMhz ? $"above the reference's +{F(MeasuredMemoryMhz)}: this card's own tests decide" : "NVML shows it doubled"
                : "the V/F curve is the better tool for the core";
            rows.Add(new(icon, name, $"{Sg(stock)} MHz", $"{Sg(now)} MHz", Change(now, stock, "MHz"), inRange ? Safe : Look, inRange ? note : "outside the driver's range"));
        }

        // The rails: REL/ALT above 0 is past NVIDIA's own reliability limit; no MAX past the 1150 mV ceiling.
        foreach (var (key, icon, name) in new[] { ("nvvdd", "🔌", "NVVDD rail (core)"), ("msvdd", "🔌", "MSVDD rail (L2, crossbar)") })
        {
            var r = Obj(s, "rails", key);
            string? err = r is { } rv ? Str(rv, "error") : "unreadable";
            if (err != null) { rows.Add(new(icon, name, "?", "?", "?", Look, err)); continue; }
            double? rel = Num(r, "offsets_mv", "rel"), vmin = Num(r, "offsets_mv", "vmin"), alt = Num(r, "offsets_mv", "alt");
            double? relS = Num(r, "stock_offsets_mv", "rel"), vminS = Num(r, "stock_offsets_mv", "vmin"), altS = Num(r, "stock_offsets_mv", "alt");
            double? max = Num(r, "limits_mv", "max");
            var notes = new List<string?>();
            string mark = Safe;
            if (max > CeilingMv) { mark = Look; notes.Add($"MAX {F(max)} mV is past the {F(CeilingMv)} mV ceiling"); }
            if (rel > 0 || alt > Math.Max(0, altS ?? 0))
            {
                mark = Look;
                notes.Add(key == "msvdd" && rel <= MeasuredMsvddRelMv
                    ? $"REL {Sg(rel)} mV is past NVIDIA's own limit (+{F(MeasuredMsvddRelMv)} measured on the reference)"
                    : "raised past NVIDIA's own reliability limit");
            }
            else if (key == "msvdd" && rel == 0 && relS < 0) notes.Add("REL 0 is NVIDIA's own reliability limit");
            else if (rel < relS) notes.Add("undervolted");
            if (vmin - (vminS ?? 0) > MeasuredVminAboveMv) { mark = Look; notes.Add($"VMIN more than +{F(MeasuredVminAboveMv)} mV over stock"); }
            notes.Add(max is { } m ? $"MAX {F(m)} mV" : null);
            bool stockKnown = relS != null && vminS != null;
            if (!stockKnown) notes.Insert(0, StockUnknown);
            string change = !stockKnown ? "?"
                : Join(rel != relS ? $"REL {Arrow(rel - relS)} {Sg(rel - relS)} mV" : null, vmin != vminS ? $"VMIN {Arrow(vmin - vminS)} {Sg(vmin - vminS)} mV" : null) is { Length: > 0 } c ? c : "—";
            rows.Add(new(icon, name, stockKnown ? $"REL {Sg(relS)}, VMIN {Sg(vminS)} mV" : "?", $"REL {Sg(rel)}, VMIN {Sg(vmin)} mV", change, mark, Join([.. notes])));
        }

        // XBAR/SYS/Video: XBAR measured to +400, above +250 only with +20 mV of demand (the cliff); SYS and Video unmeasured.
        foreach (var (key, name) in new[] { ("xbar", "XBAR (crossbar)"), ("sys", "SYS clock"), ("video", "Video clock") })
        {
            var d = Obj(s, "clock_domains", key);
            double? off = Num(d, "offset_mhz"), offS = Num(d, "stock_offset_mhz") ?? 0, dem = Num(d, "vdemand_mv") ?? 0, demS = Num(d, "stock_vdemand_mv") ?? 0, clk = Num(d, "clock_mhz");
            if (key != "xbar" && off == offS && dem == demS) continue;
            string mark = Safe, note;
            if (key != "xbar") { mark = Look; note = "not measured on the reference card"; }
            else if (off > MeasuredXbarMhz) { mark = Look; note = $"past the +{F(MeasuredXbarMhz)} MHz measured; +450 without voltage gave a grey screen elsewhere"; }
            else if (off > XbarCliffMhz && dem < XbarCliffDemandMv) { mark = Look; note = $"the cliff: above +{F(XbarCliffMhz)} MHz it needs +{F(XbarCliffDemandMv)} mV of voltage demand"; }
            else note = off > XbarCliffMhz ? $"+{F(dem)} mV keeps MSVDD off its floor" : off > 0 ? "within the measured range" : "stock";
            string change = Join(off != offS ? $"{Arrow(off - offS)} {Sg(off - offS)} MHz" : null, dem != demS ? $"{Arrow(dem - demS)} {Sg(dem - demS)} mV" : null) is { Length: > 0 } c ? c : "—";
            rows.Add(new("🔀", name, $"{Sg(offS)} MHz, {Sg(demS)} mV", $"{Sg(off)} MHz, {Sg(dem)} mV", change, mark,
                Join(note, clk is { } k && k > 0 ? $"running {F(k)} MHz now" : null)));
        }

        // Voltage boost: no measurable effect on a 5090; shown only when set.
        var vb = Obj(s, "voltage_boost");
        if (Num(vb, "independent_pct") is { } ib && ib != (Num(vb, "stock_independent_pct") ?? 0))
            rows.Add(new("🔋", "Voltage boost", $"{F(Num(vb, "stock_independent_pct") ?? 0)} %", $"{F(ib)} %", Change(ib, Num(vb, "stock_independent_pct") ?? 0, "%"), Safe, "no measurable effect on a 5090"));

        // The fan curve: full speed no later than stock's.
        var curve = Arr(s, "fans", "curves").FirstOrDefault(c => Bool(c, "editable") == true);
        if (curve.ValueKind == JsonValueKind.Object)
        {
            string? now = Str(curve, "curve"), stock = Str(curve, "stock_curve");
            var (nt, nd) = Last(now);
            var (st, _) = Last(stock);
            bool ok = nd is >= 99.5 && (st is not { } stTop || nt <= stTop + 0.01);
            if (stock == null)
                rows.Add(new("🌀", "Fan curve", "?", Short(now), "?", nd is >= 99.5 ? Info : Look,
                    Join(StockUnknown, nd is >= 99.5 ? $"full speed at {F(nt)} °C" : "never reaches full speed")));
            else
                rows.Add(new("🌀", "Fan curve", Short(stock), Short(now),
                    now == stock ? "—" : nt is { } a && st is { } b ? $"full speed at {F(a)} °C (stock {F(b)} °C)" : "changed",
                    ok ? Safe : Look, ok ? (nt < st ? "full speed sooner than stock" : now == stock ? "the card's own" : "full speed no later than stock") : "full speed later than the stock curve"));
        }

        // The fans now: following the curve, or held by a trial.
        var channels = Arr(s, "fans", "channels").Where(c => c.ValueKind == JsonValueKind.Object).ToList();
        if (channels.Count > 0)
        {
            bool manual = channels.Any(c => Str(c, "mode") == "manual");
            double? duty = channels.Select(c => Num(c, "duty_pct")).Max(), rpm = channels.Select(c => Num(c, "rpm")).Max();
            rows.Add(new("🌀", "Fans", "auto", manual ? $"held at {F(duty)} %" : "auto", manual ? "held" : "—", Safe,
                Join(rpm is { } r ? (r == 0 ? "stopped (idle, normal)" : $"{F(rpm)} rpm, {F(duty)} %") : null, manual ? "a trial holds them; back_to_daily gives them back" : null)));
        }

        // Current limits: never above the VBIOS default.
        var cl = Obj(s, "current_limits");
        double? nv = Num(cl, "nvvdd", "limit_a"), nvS = Num(cl, "nvvdd", "stock_a"), ms = Num(cl, "msvdd", "limit_a"), msS = Num(cl, "msvdd", "stock_a");
        if (nv != null || ms != null)
        {
            bool above = nv > nvS + 0.5 || ms > msS + 0.5;
            bool lowered = nv < nvS - 0.5 || ms < msS - 0.5;
            rows.Add(new("🔋", "Current limits", $"NVVDD {F(nvS)} A, MSVDD {F(msS)} A", $"NVVDD {F(nv)} A, MSVDD {F(ms)} A",
                above ? "⬆️ raised" : lowered ? "⬇️ lowered" : "—", above ? Look : Safe,
                above ? "above the VBIOS default" : lowered ? "lowered for a trial" : Join(Num(cl, "nvvdd", "now_a") is { } a ? $"NVVDD drawing {F(a)} A now" : null)));
        }
        return rows;
    }

    private static List<ReportRow> LiveRows(JsonElement s, JsonElement l)
    {
        var rows = new List<ReportRow>();
        var rd = Obj(l, "readings");
        double? temp = Num(rd, "temp_c"), board = Num(rd, "board_w"), limit = Num(rd, "power_limit_w"), clock = Num(rd, "clock_mhz"),
            mv = Num(rd, "nvvdd_mv"), max = Num(rd, "nvvdd_max_mv"), util = Num(rd, "util_pct");
        if (temp is { } t)
            rows.Add(new("🌡️", "Temperature", "", $"{F(t)} °C", "", t >= HotC ? Act : t >= WarmC ? Look : Safe,
                t >= HotC ? "too hot: check the case airflow and the fan curve now" : t >= WarmC ? "warm: the stock fan curve holds 83 °C at the wall" : t < 50 ? "cool" : "healthy"));
        if (board is { } b)
            rows.Add(new("⚡", "Power draw", "", limit is { } lim ? $"{F(b)} W of {F(lim)} W" : $"{F(b)} W", "", Safe,
                Join(util is { } u ? $"{F(u)} % busy" : null, limit is { } li && b >= li - 10 ? "on the power limit" : null)));
        if (clock is { } c)
            rows.Add(new("📈", "Core", "", mv is { } v ? $"{F(c)} MHz @ {F(v)} mV" : $"{F(c)} MHz", "", mv > max + 0.5 ? Look : Safe,
                max is { } m ? $"voltage limit {F(m)} mV" : ""));
        if (Str(l, "limiter") is { } id)
            rows.Add(new(id switch { "power_wall" => "⚡", "thermal" => "🌡️", "idle" => "💤", "curve_cap" => "📈", "voltage_limit" => "🔌", _ => "🧭" },
                "Holding the clock", "", id switch
                {
                    "power_wall" => "the power limit",
                    "power_policy" => "a power sub-limit",
                    "voltage_limit" => "the voltage limit",
                    "curve_cap" => "the top of the V/F curve",
                    "thermal" => "temperature",
                    "hardware_slowdown" => "a hardware slowdown",
                    "idle" => "nothing: it's idle",
                    _ => "nothing: the load asks for less",
                }, "", id is "thermal" or "hardware_slowdown" ? Look : Info, Str(l, "votes") is { } v ? $"{v} samples agree" : ""));

        // What is on the card in the long run: the daily profile, and a trial not yet kept.
        var daily = Obj(s, "daily_profile");
        if (daily is { } dp && Str(dp, "error") != null)
        {
            rows.Add(new("💾", "Daily profile", "", "unknown", "", Look,
                "the Clocktail service does not answer, so nothing can be changed, kept or put back after a crash: install_service (one UAC prompt), or check the ClocktailBroker scheduled task"));
            return rows;
        }
        string profile = daily == null ? "none: the card runs stock" : Bool(daily, "suspended") == true ? "suspended: stock until load_daily"
            : Str(daily.Value, "kept_at") is { Length: >= 10 } kept ? $"kept {kept[..10]}" : "kept";
        rows.Add(new("💾", "Daily profile", "", profile, "", Info, daily != null && Bool(daily, "suspended") != true ? "the service puts it back after every boot and driver reset" : ""));
        if (Bool(s, "trial_in_force") == true)
            rows.Add(new("🧪", "Trial on the card", "", Str(s, "session", "trial", "change") ?? "a trial", "", Info, "not kept yet: keep_as_daily or back_to_daily"));
        return rows;
    }

    private static string Table(string gpu, List<ReportRow> rows, List<ReportRow> live, string verdict)
    {
        var b = new StringBuilder();
        b.AppendLine($"### 🩺 Card report · {gpu}");
        b.AppendLine();
        b.AppendLine("| | Setting | Original | Now | Change | Safe | Note |");
        b.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows) b.AppendLine($"| {r.Icon} | {r.Name} | {r.Original} | {r.Now} | {r.Change} | {r.Mark} | {r.Note} |");
        b.AppendLine();
        b.AppendLine("| | Right now | | Safe | Note |");
        b.AppendLine("|---|---|---|---|---|");
        foreach (var r in live) b.AppendLine($"| {r.Icon} | {r.Name} | {r.Now} | {r.Mark} | {r.Note} |");
        b.AppendLine();
        b.AppendLine($"**{verdict}**");
        b.AppendLine();
        b.Append("_✅ = within NVIDIA's own limits for this card (or the range measured on the reference card): nothing here strains the hardware. " +
                 "Whether it holds in every game is a separate question: stress tests and play answer that._");
        return b.ToString();
    }

    private static JsonElement? Obj(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind == JsonValueKind.Object ? cur : null;
    }

    private static double? Num(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind == JsonValueKind.Number ? cur.GetDouble() : null;
    }

    private static bool? Bool(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return [];
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [];
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.#", Inv) : "?";
    private static string Sg(double? v) => v is { } x ? x.ToString("+0.#;-0.#;0", Inv) : "?";
    private static string V(double? v, string unit) => v is { } ? $"{F(v)} {unit}" : "?";
    private static string Arrow(double? d) => d > 0 ? "⬆️" : d < 0 ? "⬇️" : "";
    private static string Change(double? now, double? stock, string unit) =>
        now is { } n && stock is { } st ? Math.Abs(n - st) < 0.05 ? "—" : $"{Arrow(n - st)} {Sg(n - st)} {unit}" : "?";
    private static string Join(params string?[] parts) => string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"50:20:1100,79:60:1995,86:100:2875" → "50 °C 20 % · 79 °C 60 % · 86 °C 100 %".</summary>
    private static string Short(string? curve) => curve == null ? "?" : string.Join(" · ", curve.Split(',').Select(p =>
    {
        var f = p.Split(':');
        return f.Length >= 2 ? $"{f[0]} °C {f[1]} %" : p;
    }));

    /// <summary>The last point's temperature and duty.</summary>
    private static (double? Temp, double? Duty) Last(string? curve)
    {
        var f = curve?.Split(',').LastOrDefault()?.Split(':');
        return f is { Length: >= 2 } && double.TryParse(f[0], NumberStyles.Float, Inv, out var t) && double.TryParse(f[1], NumberStyles.Float, Inv, out var d)
            ? (t, d) : (null, null);
    }
}
