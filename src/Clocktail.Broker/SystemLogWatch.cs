using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Broker;

/// <summary>
/// Subscribes to the System log for one kind of event and passes each one on: the display driver's resets
/// (<see cref="Domain.Platform.SystemLog.DriverResetQuery"/>; nvlddmkm 153 is what the 5090 on driver 617.42 wrote for the
/// in-game hang of 2026-10-18, Display 4101 the generic "display driver stopped responding and has recovered"), and the
/// PC waking up (<see cref="Domain.Platform.SystemLog.WakeQuery"/>). A push subscription (EvtSubscribe), so nothing polls
/// and no package is needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SystemLogWatch : IDisposable
{
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

    /// <param name="what">The events in words, passed to <paramref name="onEvent"/> and used in errors.</param>
    /// <param name="onEvent">Called on an event-log thread with <paramref name="what"/>; must not block.</param>
    /// <param name="log">A subscription error: logged, never taken for an event (a give-back would end a tuning run for nothing).</param>
    public SystemLogWatch(string query, string what, Action<string> onEvent, Action<string> log)
    {
        _callback = (action, _, evt) =>
        {
            if (action == ActionDeliver) onEvent(what);
            else log($"System log subscription ({what}) reported error {evt.ToInt64()}");
            return 0;
        };
        _subscription = EvtSubscribe(IntPtr.Zero, IntPtr.Zero, "System", query, IntPtr.Zero, IntPtr.Zero,
                                     Marshal.GetFunctionPointerForDelegate(_callback), SubscribeToFutureEvents);
        if (_subscription == IntPtr.Zero)
            throw new InvalidOperationException($"could not subscribe to the System log for {what} (error {Marshal.GetLastPInvokeError()})");
    }

    public void Dispose() => EvtClose(_subscription);
}
