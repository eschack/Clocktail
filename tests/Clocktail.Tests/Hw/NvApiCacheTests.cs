using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

/// <summary>NVAPI's interface cache from many threads at once, as the broker's telemetry sampler and its request threads
/// use it: every ID resolves to its own pointer, once. A Dictionary written from two threads at once can lose entries or
/// corrupt itself; the cache has its own lock since 0.3.7. No GPU, no nvapi64.dll.</summary>
public unsafe class NvApiCacheTests
{
    private static int _queries;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Query(uint id)
    {
        Interlocked.Increment(ref _queries);
        return (nint)(0x10000 + (long)id);
    }

    [Fact]
    public void Interfaces_resolved_from_many_threads_at_once_resolve_each_to_its_own_pointer_once()
    {
        _queries = 0;
        var api = NvApi.FromQuery(&Query);
        const int ids = 4000;
        Parallel.For(0, 16, _ =>
        {
            for (uint id = 0; id < ids; id++)
                Assert.Equal((nint)(0x10000 + (long)id), api.Query(id));
        });
        Assert.Equal(ids, _queries);   // each one asked of the driver once, then cached
    }
}
