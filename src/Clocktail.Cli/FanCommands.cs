using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Fan;
using Clocktail.Hw;

namespace Clocktail.Cli;

/// <summary>--fans (read) and --fan ID=LEVEL|auto (write); --fan-policy (read) and --fan-curve CURVE|stock (write) for the
/// firmware's own curve. Kept apart from Program so the fan thread owns it.</summary>
internal static class FanCommands
{
    /// <summary>"1=55", "1:55", "1=auto", "all=auto".</summary>
    public static (uint? Id, FanState State) ParseRequest(string text)
    {
        var p = text.Split('=', ':');
        if (p.Length != 2) throw new ArgumentException($"--fan needs ID=LEVEL or ID=auto, not '{text}'.");
        uint? id = p[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? null
            : uint.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v
            : throw new ArgumentException($"'{p[0]}' is not a fan channel id (or 'all').");
        if (p[1].Equals("auto", StringComparison.OrdinalIgnoreCase)) return (id, FanState.Auto);
        if (!uint.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level > FanLayout.LevelCeiling)
            throw new ArgumentException($"'{p[1]}' is not a fan level (a whole number, or 'auto').");
        return (id, new FanState(true, level));
    }

    public static int Read(CliContext ctx)
    {
        var (hw, session, mgr, gpu) = (ctx.Hw, ctx.Session, ctx.Mgr, ctx.Gpu);
        var info = FanReader.Read(hw, session, Slot.ClientFanCoolersGetInfo);
        var snap = FanReader.Snapshot(hw, session);
        var nvml = mgr.Nvml != null && gpu.HasNvml ? Telemetry.Read(gpu, mgr.Nvml) : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            info_ids = FanParser.ParseInfoIds(info),
            channels = snap.Channels.Select(c => new
            {
                id = c.Id, state = c.Control.State.ToString(), control_level = c.Control.Level, control_mode = $"0x{c.Control.Mode:x}",
                min_level = c.Status.MinLevel, max_level = c.Status.MaxLevel, status_level = c.Status.Level, reading = c.Status.Reading,
            }),
            nvml_fan_percent = nvml?.FanPercent,
            // Raw blocks up to the last live entry, so the layout can be checked against the notes byte by byte.
            info_raw = Convert.ToHexString(info.AsSpan(0, FanLayout.InfoEntry(Math.Max(1, (int)info[FanLayout.InfoCount])))),
            status_raw = Convert.ToHexString(snap.StatusRaw.AsSpan(0, FanLayout.StatusEntry(Math.Max(1, snap.Channels.Count)))),
            control_raw = Convert.ToHexString(snap.ControlRaw.AsSpan(0, FanLayout.ControlEntry(Math.Max(1, snap.Channels.Count)))),
        }, CliContext.Json));
        return 0;
    }

    /// <summary>
    /// One broker write per channel: kind "fan", key = channel id, value = "auto" | "manual:LEVEL". The firmware
    /// [min, max] is checked by the plan on the broker's side against a fresh Status read; "all" expands here from
    /// a read-only snapshot. One failure stops the list, so no write lands on an uncertain state.
    /// </summary>
    public static int Apply(CliContext ctx, IReadOnlyList<(uint? Id, FanState State)> requests, bool takeOver)
    {
        var ids = FanReader.Snapshot(ctx.Hw, ctx.Session).Channels.Select(c => c.Id).ToList();
        var reqs = requests.SelectMany(r => (r.Id is { } one ? [one] : ids)
            .Select(id => ctx.WriteRequest(FanControl.Kind, FanControl.Key(id), r.State.ToString(), takeOver)));
        return ctx.PrintAll(reqs);
    }

    /// <summary>"50:20,79:60,86:100" (°C:duty%, rpm from the policy's own pairing), "T:D:R,…", or "stock"; checked here
    /// for its shape so a typo fails before the broker is asked.</summary>
    public static string ParseCurve(string text)
    {
        if (FanCurveText.IsStock(text)) return FanCurveText.Stock;
        try { return FanCurveText.Format(FanCurveText.Parse(text)); }
        catch (FormatException e) { throw new ArgumentException($"--fan-curve: {e.Message}"); }
    }

    /// <summary>
    /// The firmware fan policies: per policy its type, link, whether it is editable, its points (exact, and as the
    /// canonical curve text) and what Status reports for it; the fan channels' modes beside them, since a manual channel
    /// overrides every policy; NVML's GPU temperature and fan %, to tell which sensor a policy follows.
    /// </summary>
    public static int ReadPolicy(CliContext ctx)
    {
        var (hw, session, mgr, gpu) = (ctx.Hw, ctx.Session, ctx.Mgr, ctx.Gpu);
        var snap = FanPolicyReader.Read(hw, session);
        var coolers = FanReader.Snapshot(hw, session);
        var nvml = mgr.Nvml != null && gpu.HasNvml ? Telemetry.Read(gpu, mgr.Nvml) : null;
        static double R(double v) => Math.Round(v, 3);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            info_mask = $"0x{snap.Info.Mask:x}",
            policies = snap.Info.Policies.Select(p =>
            {
                var c = snap.Policies.FirstOrDefault(x => x.Index == p.Index);
                var s = snap.Status.FirstOrDefault(x => x.Index == p.Index);
                return new
                {
                    index = p.Index, type = p.Type, link = p.Link, editable = p.Editable,
                    curve = c?.Curve,
                    points = c?.Points.Select(x => new { temp_c = x.TempC, duty_pct = Math.Round(x.DutyPct, 3), rpm = x.Rpm, raw = $"0x{x.Temp:x}/0x{x.Duty:x}/{x.Rpm}" }),
                    // Status, [inferred]: see FanPolicyLayout.StatusType.
                    status = s == null ? null : new
                    {
                        temperature_c = R(s.TemperatureC), curve_temperature_c = R(s.CurveTemperatureC), target_rpm = s.TargetRpm,
                        filtered_temperature_c = R(s.FilteredTemperatureC),
                        word_2c = $"0x{s.Word2c:x}", word_38 = $"0x{s.Word38:x}", word_3c = $"0x{s.Word3c:x}",
                    },
                    control_raw = Convert.ToHexString(snap.ControlRaw.AsSpan(FanPolicyLayout.ControlEntry(p.Index), FanPolicyLayout.ControlStride)),
                    status_raw = Convert.ToHexString(snap.StatusRaw.AsSpan(FanPolicyLayout.StatusEntry(p.Index), FanPolicyLayout.StatusStride)),
                };
            }),
            // A channel in manual mode runs at its fixed level whatever the policies say (--fan ID=auto gives it back).
            fan_channels = coolers.Channels.Select(c => new { id = c.Id, state = c.Control.State.ToString(), status_level = c.Status.Level, reading = c.Status.Reading }),
            nvml_temperature_c = nvml?.TemperatureC,
            nvml_fan_percent = nvml?.FanPercent,
        }, CliContext.Json));
        return 0;
    }

    /// <summary>
    /// One broker write per editable policy: kind "fancurve", key = policy index, the same curve for each. The rules
    /// (editable, type, points) are checked by the plan on the broker's side against a fresh read; the editable set is
    /// taken here from a read-only Info read. One failure stops the list.
    /// </summary>
    public static int ApplyCurve(CliContext ctx, string curve, bool takeOver)
    {
        var editable = FanPolicyReader.ReadInfo(ctx.Hw, ctx.Session).Editable.Select(p => p.Index).ToList();
        if (editable.Count == 0) throw new InvalidOperationException("this GPU has no editable fan policy");
        return ctx.PrintAll(editable.Select(i => ctx.WriteRequest(FanPolicyWrites.Kind, FanPolicyWrites.Key(i), curve, takeOver)));
    }
}
