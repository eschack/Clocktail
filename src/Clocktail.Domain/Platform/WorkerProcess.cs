using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Domain.Platform;

/// <summary>
/// Starts this program's worker and waits for it, for a <see cref="Supervisor"/>. The worker's standard output and error
/// come back line by line (the runtime writes its "Fatal error" and stack there), so the supervisor's log is the one
/// writer of its file. Every worker goes into a job object that kills it when the supervisor's handle closes: ending the
/// supervisor (the task being ended, the installer, the tray's Quit) ends the worker with it instead of leaving it orphaned.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkerProcess(string exe)
{
    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private nint _job;

    /// <summary>For the worker: when its output goes to a pipe (a supervisor reads it), write it as UTF-8, which is how
    /// <see cref="Run"/> reads it, instead of the console's code page.</summary>
    public static void WriteUtf8WhenRedirected()
    {
        if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true });
        if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true });
    }

    /// <summary>Start <c>exe arguments</c>, pass each line it prints to <paramref name="line"/>, and return its exit code.</summary>
    public int Run(string arguments, Action<string> line, Action<string>? log = null)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // Not the console's code page (437 here): the broker's lines carry the profile's Chinese notes. The worker
            // writes UTF-8 to a redirected output (WriteUtf8WhenRedirected).
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        using var p = new Process { StartInfo = psi };
        // A line that cannot be written (a full disk) is lost, never the supervisor: these run on pool threads, where an
        // exception would end the process and, through the job, the worker with it.
        void Pass(string? text)
        {
            if (text == null) return;
            try { line(text); } catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        }
        p.OutputDataReceived += (_, e) => Pass(e.Data);
        p.ErrorDataReceived += (_, e) => Pass(e.Data);
        try { p.Start(); }
        catch (Win32Exception e)
        {
            log?.Invoke($"could not start the worker ({e.Message})");
            return e.NativeErrorCode == 0 ? 1 : e.NativeErrorCode;
        }
        if (!Contain(p)) log?.Invoke($"worker pid {p.Id} could not be tied to the supervisor (error {Marshal.GetLastPInvokeError()}); it would outlive a killed supervisor");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();   // also drains the redirected output
        return p.ExitCode;
    }

    private bool Contain(Process p)
    {
        if (_job == 0)
        {
            var job = CreateJobObjectW(0, null);
            if (job == 0) return false;
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>())) { CloseHandle(job); return false; }
            _job = job;   // held until this process ends: its closing is what kills the worker
        }
        return AssignProcessToJobObject(_job, p.Handle);
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int size);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
