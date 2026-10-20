using System.Diagnostics;
using Microsoft.Win32;

namespace Clocktail.Mcp;

/// <summary>
/// The driver's own golden stressor, the one NVIDIA App's auto-tune runs: graphics, compute
/// and ray tracing, every result compared bit for bit with the first, so a silent error is caught where a game shows
/// nothing. It only loads the GPU; it writes no setting. Started exactly as _nvtopps starts it: -b compare interval ms,
/// -r run ms, -l load (≥ 100 continuous; under 100 a 250 Hz square wave at that duty).
/// </summary>
public static class Nvgwls
{
    /// <summary>The nvgwls.exe of the driver that is loaded: the folder nvlddmkm's image path points at.</summary>
    public static string? Find()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? image = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm", "ImagePath", null) as string;
        if (image != null)
        {
            string path = image.Replace(@"\SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                               .Replace(@"\??\", "", StringComparison.Ordinal);
            if (Path.GetDirectoryName(path) is { } dir && Path.Combine(dir, "Display.NvContainer", "nvgwls.exe") is var exe && File.Exists(exe))
                return exe;
        }
        // Fallback: the newest copy in the driver store.
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        return Directory.Exists(store)
            ? Directory.EnumerateDirectories(store, "nv_dispi.inf_amd64_*")
                .Select(d => Path.Combine(d, "Display.NvContainer", "nvgwls.exe")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    /// <summary>The exit codes as _nvtopps reads them; null is a run that did not end within its time plus 30 s.</summary>
    public static string Verdict(int? code) => code switch
    {
        0 => "pass",
        1 => "cancelled",
        2 => "runtime error (the stressor could not run)",
        3 => "TDR",
        4 => "the stressor crashed",
        5 => "RC / Xid error",
        6 => "data error (a result differed from the reference: silent corruption)",
        null => "hang (no exit within the run time + 30 s; killed)",
        _ => $"exit {code}",
    };

    /// <summary>A failure of the card, not of the run: what counts against the settings under test.</summary>
    public static bool Failed(int? code) => code is null or 3 or 4 or 5 or 6;

    /// <summary>Runs it hidden and calls <paramref name="sample"/> every 500 ms until it exits or overruns.</summary>
    public static int? Run(string exe, int load, int seconds, Action sample)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clocktail-stress");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir,
        };
        foreach (var a in new[] { "-b", "500", "-r", (seconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), "-l", load.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("nvgwls did not start");
        var deadline = DateTime.UtcNow.AddSeconds(seconds + 30);
        while (!p.WaitForExit(500))
        {
            sample();
            if (DateTime.UtcNow > deadline)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
        }
        return p.ExitCode;
    }
}
