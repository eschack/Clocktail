using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Clocktail.Lab.Game;

/// <summary>Do not execute game code until containment and durable process registration have both succeeded.</summary>
[SupportedOSPlatform("windows")]
public sealed class SuspendedGame : IDisposable
{
    private nint _process, _thread;
    private bool _resumed;
    public Process Process { get; }

    private SuspendedGame(Process process, nint nativeProcess, nint thread) { Process = process; _process = nativeProcess; _thread = thread; }

    public static SuspendedGame Start(ProcessStartInfo start)
    {
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
        if (!CreateProcessW(start.FileName, command, 0, 0, false, 4u | (start.CreateNoWindow ? 0x08000000u : 0), 0,
                string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref info, out var child))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "the suspended game could not start");
        try { return new(System.Diagnostics.Process.GetProcessById((int)child.Pid), child.Process, child.Thread); }
        catch
        {
            TerminateProcess(child.Process, 1);
            CloseHandle(child.Thread); CloseHandle(child.Process);
            throw;
        }
    }

    public void Resume()
    {
        if (_resumed || _thread == 0) throw new InvalidOperationException("the game was already resumed or disposed");
        if (ResumeThread(_thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError(), "the game could not resume");
        _resumed = true;
    }

    public void Dispose()
    {
        if (_process == 0) return;
        if (!_resumed) TerminateProcess(_process, 1);
        CloseHandle(_thread); CloseHandle(_process);
        _thread = _process = 0;
        // The job owns the managed Process and waits for its exit before restoration.
    }

    public static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string? directory, ref StartupInfo start, out ProcessInfo child);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
