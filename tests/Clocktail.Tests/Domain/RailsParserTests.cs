using System.Buffers.Binary;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class RailsParserTests
{
    [Fact]
    public void Parses_both_rails_with_offsets_limits_and_domains()
    {
        var s = RailsFixture.Snapshot();
        Assert.Equal(3u, s.Mask);
        Assert.Equal(0, s.LegacyBoostPercent);
        Assert.Equal(2, s.Rails.Count);

        var nv = s.Get(Rail.Nvvdd)!;
        Assert.Equal(0, nv.Ordinal);
        Assert.Equal(1u, nv.Type);
        Assert.Equal(new RailOffsets(0, 0, 0, 0), nv.Offsets);
        Assert.Equal(RailsFixture.NvvddRel, nv.Limits.RelUv);
        Assert.Equal(RailsFixture.DomainMax, nv.Domain!.Value.MaxUv);
        Assert.True(nv.Writable, nv.NotWritableReason);

        var ms = s.Get(Rail.Msvdd)!;
        Assert.Equal(1, ms.Ordinal);
        Assert.Equal(3u, ms.Type);
        Assert.Equal(RailsFixture.MsvddRelOffset, ms.Offsets.RelUv);
        Assert.Equal(RailsFixture.MsvddRel - RailsFixture.MsvddRelOffset, ms.Base(RailSlot.Rel));   // base = eval − offset
        Assert.Equal(0u, ms.Limits.AltOpUv);
        Assert.True(ms.Writable, ms.NotWritableReason);
    }

    [Fact]
    public void Control_field_offsets_follow_the_ordinal_not_the_bit()
    {
        var s = RailsFixture.Snapshot();
        Assert.Equal(0x48 + 4, s.Get(Rail.Nvvdd)!.ControlFieldOffset(RailSlot.Rel));
        Assert.Equal(0x48 + 0x54 + 0x10, s.Get(Rail.Msvdd)!.ControlFieldOffset(RailSlot.Vmin));
    }

    [Fact]
    public void A_mask_mismatch_between_structures_is_refused()
    {
        var control = RailsFixture.Control();
        BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(4), 1);
        var e = Assert.Throws<InvalidDataException>(() => RailsParser.Parse(RailsFixture.Info(), control, RailsFixture.Status(), null, true));
        Assert.Contains("different rail mask", e.Message);
    }

    [Fact]
    public void Missing_nvvdd_is_refused()
    {
        var info = RailsFixture.Info();
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 2);
        Assert.Throws<InvalidDataException>(() => RailsParser.Parse(info, RailsFixture.Control(), RailsFixture.Status(), null, true));
    }

    [Fact]
    public void Zero_evaluated_limits_are_refused()
    {
        var status = RailsFixture.Status();
        BinaryPrimitives.WriteUInt32LittleEndian(status.AsSpan(RailsLayout.StatusBase + 16), 0);
        var e = Assert.Throws<InvalidDataException>(() => RailsParser.Parse(RailsFixture.Info(), RailsFixture.Control(), status, null, true));
        Assert.Contains("implausible", e.Message);
    }

    [Fact]
    public void A_wrong_version_word_is_refused()
    {
        var info = RailsFixture.Info();
        BinaryPrimitives.WriteUInt32LittleEndian(info, 0x1184c);
        Assert.Throws<InvalidDataException>(() => RailsParser.Parse(info, RailsFixture.Control(), RailsFixture.Status(), null, true));
    }

    [Fact]
    public void Without_the_setter_rails_are_read_only()
    {
        var s = RailsFixture.Snapshot(setAvailable: false);
        Assert.All(s.Rails, r => Assert.False(r.Writable));
        Assert.Contains("private voltage write", s.Rails[0].NotWritableReason);
    }

    [Fact]
    public void A_rail_whose_kind_differs_between_info_and_control_is_not_writable()
    {
        var control = RailsFixture.Control();
        BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(RailsLayout.ControlBase), 3);
        var s = RailsParser.Parse(RailsFixture.Info(), control, RailsFixture.Status(), null, true);
        Assert.False(s.Get(Rail.Nvvdd)!.Writable);
        Assert.True(s.Get(Rail.Msvdd)!.Writable);
    }

    [Fact]
    public void An_invalid_device_entry_leaves_the_domain_unknown()
    {
        var devices = RailsFixture.Devices();
        devices[RailsLayout.DevicesBase + 6] = 0;   // NVVDD's domain not valid
        var s = RailsParser.Parse(RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status(), devices, true);
        Assert.Null(s.Get(Rail.Nvvdd)!.Domain);
        Assert.NotNull(s.Get(Rail.Msvdd)!.Domain);
    }

    [Fact]
    public void The_reader_propagates_the_mask_and_parses_the_live_structures()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo], [Slot.VoltVoltRailsSetControl]);
        var s = RailsReader.Read(new NvClient(api), new GpuSession(1, "fake"));
        Assert.Equal(2, s.Rails.Count);
        Assert.True(s.Rails.All(r => r.Writable));
        Assert.Equal(4, FakeDriver.Gets);
    }

    [Fact]
    public void Without_a_devices_table_the_reader_still_returns_rails()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus]);
        var s = RailsReader.Read(new NvClient(api), new GpuSession(1, "fake"));
        Assert.Null(s.RawDevices);
        Assert.All(s.Rails, r => Assert.Null(r.Domain));
        Assert.All(s.Rails, r => Assert.False(r.Writable));   // no setter wired
    }
}
