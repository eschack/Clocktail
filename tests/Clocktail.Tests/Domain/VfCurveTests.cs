using System.Buffers.Binary;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class VfCurveTests
{
    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static VfCurveSnapshot Parse(FakeVf f) { var c = f.Control(); return VfParser.Parse(f.Info(), f.Status(c), c); }

    [Fact]
    public void The_reader_asks_status_and_control_about_the_advertised_mask()
    {
        var f = new FakeVf();
        var hw = f.Install();
        var s = VfReader.Read(hw, new GpuSession(1, "fake"));

        Assert.Equal(VfMode.Regional, s.Mode);
        Assert.Equal(VfLayoutId.Regional132, s.Layout);
        Assert.Equal(132, s.MaskPoints);
        Assert.Equal(127, s.EditableCount);
        Assert.Equal(Enumerable.Range(0, 127), s.Points.Select(p => p.Index));
        Assert.Equal(1_100_000u, s.Points[80].VoltageUv);
        Assert.Equal(2_500_000u, s.Points[80].BaseKhz);
        Assert.Equal(2_500_000u, s.Points[80].EffectiveKhz);
        Assert.All(s.Points, p => Assert.Equal(0, p.OffsetKhz));
        Assert.Equal(0, s.DemandShiftUv);
        Assert.Equal("0*127", VfOffsets.Encode(s.Offsets()));
    }

    [Fact]
    public void A_status_asked_about_no_point_holds_only_the_mode_byte_and_does_not_parse()
    {
        var f = new FakeVf();
        var hw = f.Install();
        var bare = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetStatus);
        Assert.True(hw.Call(new GpuSession(1, "fake"), Slot.ClockClientClkVfPointsGetStatus, bare).IsOk);
        Assert.Equal(1, bare[VfLayout.StatusMode]);
        Assert.Throws<InvalidDataException>(() => VfParser.Parse(f.Info(), bare, f.Control()));
    }

    [Theory]
    [InlineData(VfMode.Regional, 132, 127, VfLayoutId.Regional132)]
    [InlineData(VfMode.Regional, 129, 127, VfLayoutId.Regional129)]
    [InlineData(VfMode.Legacy, 84, 80, VfLayoutId.Legacy84)]
    [InlineData(VfMode.Legacy, 133, 128, VfLayoutId.Legacy133)]
    public void The_four_supported_layouts_parse_with_their_editable_counts(VfMode mode, int maskPoints, int editable, VfLayoutId id)
    {
        var s = Parse(new FakeVf { Mode = mode, MaskPoints = maskPoints, Editable = editable });
        Assert.Equal(id, s.Layout);
        Assert.Equal(editable, s.EditableCount);
        Assert.Equal(editable - 1, s.Points[^1].Index);
        // Legacy reports no base: it is the reported frequency minus the stored offset.
        Assert.Equal(new FakeVf().Base(editable - 1), s.Points[^1].BaseKhz);
    }

    [Theory]
    [InlineData(VfMode.Regional, 133, 127)]
    [InlineData(VfMode.Regional, 128, 127)]
    [InlineData(VfMode.Legacy, 132, 127)]
    public void Any_other_layout_is_refused(VfMode mode, int maskPoints, int editable)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(new FakeVf { Mode = mode, MaskPoints = maskPoints, Editable = editable }));
        Assert.Contains("supported layout", e.Message);
    }

    [Fact]
    public void A_hole_in_the_mask_is_refused()
    {
        var f = new FakeVf();
        var info = f.Info();
        info[VfLayout.MaskOffset + 1] &= 0xfe;   // point 8 missing
        var c = f.Control();
        Assert.Throws<InvalidDataException>(() => VfParser.Parse(info, f.Status(c), c));
    }

    [Fact]
    public void Status_and_control_must_echo_the_info_mask()
    {
        var f = new FakeVf();
        var c = f.Control();
        var s = f.Status(c);
        s[VfLayout.MaskOffset] = 0xfe;
        Assert.Contains("inconsistent V/F point masks", Assert.Throws<InvalidDataException>(() => VfParser.Parse(f.Info(), s, c)).Message);
    }

    [Fact]
    public void Regional_voltages_must_rise_strictly()
    {
        var f = new FakeVf { Voltage = i => i == 41 ? 700_000 + 40 * 5_000 : 700_000 + (uint)i * 5_000 };
        Assert.Contains("structural validation", Assert.Throws<InvalidDataException>(() => Parse(f)).Message);
    }

    [Fact]
    public void Legacy_voltages_may_repeat()
    {
        var s = Parse(new FakeVf { Mode = VfMode.Legacy, MaskPoints = 84, Editable = 80, Voltage = i => 700_000 + (uint)(i / 2) * 5_000 });
        Assert.Equal(80, s.EditableCount);
    }

    [Fact]
    public void The_voltage_demand_shift_is_reported_and_must_be_the_same_on_every_point()
    {
        Assert.Equal(-25_000, Parse(new FakeVf { ShiftUv = -25_000 }).DemandShiftUv);

        var f = new FakeVf { StatusHook = (s, _) => W(s, VfLayout.Status(70) + 0x10, 700_000 + 70 * 5_000 - 5_000) };
        Assert.Contains("inconsistent Core voltage-demand shift", Assert.Throws<InvalidDataException>(() => Parse(f)).Message);
    }

    [Fact]
    public void A_zero_base_or_a_zero_frequency_is_refused()
    {
        var noBase = new FakeVf { StatusHook = (s, _) => W(s, VfLayout.Status(3) + 0xc, 0) };
        Assert.Contains("baseline", Assert.Throws<InvalidDataException>(() => Parse(noBase)).Message);
        var noFreq = new FakeVf { Effective = (i, o) => i == 3 ? 0u : 1_000_000u };
        Assert.Contains("structural validation", Assert.Throws<InvalidDataException>(() => Parse(noFreq)).Message);
    }

    [Fact]
    public void A_legacy_curve_must_match_its_advertised_topology()
    {
        var f = new FakeVf { Mode = VfMode.Legacy, MaskPoints = 84, Editable = 80 };
        var info = f.Info();
        W(info, VfLayout.Info(81), 0);   // the second type-1 point claims to be type 0
        var c = f.Control();
        Assert.Contains("advertised topology", Assert.Throws<InvalidDataException>(() => VfParser.Parse(info, f.Status(c), c)).Message);
    }

    [Fact]
    public void Offsets_encode_as_maximal_runs_and_decode_back()
    {
        Assert.Equal("0*127", VfOffsets.Encode(new int[127]));
        int[] mixed = [.. Enumerable.Repeat(15_000, 40), -5_000, .. Enumerable.Repeat(0, 86)];
        Assert.Equal("15000*40,-5000,0*86", VfOffsets.Encode(mixed));
        Assert.Equal(mixed, VfOffsets.Decode(VfOffsets.Encode(mixed)));
        Assert.Throws<FormatException>(() => VfOffsets.Decode(""));
        Assert.Throws<FormatException>(() => VfOffsets.Decode("1*2*3"));
        Assert.Throws<FormatException>(() => VfOffsets.Decode("0*0"));
        Assert.Throws<FormatException>(() => VfOffsets.Decode("0*256"));
    }
}
