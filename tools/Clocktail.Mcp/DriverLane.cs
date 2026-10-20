using System.Diagnostics;
using System.Globalization;

namespace Clocktail.Mcp;

/// <summary>
/// One driver call at a time, with a deadline on the wait. A call that never returns (a hung GPU keeps NVAPI and NVML
/// waiting through the whole TDR delay, 30 s on the reference machine) must not hang every tool behind it: a caller that
/// cannot get in within <c>wait</c> is told so and reads nothing, and once the lane has been held longer than that, new
/// callers are told at once. The call in the driver is never cancelled; when it returns, the lane is free again.
/// Re-entrant on the holder's thread.
/// </summary>
public sealed class DriverLane(TimeSpan wait)
{
    private readonly object _gate = new();
    private long _since;     // Stopwatch timestamp of the outermost holder's entry; 0 while free
    private string? _what;
    private int _depth;      // the holder's thread only

    public Held Enter(string what)
    {
        if (!Monitor.TryEnter(_gate))
        {
            long since = Volatile.Read(ref _since);
            var held = since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(since);
            if (held >= wait || !Monitor.TryEnter(_gate, wait - held))
            {
                since = Volatile.Read(ref _since);
                throw new DriverBusyException(Volatile.Read(ref _what), since == 0 ? wait : Stopwatch.GetElapsedTime(since));
            }
        }
        if (_depth++ == 0)
        {
            Volatile.Write(ref _what, what);
            Volatile.Write(ref _since, Stopwatch.GetTimestamp());
        }
        return new Held(this);
    }

    private void Exit()
    {
        if (--_depth == 0)
        {
            Volatile.Write(ref _since, 0);
            Volatile.Write(ref _what, null);
        }
        Monitor.Exit(_gate);
    }

    public readonly struct Held(DriverLane lane) : IDisposable
    {
        public void Dispose() => lane.Exit();
    }
}

/// <summary>The driver did not answer in time: nothing was read, and nothing waits on it here.</summary>
public sealed class DriverBusyException(string? what, TimeSpan held) : InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
    $"the driver has not answered for {held.TotalSeconds:0} s ({what ?? "a read"} is still waiting on it: the GPU may be hung, or a driver reset is under way); nothing was read. Try again in a few seconds; after a driver reset the service puts the daily profile back by itself"));
