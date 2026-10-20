using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Hw;

namespace Clocktail.Cli;

/// <summary>
/// Read-only raw dumps of one undecoded readable slot, sampled several times with NVML's board power beside each
/// sample. A field that tracks NVML's total across load changes is a power channel; one that sums with its
/// neighbours to the total is a rail of it. This is how the power topology is decoded: by correlation on the live
/// card, never by guessing a layout. Setters cannot be reached from here (`CallRaw` refuses them).
/// </summary>
internal static class DumpCommands
{
    public const string Usage = """

        Raw read (read-only, for decoding undocumented structures):
          --dump SLOT              read SLOT (a readable slot name, e.g. ClientPowerTopologyGetStatus) and print the whole buffer
          --dump-size HEX          structure size (find it with --probe-size); default: the slot's known size
          --dump-rev N             structure revision (default 1, or the known one)
          --dump-set OFF=HEX       put a dword into the buffer before the call (an input mask); repeatable
          --dump-count N           samples (default 1), each with NVML board power and the call's duration
          --dump-interval-ms MS    time between samples (default 1000)
        """;

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Contains("--dump")) return false;

        int? gpuIndex = null; string? gpuId = null; string? slotName = null;
        int? size = null, rev = null; int count = 1, interval = 1000;
        var sets = new List<(int Off, uint Value)>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--dump": slotName = Next(); break;
                case "--dump-size": size = Convert.ToInt32(Next().Replace("0x", ""), 16); break;
                case "--dump-rev": rev = int.Parse(Next()); break;
                case "--dump-count": count = int.Parse(Next()); break;
                case "--dump-interval-ms": interval = int.Parse(Next()); break;
                case "--dump-set":
                    var kv = Next().Split('=', 2);
                    if (kv.Length != 2) throw new ArgumentException("--dump-set takes OFF=HEX");
                    sets.Add((Convert.ToInt32(kv[0].Replace("0x", ""), 16), Convert.ToUInt32(kv[1].Replace("0x", ""), 16)));
                    break;
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with --dump");
            }
        }
        if (!Enum.TryParse<Slot>(slotName, ignoreCase: true, out var slot) || !Enum.IsDefined(slot))
            throw new ArgumentException($"'{slotName}' is not a slot name");
        var info = Slots.Info(slot);
        if (info.Access != SlotAccess.Read) throw new ArgumentException($"{slot} is not a readable slot");
        int sz = size ?? info.Shape?.Size ?? throw new ArgumentException($"{slot} has no known size; pass --dump-size");
        int rv = rev ?? info.Shape?.Rev ?? 1;

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        var samples = new List<object>();
        for (int k = 0; k < count; k++)
        {
            if (k > 0) Thread.Sleep(interval);
            var b = new byte[sz];
            BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(sz, rv).Word);
            foreach (var (off, v) in sets) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
            uint? nvmlMw = null;
            try { if (ctx.Gpu.HasNvml) nvmlMw = mgr.Nvml?.PowerUsageMw(ctx.Gpu.NvmlHandle); } catch (NvmlException) { }
            var sw = Stopwatch.StartNew();
            var r = ctx.Hw.CallRaw(ctx.Session, slot, b);
            double ms = sw.Elapsed.TotalMilliseconds;
            samples.Add(new
            {
                at = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"),
                outcome = r.Outcome.ToString(),
                status = r.Status,
                call_ms = Math.Round(ms, 3),
                nvml_power_mw = nvmlMw,
                hex = r.IsOk ? Convert.ToHexString(b) : null,
            });
            if (!r.IsOk) { code = 1; break; }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { slot = slot.ToString(), id = $"0x{info.Id:x8}", size = $"0x{sz:x}", rev = rv, samples }, CliContext.Json));
        return true;
    }
}
