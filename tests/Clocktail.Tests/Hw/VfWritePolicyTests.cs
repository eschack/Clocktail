using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

/// <summary>Kind 7, the V/F point setter's whitelist: complete, off by default until its live drill.</summary>
public class VfWritePolicyTests
{
    private const Slot Set = Slot.ClockClientClkVfPointsSetControl;
    private static string? Check(byte[] b) => WritePolicySet.Default.WithEnabled(Set).Check(Set, b);

    private static byte[] Selecting(params int[] points)
    {
        var b = NvClient.NewBuffer(Set);
        foreach (int i in points) b[4 + (i >> 3)] |= (byte)(1 << (i & 7));
        return b;
    }

    [Fact]
    public void Selected_type_zero_points_inside_the_first_128_pass()
    {
        Assert.Null(Check(Selecting(0)));
        Assert.Null(Check(Selecting(60, 126, 127)));
    }

    [Fact]
    public void The_rule_set_is_complete_and_the_setter_is_off_by_default()
    {
        var p = WritePolicySet.Default.All.Single(x => x.Slot == Set);
        Assert.True(p.Complete);
        Assert.Equal(PolicyState.Disabled, p.State);
        Assert.NotNull(WritePolicySet.Default.Check(Set, Selecting(0)));
    }

    [Fact]
    public void An_empty_mask_is_refused()
    {
        Assert.Contains("selects no point", Check(Selecting()));
    }

    [Fact]
    public void Points_from_128_up_are_never_writable()
    {
        Assert.Contains("must be zero", Check(Selecting(60, 128)));
        Assert.Contains("must be zero", Check(Selecting(255)));
    }

    [Fact]
    public void A_selected_point_that_is_not_a_core_frequency_entry_is_refused()
    {
        var b = Selecting(60, 127);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x44 + 127 * 0x24), 1);
        Assert.Contains("point 127 is entry type 1", Check(b));
        // The same entry, not selected, is none of the setter's business.
        b[4 + (127 >> 3)] &= unchecked((byte)~(1 << (127 & 7)));
        Assert.Null(Check(b));
    }

    [Fact]
    public void The_offsets_themselves_are_not_the_whitelists_concern()
    {
        var b = Selecting(10);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x58 + 10 * 0x24), int.MinValue);
        Assert.Null(Check(b));   // bounds are the domain plan's (VfRules), which knows the firmware range
    }
}
