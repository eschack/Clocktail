using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;
using Clocktail.Mcp.CurveEditor;

namespace Clocktail.Tray;

internal static class EditorLauncher
{
    public static void Open(Form owner, string gpuId, bool demo)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Clocktail.Tray.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(demo ? "--demo-editor" : "--curve-editor");
        start.ArgumentList.Add(gpuId);
        try { Process.Start(start)?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, e.Message, "Open V/F editor");
        }
    }

    // Each editor owns its lifetime, including a save that is still running when the tray exits.
    public static void Run(string gpuId, bool demo)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gpuId.ToUpperInvariant())));
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset,
            $@"Local\Clocktail.Editor.{identity.User?.Value}.{demo}.{key}", out bool first);
        if (!first) { activate.Set(); return; }
        try
        {
            if (demo) { RunDemo(activate); return; }
            using var card = new Card();
            if (!string.Equals(card.StableId, gpuId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The editor's GPU differs from the selected tray GPU. Refresh the tray and try again.");
            using var residency = new Residency(card);
            using var live = new LiveWatch(card);
            using var closed = new ManualResetEvent(false);
            string? outcome = null;
            var tools = new TuningTools(card, residency, live);
            residency.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                using var reply = JsonDocument.Parse(tools.OpenCurveEditor(result => { outcome = result; closed.Set(); }));
                if (!reply.RootElement.GetProperty("success").GetBoolean())
                    throw new InvalidOperationException(reply.RootElement.GetProperty("error").GetString());
                WaitForClose(closed, activate);
                if (outcome?.StartsWith("the window failed:", StringComparison.Ordinal) == true)
                    MessageBox.Show(outcome, "V/F editor");
            }
            finally { residency.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            MessageBox.Show(e.Message, "Open V/F editor");
        }
    }

    private static void WaitForClose(WaitHandle closed, WaitHandle activate)
    {
        WaitHandle[] signals = [closed, activate];
        while (WaitHandle.WaitAny(signals) == 1) EditorState.Front();
    }

    private static void RunDemo(WaitHandle activate)
    {
        var samples = new DemoSource("matched").Create()!.Devices[0].Verification!.Curve;
        var points = samples.Select((p, i) =>
        {
            uint uv = (uint)(p.Millivolts * 1000), baseline = (uint)(p.StockMhz!.Value * 1000), effective = (uint)(p.EffectiveMhz * 1000);
            return new VfPoint(i, uv, baseline, effective, (int)effective - (int)baseline, uv);
        }).ToArray();
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [], points, 0, [], [], []);
        var data = new EditorData("Synthetic preview · No GPU connection", samples.Select(p => p.Millivolts).ToArray(),
            points.Select(p => (int)p.BaseKhz).ToArray(), curve.Offsets(), points.Length, -1000000, 1000000,
            null, null, [], [], [], null, null, [], null, [], curve);
        using var closed = new ManualResetEvent(false);
        EditorState.TryClaim();
        string? outcome = null;
        CurveEditorForm.Show(data, (_, _) => new(false, null, "Synthetic preview: changes cannot be saved."),
            _ => new([], [], []), result => { outcome = result; EditorState.Closed(result); closed.Set(); });
        WaitForClose(closed, activate);
        if (outcome?.StartsWith("the window failed:", StringComparison.Ordinal) == true)
            MessageBox.Show(outcome, "V/F editor");
    }
}
