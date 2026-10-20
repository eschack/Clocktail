using System.Runtime.InteropServices;
using Clocktail.Domain.Platform;
using Microsoft.Extensions.Hosting;

namespace Clocktail.Mcp;

/// <summary>
/// The worker's watch on its driver, once a second (<see cref="Relay"/> runs the worker): when the driver it opened is
/// gone (an NVAPI handle loss, or NVML saying so), the residency samples still in memory go to disk and the process ends
/// at once with exit <see cref="Supervisor.DriverLost"/>, without calling into the driver that went away; the relay starts
/// a fresh worker, which opens the driver that came back. The look takes no driver call and no lane, so a call stuck in
/// the driver does not stop it.
/// </summary>
public sealed class DriverWatch(Card card, Residency residency) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (card.DriverGone() is not { } why) continue;
                Console.Error.WriteLine($"clocktail worker: the driver this process opened is gone ({why}); ending with exit {Supervisor.DriverLost} for a fresh worker");
                try { residency.Flush(); } catch (Exception) { }
                Console.Error.Flush();
                TerminateProcess(GetCurrentProcess(), Supervisor.DriverLost);   // no finalizers, nothing through the dead driver
                Environment.Exit(Supervisor.DriverLost);
            }
        }
        catch (OperationCanceledException) { }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, int exitCode);
}
