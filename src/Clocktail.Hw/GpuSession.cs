namespace Clocktail.Hw;

/// <summary>
/// One GPU as this process currently holds it: the NVAPI handle plus the two facts every call needs,
/// whether the handle is still believed valid and who is allowed to talk to the driver right now.
/// <para>
/// A handle loss (NVAPI status -8 or -10) latches <see cref="Faulted"/>. From then on every call through
/// <see cref="NvClient"/> returns <see cref="NvOutcome.Poisoned"/> without touching the driver, until a
/// reconnection calls <see cref="Rebind"/> with a fresh handle.
/// </para>
/// </summary>
public sealed class GpuSession
{
    private readonly object _gate = new();
    private volatile bool _faulted;
    private int _faultStatus;

    public GpuSession(nint handle, string label)
    {
        if (handle == 0) throw new ArgumentException("A GPU session needs a non-null handle.", nameof(handle));
        Handle = handle;
        Label = label;
    }

    public nint Handle { get; private set; }

    /// <summary>Human-readable identity for messages (name or stable id). Never used for matching.</summary>
    public string Label { get; }

    /// <summary>True after a handle loss. Cleared only by <see cref="Rebind"/>.</summary>
    public bool Faulted => _faulted;

    /// <summary>The NVAPI status that latched the fault (0 when not faulted).</summary>
    public int FaultStatus => _faultStatus;

    /// <summary>Bumped on every rebind, so cached state tied to an old handle can notice it is stale.</summary>
    public int Generation { get; private set; }

    internal void Poison(int status)
    {
        _faultStatus = status;
        _faulted = true;
    }

    /// <summary>Install the handle a reconnection found for the same physical GPU and clear the fault.</summary>
    public void Rebind(nint handle)
    {
        if (handle == 0) throw new ArgumentException("A GPU session needs a non-null handle.", nameof(handle));
        lock (_gate)
        {
            Handle = handle;
            _faultStatus = 0;
            _faulted = false;
            Generation++;
        }
    }

    /// <summary>
    /// Serializes everything that talks to this GPU (writers and the telemetry poller alike).
    /// Re-entrant on the owning thread. Returns false when another thread holds it past <paramref name="timeout"/>.
    /// </summary>
    public bool TryEnter(TimeSpan timeout, out Scope scope)
    {
        if (Monitor.TryEnter(_gate, timeout))
        {
            scope = new Scope(_gate);
            return true;
        }
        scope = default;
        return false;
    }

    public readonly struct Scope(object? gate) : IDisposable
    {
        public void Dispose()
        {
            if (gate != null) Monitor.Exit(gate);
        }
    }
}
