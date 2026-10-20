using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clocktail.Core.Native;
using Clocktail.Hw;
using Xunit;

namespace Clocktail.Tests;

public unsafe class GuardedCallTests
{
    // Fake "driver" entry points with the (gpuHandle, struct*) signature used by private NVAPI calls.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WellBehaved(nint gpu, byte* p) { p[8] = 0x42; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WritesPastEnd(nint gpu, byte* p) { p[16] = 1; return 0; }   // struct is 16 bytes

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WritesBefore(nint gpu, byte* p) { p[-1] = 1; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Fails(nint gpu, byte* p) { p[0] = 9; return -9; }

    private static nint Ptr(delegate* unmanaged[Cdecl]<nint, byte*, int> f) => (nint)f;

    [Fact]
    public void Version_packs_size_and_revision()
    {
        Assert.Equal(0x2184cu, GuardedCall.Version(0x184c, 2));
        Assert.Equal(0x20ac8u, GuardedCall.Version(0xac8, 2));
        Assert.Equal(0x10028u, GuardedCall.Version(0x28, 1));
    }

    [Fact]
    public void Successful_call_copies_reply_back()
    {
        var data = new byte[16];
        int rc = GuardedCall.Invoke("t", Ptr(&WellBehaved), 1, data);
        Assert.Equal(0, rc);
        Assert.Equal(0x42, data[8]);
    }

    [Fact]
    public void Write_past_end_is_detected()
    {
        var data = new byte[16];
        Assert.Throws<GuardViolationException>(() => GuardedCall.Invoke("t", Ptr(&WritesPastEnd), 1, data));
    }

    [Fact]
    public void Write_before_start_is_detected()
    {
        var data = new byte[16];
        Assert.Throws<GuardViolationException>(() => GuardedCall.Invoke("t", Ptr(&WritesBefore), 1, data));
    }

    [Fact]
    public void Failed_call_returns_status_and_leaves_buffer_untouched()
    {
        var data = new byte[16];
        int rc = GuardedCall.Invoke("t", Ptr(&Fails), 1, data);
        Assert.Equal(-9, rc);
        Assert.Equal(0, data[0]);
    }

    [Fact]
    public void Missing_interface_is_reported()
    {
        Assert.Throws<NotSupportedException>(() => GuardedCall.Invoke("t", 0, 1, new byte[4]));
    }
}
