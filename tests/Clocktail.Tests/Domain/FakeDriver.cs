using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clocktail.Hw;
using Clocktail.Tests.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// A driver that keeps one structure per version word and can misbehave on demand. Get copies the stored
/// structure out; Set stores what it is given, subject to <see cref="Mode"/>. Static because unmanaged
/// callers cannot carry a closure; tests run one scenario at a time.
/// </summary>
internal static unsafe class FakeDriver
{
    public enum SetMode
    {
        Accept,
        /// <summary>Return an error and keep the old state.</summary>
        RefuseUntouched,
        /// <summary>Store the new state but still report an error.</summary>
        AcceptReportError,
        /// <summary>Store the new state with the field at <see cref="Field"/> clamped to zero.</summary>
        ClampField,
        /// <summary>Store the new state and also flip a byte at <see cref="Field"/> + <see cref="Neighbour"/>.</summary>
        TouchNeighbour,
        /// <summary>Drop the handle: return -10 and keep the old state.</summary>
        LoseHandle,
    }

    public static readonly Dictionary<uint, byte[]> State = new();
    public static SetMode Mode = SetMode.Accept;
    public static int Field;
    public static int Neighbour = 0x54;
    public static int Gets, Sets;
    /// <summary>When > 0, that many upcoming Gets fail with -1 (counting down).</summary>
    public static int FailNextGets;
    /// <summary>When set, the first Set after it is armed also applies <see cref="Mode"/>; later Sets accept. Used for "restore works".</summary>
    public static bool MisbehaveOnce;
    private static bool _misbehaved;
    /// <summary>
    /// Request-style interfaces: the written structure is a request, not the new state. When set, Set stores
    /// <c>RequestApplier(currentState, request)</c> instead of the request itself.
    /// </summary>
    public static Func<byte[], byte[], byte[]>? RequestApplier;
    /// <summary>
    /// Query-style reads: the answer depends on what the caller put in the buffer (a board bit, a command code).
    /// When set, Get copies <c>QueryResponder(request)</c> instead of the stored structure, unless it returns null.
    /// </summary>
    public static Func<byte[], byte[]?>? QueryResponder;
    /// <summary>Runs at the start of every Set, before anything is stored: lets a test see the world as the driver is told to change it.</summary>
    public static Action? OnSet;

    public static void Reset()
    {
        State.Clear();
        Mode = SetMode.Accept;
        Field = 0;
        Neighbour = 0x54;
        Gets = Sets = 0;
        FailNextGets = 0;
        MisbehaveOnce = false;
        _misbehaved = false;
        RequestApplier = null;
        QueryResponder = null;
        OnSet = null;
    }

    /// <summary>Wire any number of readable and writable slots to the stored-structure stand-ins.</summary>
    public static FakeApi ApiFor(IEnumerable<Slot> gets, IEnumerable<Slot>? sets = null)
    {
        var api = new FakeApi();
        foreach (var g in gets) api.With(g, &Get);
        foreach (var w in sets ?? []) api.With(w, &Set);
        return api;
    }

    public static void Seed(Slot slot, Action<byte[]>? init = null)
    {
        var b = NvClient.NewBuffer(slot);
        init?.Invoke(b);
        State[BitConverter.ToUInt32(b)] = b;
    }

    public static byte[] Current(Slot slot) => State[Slots.Info(slot).Shape!.Value.Word];

    public static FakeApi Api(Slot get, Slot set) => new FakeApi().With(get, &Get).With(set, &Set);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Get(nint gpu, byte* p)
    {
        Gets++;
        if (FailNextGets > 0) { FailNextGets--; return -1; }
        uint word = *(uint*)p;
        if (!State.TryGetValue(word, out var s)) return NvStatus.NotSupported;
        if (QueryResponder != null && QueryResponder(new ReadOnlySpan<byte>(p, s.Length).ToArray()) is { } answer) s = answer;
        s.AsSpan().CopyTo(new Span<byte>(p, s.Length));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Set(nint gpu, byte* p)
    {
        Sets++;
        OnSet?.Invoke();
        uint word = *(uint*)p;
        if (!State.TryGetValue(word, out var s)) return NvStatus.NotSupported;
        var incoming = new ReadOnlySpan<byte>(p, s.Length).ToArray();
        if (RequestApplier != null) incoming = RequestApplier(s, incoming);
        var mode = Mode;
        if (MisbehaveOnce) { if (_misbehaved) mode = SetMode.Accept; _misbehaved = true; }
        switch (mode)
        {
            case SetMode.Accept: State[word] = incoming; return 0;
            case SetMode.RefuseUntouched: return -5;
            case SetMode.AcceptReportError: State[word] = incoming; return -5;
            case SetMode.ClampField: incoming.AsSpan(Field, 4).Clear(); State[word] = incoming; return 0;
            case SetMode.TouchNeighbour: incoming[Field + Neighbour] ^= 0x01; State[word] = incoming; return 0;
            case SetMode.LoseHandle: return NvStatus.HandleInvalidated;
            default: throw new InvalidOperationException();
        }
    }
}
