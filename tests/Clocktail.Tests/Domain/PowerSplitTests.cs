using System.Buffers.Binary;
using Clocktail.Domain.Power;

namespace Clocktail.Tests.Domain;

public class PowerSplitTests
{
    // A live GetAllClocks probe, first sample (nvidia-smi: graphics 1507, memory 810, video 1245 MHz).
    private static byte[] LiveClocks()
    {
        var b = new byte[AllClocksLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x00020484);
        void E(int i, uint khz, uint flags)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + i * 8), khz);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + i * 8), flags);
        }
        E(0, 1507000, 0x11); E(1, 1432000, 1); E(2, 1320000, 0x11); E(3, 540000, 1); E(4, 810000, 9);
        E(6, 1188000, 1); E(20, 855000, 1); E(21, 1245000, 1); E(22, 0, 1); E(31, 2, 1);
        E(32, 1493342, 0x20);   // past the domain table: other data, never read as a clock
        return b;
    }

    [Fact]
    public void The_clock_table_is_indexed_by_internal_domain_id()
    {
        var c = AllClocksLayout.Parse(LiveClocks());
        Assert.Equal(1507000u, c[AllClocksLayout.Graphics]);
        Assert.Equal(1432000u, c[AllClocksLayout.Xbar]);
        Assert.Equal(1320000u, c[AllClocksLayout.Sys]);
        Assert.Equal(810000u, c[AllClocksLayout.Memory]);
        Assert.Equal(1245000u, c[AllClocksLayout.Video]);
        Assert.False(c.ContainsKey(5));    // absent
        Assert.False(c.ContainsKey(32));   // beyond the table
    }

    [Fact]
    public void The_topology_reads_each_entry_id_and_raw_value()
    {
        // A live power topology probe, 01:10:33.903
        var b = Convert.FromHexString("480001000200000000000000000000003D080000000000000100000000000000A4160000000000000000000000000000000000000000000000000000000000000000000000000000");
        var t = PowerTopologyLayout.Parse(b);
        Assert.Equal([(0u, 0x83du), (1u, 0x16a4u)], t);
    }
}
