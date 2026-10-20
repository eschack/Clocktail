using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Broker;

/// <summary>
/// Subscribes to the System log for the display driver's reset events and passes each one on. nvlddmkm 153 is what
/// the 5090 on driver 617.42 wrote for the in-game hang of 2026-10-18 (no Display 4101 came with it); 4101 is the
/// generic "display driver stopped responding and has recovered", kept for drivers that write it instead. A push
/// subscription (EvtSubscribe), so nothing polls and no package is needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DriverResetWatch : IDisposable
{
    public const string Query =
        "*[System[(Provider[@Name='nvlddmkm'] and EventID=153) or (Provider[@Name='Display'] and EventID=4101)]]";

    private const uint SubscribeToFutureEvents = 1;
    private const int ActionDeliver = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(int action, IntPtr context, IntPtr evt);

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr EvtSubscribe(IntPtr session, IntPtr signalEvent, string channelPath, string query,
                                               IntPtr bookmark, IntPtr context, IntPtr callback, uint flags);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(IntPtr handle);

    private readonly Callback _callback;   // held so the collector never frees what the event log calls into
    private readonly IntPtr _subscription;

    /// <param name="onReset">Called on an event-log thread with a short description; must not block.</param>
    /// <param name="log">A subscription error: logged, never taken for a reset (a give-back would end a tuning run for nothing).</param>
    public DriverResetWatch(Action<string> onReset, Action<string> log)
    {
        _callback = (action, _, evt) =>
        {
            if (action == ActionDeliver) onReset("nvlddmkm 153 / Display 4101 in the System log");
            else log($"driver-reset subscription reported error {evt.ToInt64()}");
            return 0;
        };
        _subscription = EvtSubscribe(IntPtr.Zero, IntPtr.Zero, "System", Query, IntPtr.Zero, IntPtr.Zero,
                                     Marshal.GetFunctionPointerForDelegate(_callback), SubscribeToFutureEvents);
        if (_subscription == IntPtr.Zero)
            throw new InvalidOperationException($"could not subscribe to the System log for driver resets (error {Marshal.GetLastPInvokeError()})");
    }

    public void Dispose() => EvtClose(_subscription);
}
