namespace Clocktail.Domain.Broker;

/// <summary>
/// Groups driver-reset events into resets. Every TDR so far logged a single nvlddmkm 153, but the device restart of a GPU
/// left hung by one (2026-10-19 21:48Z) logged nine in 100 ms and four more over the next 18 s, and each event saved the
/// same flight window as one more trace: eight identical lines in one second, then two saves of one file colliding. An
/// event that comes within <see cref="Quiet"/> of the one before it belongs to the same reset, so a storm that keeps
/// going is one reset however long it lasts.
/// </summary>
public sealed class ResetBursts(TimeSpan quiet)
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(3);

    public TimeSpan Quiet { get; } = quiet;
    private readonly object _gate = new();
    private DateTimeOffset? _last;

    /// <summary>True when the event seen at <paramref name="at"/> starts a new reset; false when it continues the last one.</summary>
    public bool Starts(DateTimeOffset at)
    {
        lock (_gate)
        {
            bool starts = _last is not { } last || at - last >= Quiet;
            _last = at;
            return starts;
        }
    }
}
