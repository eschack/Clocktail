using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

public class VersionedTests
{
    [Theory]
    [InlineData(0x184c, 2, 0x2184cu, false)]
    [InlineData(0xac8, 2, 0x20ac8u, false)]
    [InlineData(0x28, 1, 0x10028u, false)]
    [InlineData(0x15b0c, 2, 0x35b0cu, true)]    // core V/F status: size spills into the rev half
    [InlineData(0x424604, 5, 0x474604u, true)]  // domain V/F control
    [InlineData(0x2486e0, 3, 0x2786e0u, true)]
    public void Word_is_built_with_or(int size, int rev, uint word, bool overflows)
    {
        var v = new Versioned(size, rev);
        Assert.Equal(word, v.Word);
        Assert.Equal(overflows, v.Overflows);
        Assert.True(v.Matches(word));
    }

    [Fact]
    public void Overflowing_sizes_cannot_be_recovered_from_the_word()
    {
        // 0x35b0c reads as "size 0x5b0c rev 3" if parsed naively; the pair is the only truth.
        var v = new Versioned(0x15b0c, 2);
        Assert.NotEqual(v.Size, (int)(v.Word & 0xffff));
        Assert.NotEqual(v.Rev, (int)(v.Word >> 16));
    }

    [Fact]
    public void Rejects_impossible_pairs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Versioned(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Versioned(0x10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Versioned(0x10, 0x10000));
    }
}
