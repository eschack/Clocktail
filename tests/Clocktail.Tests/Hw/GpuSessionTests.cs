using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

public class GpuSessionTests
{
    [Fact]
    public void Lock_is_reentrant_on_the_owning_thread_and_blocks_others()
    {
        var gpu = new GpuSession(1, "g");
        Assert.True(gpu.TryEnter(TimeSpan.Zero, out var outer));
        using (outer)
        {
            Assert.True(gpu.TryEnter(TimeSpan.Zero, out var inner));
            inner.Dispose();

            bool otherGotIt = true;
            var t = new Thread(() => otherGotIt = gpu.TryEnter(TimeSpan.FromMilliseconds(50), out var s) && Dispose(s));
            t.Start(); t.Join();
            Assert.False(otherGotIt);
        }
        Assert.True(gpu.TryEnter(TimeSpan.Zero, out var again));
        again.Dispose();

        static bool Dispose(GpuSession.Scope s) { s.Dispose(); return true; }
    }

    [Fact]
    public void Null_handles_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new GpuSession(0, "g"));
        Assert.Throws<ArgumentException>(() => new GpuSession(1, "g").Rebind(0));
    }
}
