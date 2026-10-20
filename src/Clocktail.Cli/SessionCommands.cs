using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;

namespace Clocktail.Cli;

/// <summary>
/// One optimizer step: a whole candidate state applied as an ordered list of writes, each through the broker's
/// generic write path (firmware limits, readback, journaled original). The journal is the session: whatever it holds
/// is exactly what `--recover` gives back, after a judge's verdict, a crashed optimizer, or a TDR. So a session needs
/// no state of its own, and a failed step can only end in one place, the journal's originals, the one state that is
/// known without trusting the step that just failed.
/// </summary>
internal static class SessionCommands
{
    public const string Usage = """

        Optimizer session (a candidate state = an ordered list of writes, judged by StabilityLab, then kept or given back):
          --session FILE           apply FILE, a JSON array of {"kind": K, "key": KEY, "value": V} in order, each through the broker
                                   (K is any write path `Clocktail.Broker status` lists: pstates20, power, rails, clockdomain, fan,
                                   vf, boost; V as that path describes it). The first refusal, veto or failure gives back everything
                                   the journal holds and reports which item failed and why. Give back after judging with --recover;
                                   --ownership shows what is held and the boot it was written in.
          --session-enable SLOT    open an off-until-drilled setter (e.g. PowerPolicySetControl) for this session's writes
        """;

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Contains("--session")) return false;

        int? gpuIndex = null; string? gpuId = null; string? file = null; bool takeOver = false;
        var enable = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--take-over": takeOver = true; break;
                case "--session": file = Next(); break;
                case "--session-enable": enable.Add(Next()); break;
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with --session");
            }
        }
        var steps = WriteSession.Load(file!);

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        code = WriteSession.Apply(ctx.Perform, ctx.StableId, steps, takeOver, enable.Count > 0 ? enable.ToArray() : null, out var report);
        Console.WriteLine(JsonSerializer.Serialize(report, CliContext.Json));
        return true;
    }
}
