using System.Buffers.Binary;
using Clocktail.Domain.Rails;

namespace Clocktail.Tests.Domain;

/// <summary>The ADC status as the 5090 returned it under nvgwls -l 100 (2026-10-20): mask 0x1ffd, device 0 at 962.5 mV,
/// the 11 GPCs at 925-943.75 mV with NVVDD at 930.</summary>
public class GpcVoltagesTests
{
    private static byte[] Status(uint mask = GpcVoltages.KnownMask, uint sentinel = 0xffffffff, int badCodeAt = -1)
    {
        var b = new byte[0x9c8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(GpcVoltages.Sentinel), sentinel);
        byte[] codes = [154, 0, 150, 151, 148, 150, 149, 151, 150, 148, 151, 150, 149];
        for (int i = 0; i <= 12; i++)
        {
            if (i == 1) continue;
            int o = GpcVoltages.Stride + i * GpcVoltages.Stride;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), codes[i] * GpcVoltages.CodeUv + (i == badCodeAt ? 1000u : 0));
            b[o + 4] = codes[i];
        }
        return b;
    }

    [Fact]
    public void The_eleven_gpcs_without_device_0()
    {
        var r = GpcVoltages.Parse(Status(), GpcVoltages.KnownMask)!;
        Assert.Equal(11, r.GpcUv.Count);
        Assert.Equal(925, r.MinMv);
        Assert.Equal(943.75, r.MaxMv);
        Assert.InRange(r.MeanMv, 935, 937);   // a few mV above NVVDD 930, as measured
    }

    [Fact]
    public void Anything_off_the_verified_layout_is_null_not_a_guess()
    {
        Assert.Null(GpcVoltages.Parse(Status(mask: 0x0ffd), 0x0ffd));               // another mask: another chip
        Assert.Null(GpcVoltages.Parse(Status(), 0x0ffd));                            // the mask was not echoed
        Assert.Null(GpcVoltages.Parse(Status(sentinel: 0), GpcVoltages.KnownMask));  // the sentinel moved
        Assert.Null(GpcVoltages.Parse(Status(badCodeAt: 5), GpcVoltages.KnownMask)); // a value that is not its code
    }
}
