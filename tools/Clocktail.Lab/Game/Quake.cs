using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Clocktail.Lab.Game;

/// <summary>
/// The installed Quake II RTX: where it is, what data it has, and how it is started for a test. Nothing in the installation
/// is changed: the game runs with its own home (configs, saves, logs) under the job's folder, so the person's q2config.cfg
/// and autoexec.cfg are neither read nor written.
/// </summary>
public static class QuakeInstall
{
    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The names in the game's .pak files (baseq2\*.pak, Quake's own archive: a 12-byte header, then 64-byte entries).</summary>
    public static IReadOnlyList<string> PakEntries(string baseq2)
    {
        var names = new List<string>();
        if (!Directory.Exists(baseq2)) return names;
        foreach (var pak in Directory.GetFiles(baseq2, "*.pak").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var f = File.OpenRead(pak);
                using var r = new BinaryReader(f);
                if (f.Length < 12 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "PACK") continue;
                int offset = r.ReadInt32(), length = r.ReadInt32();
                if (offset < 12 || length < 0 || length % 64 != 0 || (long)offset + length > f.Length) continue;
                f.Position = offset;
                for (int i = 0; i < length / 64; i++)
                {
                    var raw = r.ReadBytes(56);
                    r.ReadInt64();
                    int end = Array.IndexOf(raw, (byte)0);
                    names.Add(Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end).ToLowerInvariant());
                }
            }
            catch (IOException) { }
        }
        return names;
    }

    /// <summary>What the data offers the script: the demos (demos/*.dm2 by name) and the live maps it prefers that are there.</summary>
    public static (IReadOnlyList<string> Demos, IReadOnlyList<string> Maps) Content(IReadOnlyList<string> entries)
    {
        var demos = entries.Where(e => e.StartsWith("demos/", StringComparison.Ordinal) && e.EndsWith(".dm2", StringComparison.Ordinal))
            .Select(e => e[6..^4]).Distinct().ToList();
        var maps = GameScript.PlayMaps.Where(m => entries.Contains($"maps/{m}.bsp")).ToList();
        return (demos, maps);
    }

    /// <summary>
    /// The command line: the run's own home, rcon on 127.0.0.1 only (IPv6 off) with a random password, a window at
    /// <paramref name="geometry"/> (not full screen: the desktop's resolution never changes), ray tracing on, a single GPU
    /// (no SLI device group), vsync, sound, the frame cap, DRS, photo mode and the pause on loss of focus off, the mouse
    /// never grabbed, cinematics skipped, the console logged and flushed (its last lines are evidence of a crash), the fps
    /// on screen for whoever watches, then the demo.
    /// </summary>
    public static ProcessStartInfo StartInfo(string exe, string home, int port, string password, string geometry, string demo)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        void Set(string name, string value) { psi.ArgumentList.Add("+set"); psi.ArgumentList.Add(name); psi.ArgumentList.Add(value); }
        Set("basedir", Path.GetDirectoryName(exe)!);
        Set("homedir", home);
        Set("net_ip", "127.0.0.1"); Set("net_enable_ipv6", "0");
        Set("net_port", port.ToString(CultureInfo.InvariantCulture));
        Set("rcon_password", password); Set("backdoor", "1");
        Set("vid_rtx", "1"); Set("sli", "0"); Set("vid_hdr", "0");
        Set("vid_fullscreen", "0"); Set("vid_geometry", geometry);
        Set("vid_vsync", "0"); Set("cl_async", "1"); Set("r_maxfps", "0");
        Set("cl_autopause", "0"); Set("cl_demowait", "1"); Set("in_grab", "0");
        Set("pt_accumulation_rendering", "0"); Set("drs_enable", "0"); Set("viewsize", "100");
        Set("s_enable", "0"); Set("cl_cinematics", "0"); Set("sv_allow_map", "1"); Set("scr_fps", "1");
        Set("deathmatch", "0"); Set("coop", "0"); Set("maxclients", "1"); Set("cheats", "1");
        Set("logfile", "1"); Set("logfile_flush", "1");
        psi.ArgumentList.Add("+demo"); psi.ArgumentList.Add(demo);
        return psi;
    }

    /// <summary>The run's own home: baseq2 with an empty q2config.cfg and autoexec.cfg, so the person's own are not read.</summary>
    public static string MakeHome(string dir)
    {
        string home = Path.Combine(dir, "q2home");
        Directory.CreateDirectory(Path.Combine(home, "baseq2", "ct"));
        File.WriteAllText(Path.Combine(home, "baseq2", "q2config.cfg"), "// Clocktail game test: an isolated home.\n");
        File.WriteAllText(Path.Combine(home, "baseq2", "autoexec.cfg"), "// No autoexec.\n");
        return home;
    }

    /// <summary>The console log's last lines (logs/console.log in the run's home), or null.</summary>
    public static string? LogTail(string home, int lines = 12)
    {
        string log = Path.Combine(home, "baseq2", "logs", "console.log");
        try
        {
            if (!File.Exists(log)) return null;
            using var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length > 65536) s.Position = s.Length - 65536;
            using var r = new StreamReader(s);
            var all = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", all.TakeLast(lines).Select(l => l.TrimEnd('\r')));
        }
        catch (IOException) { return null; }
    }

    /// <summary>A free UDP port on 127.0.0.1 for the game's rcon.</summary>
    public static int FreePort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }
}

/// <summary>
/// The game's console over authenticated IPv4 loopback rcon (Q2RTX's backdoor, which listens without a running server). A
/// fresh marker per state query, so a late reply to an earlier one is never read as this one; the password is random per
/// run and never written to the job's files (the console log has the commands, not the password).
/// </summary>
public sealed class QuakeRcon : IDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly string _password;
    private readonly TimeSpan _timeout;

    public QuakeRcon(int port, string password, TimeSpan? timeout = null)
    {
        _password = password;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        // Windows reports an ICMP port-unreachable (the game not listening yet) as a reset on the next receive: off.
        if (OperatingSystem.IsWindows())
            try { _udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch (SocketException) { }
        _udp.Connect(IPAddress.Loopback, port);
    }

    public static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Packet(string password, string command)
    {
        if (password.Length < 32 || !password.All(char.IsAsciiHexDigit) || command.Length > 1000 || command.IndexOfAny(['\r', '\n', '\0', '"', ';']) >= 0)
            throw new ArgumentException("invalid local rcon request");
        return [255, 255, 255, 255, .. Encoding.ASCII.GetBytes("rcon " + password + " " + command + "\n\0")];
    }

    public void Send(string command)
    {
        try { _udp.Send(Packet(_password, command)); }
        catch (SocketException) { }   // the port is not open yet (starting) or any more (exited): the next query says which
    }

    public GameFrame? Query()
    {
        string marker = "CT" + Guid.NewGuid().ToString("N");
        Send(GameFrame.Query(marker));
        long began = Stopwatch.GetTimestamp();
        try
        {
            while (Stopwatch.GetElapsedTime(began) < _timeout)
            {
                var wait = _timeout - Stopwatch.GetElapsedTime(began);
                if (wait <= TimeSpan.Zero) break;
                using var cts = new CancellationTokenSource(wait);
                var packet = _udp.ReceiveAsync(cts.Token).AsTask().GetAwaiter().GetResult();
                var b = packet.Buffer;
                if (b.Length < 10 || b[0] != 255 || b[1] != 255 || b[2] != 255 || b[3] != 255) continue;
                if (GameFrame.Parse(Encoding.ASCII.GetString(b, 4, b.Length - 4).TrimEnd('\0'), marker) is { } f) return f;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }   // ICMP port unreachable: the game is not listening
        return null;
    }

    public void Dispose() => _udp.Dispose();
}
