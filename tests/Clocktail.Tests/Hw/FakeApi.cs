using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

/// <summary>Hands out pointers to managed stand-ins for driver entry points. No GPU involved.</summary>
internal sealed unsafe class FakeApi : INvApi
{
    public readonly Dictionary<Slot, nint> Fns = new();
    public readonly List<Slot> Resolved = new();

    public nint Resolve(Slot slot)
    {
        Resolved.Add(slot);
        return Fns.GetValueOrDefault(slot);
    }

    public FakeApi With(Slot slot, delegate* unmanaged[Cdecl]<nint, byte*, int> fn) { Fns[slot] = (nint)fn; return this; }
    public FakeApi With(Slot slot, delegate* unmanaged[Cdecl]<nint, uint, byte*, int> fn) { Fns[slot] = (nint)fn; return this; }
    public FakeApi With(Slot slot, delegate* unmanaged[Cdecl]<nint, uint*, int> fn) { Fns[slot] = (nint)fn; return this; }

    // ---- stand-ins (gpu, struct*) ----
    public static int Calls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WellBehaved(nint gpu, byte* p) { Calls++; p[8] = 0x42; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int HandleInvalidated(nint gpu, byte* p) { Calls++; return NvStatus.HandleInvalidated; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int InvalidHandle(nint gpu, byte* p) { Calls++; return NvStatus.InvalidHandle; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Fails(nint gpu, byte* p) { Calls++; p[8] = 9; return -9; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WritesPastEnd16(nint gpu, byte* p) { Calls++; p[16] = 1; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WritesBefore(nint gpu, byte* p) { Calls++; p[-1] = 1; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WritesPastEndOfBoost(nint gpu, byte* p) { Calls++; p[0x28] = 1; return 0; }

    // ---- (gpu, index, struct*) ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int EchoesIndex(nint gpu, uint index, byte* p) { Calls++; *(uint*)(p + 4) = index; return 0; }

    // ---- (gpu, uint*) ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ScalarSeven(nint gpu, uint* v) { Calls++; *v = 7; return 0; }

    // ---- sentinel {ver, domain, out} ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DomainTimesTen(nint gpu, byte* p) { Calls++; *(uint*)(p + 8) = *(uint*)(p + 4) * 10; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DomainMinusOne(nint gpu, byte* p) { Calls++; *(uint*)(p + 8) = 0xffffffff; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DomainSpillsTail(nint gpu, byte* p) { Calls++; *(uint*)(p + 8) = 5; p[12] = 0; return 0; }
}
