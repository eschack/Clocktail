using Clocktail.Hw;
using System.Runtime.InteropServices;

namespace Clocktail.Core.Native;

public sealed class GuardViolationException(string function, string where)
    : Exception($"{function}: the driver wrote outside the declared structure ({where}). The result was discarded.");

/// <summary>
/// Calls a private NVAPI "(gpuHandle, struct*)" interface: the structure is surrounded by
/// 0xCD guard bytes (0x40 before, 0x1000 after) which are verified after the call.
/// </summary>
public static unsafe class GuardedCall
{
    private const byte Fill = 0xCD;
    private const int Head = 0x40;
    private const int Tail = 0x1000;

    /// <param name="fn">Resolved NVAPI function pointer.</param>
    /// <param name="gpu">Physical GPU handle.</param>
    /// <param name="data">Structure bytes, version dword already filled in. Updated with the driver's reply on success.</param>
    /// <returns>NVAPI status (0 = OK).</returns>
    public static int Invoke(string name, nint fn, nint gpu, byte[] data)
    {
        if (fn == 0) throw new NotSupportedException($"{name}: interface is not exposed by this driver.");
        int total = Head + data.Length + Tail;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)total);
        try
        {
            new Span<byte>(mem, total).Fill(Fill);
            data.CopyTo(new Span<byte>(mem + Head, data.Length));

            int rc = ((delegate* unmanaged[Cdecl]<nint, byte*, int>)fn)(gpu, mem + Head);

            for (int i = 0; i < Head; i++)
                if (mem[i] != Fill) throw new GuardViolationException(name, $"head byte {i}");
            for (int i = 0; i < Tail; i++)
                if (mem[Head + data.Length + i] != Fill) throw new GuardViolationException(name, $"tail byte {i}");

            if (rc == 0) new ReadOnlySpan<byte>(mem + Head, data.Length).CopyTo(data);
            return rc;
        }
        finally { NativeMemory.Free(mem); }
    }

    /// <summary>NVAPI struct version dword: size in the low 16 bits, structure revision in the next 16.</summary>
    public static uint Version(int size, int revision) => (uint)(size | (revision << 16));
}
