namespace Clocktail.Domain.Broker;

public enum RestorationState { Unknown, Pending, Ready, Failed }
public enum RestorationKind { Startup, DriverReset, Resume }
public sealed record RestorationStatus(RestorationState Startup, RestorationState Recovery, long Generation, string? Reason);

/// <summary>Read-only lifecycle evidence. Tracking never performs recovery or acquires an operation window.</summary>
public sealed class BrokerStatusLifecycle
{
    private sealed class Device
    {
        public RestorationState Startup, Recovery;
        public long Generation;
        public int Active;
        public bool Failed;
        public bool QueuedRecovery;
        public string? Reason;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Invalidated;
    public void InitializeStartup(IEnumerable<string> ids)
    {
        lock (_gate) foreach (var id in ids) _devices[id] = new() { Startup = RestorationState.Pending, Reason = "Startup restoration pending" };
        Invalidated?.Invoke();
    }
    public void PendingRecovery(IEnumerable<string> ids, string reason)
    {
        lock (_gate) foreach (var id in ids) { var d = Get(id); d.Recovery = RestorationState.Pending; d.QueuedRecovery = true; d.Reason = reason; d.Generation++; }
        Invalidated?.Invoke();
    }
    public Lease Begin(string id, RestorationKind kind)
    {
        lock (_gate)
        {
            var d = Get(id);
            if (d.Active++ == 0) d.Failed = false;
            if (kind == RestorationKind.Startup) d.Startup = RestorationState.Pending;
            else { d.Recovery = RestorationState.Pending; d.QueuedRecovery = false; }
            d.Reason = kind.ToString(); d.Generation++;
        }
        Invalidated?.Invoke();
        return new(this, id, kind);
    }
    public RestorationStatus Read(string id)
    {
        lock (_gate)
        {
            var d = Get(id);
            return new(d.Startup, d.Recovery, d.Generation, d.Reason);
        }
    }
    private Device Get(string id)
    {
        if (!_devices.TryGetValue(id, out var d)) _devices[id] = d = new();
        return d;
    }
    private void Finish(string id, RestorationKind kind, bool success)
    {
        lock (_gate)
        {
            var d = Get(id);
            d.Failed |= !success;
            d.Active--;
            if (kind == RestorationKind.Startup) d.Startup = success ? RestorationState.Ready : RestorationState.Failed;
            if (d.Active == 0 && !d.QueuedRecovery && d.Recovery == RestorationState.Pending) d.Recovery = d.Failed ? RestorationState.Failed : RestorationState.Ready;
            d.Reason = success ? null : $"{kind} did not complete successfully";
        }
        Invalidated?.Invoke();
    }
    public sealed class Lease : IDisposable
    {
        private readonly BrokerStatusLifecycle _owner;
        private readonly string _id;
        private readonly RestorationKind _kind;
        private bool _ended;
        internal Lease(BrokerStatusLifecycle owner, string id, RestorationKind kind) { _owner = owner; _id = id; _kind = kind; }
        public void Complete(bool success) { if (_ended) return; _ended = true; _owner.Finish(_id, _kind, success); }
        public void Dispose() => Complete(false);
    }
}
