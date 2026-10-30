using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Game;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class TuningTools
{
    public const int MaxGameMinutes = 240;

    [McpServerTool(Name = "game_test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Start a seeded Quake II RTX game test in the background, in a window covering at least 90 % of screen area. " +
        "Needs the installed game (free on Steam), an open session and an idle, identified compute capability 12.0 GPU. " +
        "The card host verifies PnP/NVML/NVAPI/CUDA/Vulkan identity and reads the actual baseline under the measurement " +
        "lease; clients cannot supply curves or restoration values. Heavy/light transitions, bursts, rendering changes and " +
        "scripted combat exercise graphics loads. Renderer restarts and Boost Lock are opt-in. Default card levers: power " +
        "(only down from the locked baseline) and fans (cold/heat soak/auto). lab_status follows it; lab_stop requests stop. " +
        "Only foreground, drawing, progressing time with settings read back is credited. Reset/crash/hang and repeated NaN " +
        "readbacks are failure signals, not proof of their cause. Allocation failures and inability to test are unjudged. " +
        "Restoration requires independent driver readback; interrupted recovery records survive until verified. No setting " +
        "is kept or marked automatically. Optional, for when nobody is at the PC: on the reference card it reproduced a known " +
        "bad curve's hang within 90 s. When the person will play, their own games under live_start judge better. Tell the " +
        "person first: this takes the screen and foreground and runs the fans at 100 % for a stretch. Their games remain the final judge.")]
    public string GameTest(
        [Description("Minutes of credited play, 1..240 (default 10).")] int minutes = 10,
        [Description("Comma-separated patterns, default (all but restart), or all: heavy, leap, pulse, burst, ramp, drs, cuts, toggles, wide, sky, photo, play, restart.")] string? patterns = null,
        [Description("Card levers: default (power,fans), all, none, or comma-separated power,fans,boost.")] string? gpu = null,
        [Description("Window size lower bound per axis, 90..100; always covers at least 90 % of screen area.")] int windowPct = 90,
        [Description("Maximum internal resolution, 100..200 %. Default is selected from locked-baseline VRAM capacity.")] int? maxScale = null,
        [Description("Repeatable script seed; default random.")] int? seed = null,
        [Description("Thermal stop, 50..88 degrees C; default 85.")] int? maxTempC = null,
        [Description("Purpose of the test, for the log.")] string? note = null) => Guard(() =>
    {
        if (minutes is < 1 or > MaxGameMinutes) throw new ArgumentException("minutes is 1..240");
        if (windowPct is < 90 or > 100 || maxScale is < 100 or > 200) throw new ArgumentException("windowPct is 90..100; maxScale is 100..200");
        int maxC = maxTempC ?? StressMaxC;
        if (maxC is < 50 or > 88) throw new ArgumentException("maxTempC is 50..88");
        var pats = GameCatalog.Parse(patterns, GameCatalog.AllPatterns, GameCatalog.DefaultPatterns, "patterns");
        var levers = GameCatalog.Parse(gpu, GameCatalog.AllLevers, GameCatalog.DefaultLevers, "levers");
        if (pats.Count == 0) throw new ArgumentException("at least one pattern is required");
        string q2 = QuakeLocator.Find() ?? throw new InvalidOperationException("install Quake II RTX from Steam, or set CLOCKTAIL_Q2RTX");
        var running = Process.GetProcessesByName("q2rtx");
        bool already = running.Length > 0;
        foreach (var p in running) p.Dispose();
        if (already) throw new InvalidOperationException("close the existing Quake II RTX first");
        using var starting = StartLock();
        string exe = LabExe() ?? throw new InvalidOperationException("clocktail-lab.exe is not beside the MCP server");
        if (Running() != null || CardTaken()) throw new InvalidOperationException("another lab job is running: lab_status or lab_stop");
        var info = card.Broker(BrokerRequest.OpGame, "info");
        if (!info.Ok || info.Result is not { } ir || !ir.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != GameSession.Protocol)
            throw new InvalidOperationException("the broker lacks guarded game sessions: update Clocktail.Broker.exe and clocktail-lab.exe together");
        var show = card.Broker(BrokerRequest.OpExperiment, "show");
        var session = show.Ok && show.Result is { } sr && sr.TryGetProperty("experiment", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.Deserialize<Experiment>() : null;
        if (session == null || session.Trial?.Ending != null) throw new InvalidOperationException("begin an active session first; resolve pending recovery before testing");
        var tdr = OperatingSystem.IsWindows() ? TdrSettings.Read() : null;
        if (tdr?.Unsafe == true) throw new InvalidOperationException(tdr.Describe());
        int s = seed ?? Random.Shared.Next(1, int.MaxValue);
        var (job, dir, detached) = StartJob(exe, "game", "game-test", new
        {
            stable_id = card.StableId, pci = card.PciBusId, exe = q2, seconds = minutes * 60, seed = s,
            patterns = pats, levers, window_pct = windowPct, max_scale = maxScale, max_temp_c = maxC,
            grace_seconds = tdr?.StressGraceS ?? 45, note, trial_at = session.Trial?.At, session_at = session.StartedAt,
        });
        return Out(new
        {
            started = true, job, dir, minutes, seed = s, patterns = pats, levers, experimental = "offline-validated only",
            detached = detached ? null : StaysInJob,
            next = "lab_status for the locked baseline, results and verified recovery; lab_stop requests stop. Never mark stability with recovery pending",
        });
    });
}
