using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class CapabilitiesTests
{
    [Fact]
    public void A_5090_like_driver_reports_volt_boost_and_pstates()
    {
        RailsFixture.SeedDriver();
        FakeDriver.Seed(Slot.ClientVoltRailsGetControl, b => b[4] = 25);
        FakeDriver.Seed(Slot.GetPstates20, b => { b[4] = 1; b[8] = 2; b[0xc] = 3; });
        var api = FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.ClientVoltRailsGetControl, Slot.GetPstates20],
            [Slot.VoltVoltRailsSetControl, Slot.ClientVoltRailsSetControl, Slot.SetPstates20]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.True(c.VoltRead.Available, c.VoltRead.Reason);
        Assert.Contains("0x3", c.VoltRead.Reason);
        Assert.True(c.VoltWrite.Available);
        Assert.Equal(VoltBoostPath.Independent, c.Boost);
        Assert.True(c.Pstates.Available);
        Assert.Contains("2 P-states, 3 clocks", c.Pstates.Reason);
        Assert.True(c.PstatesWrite.Available);
        Assert.False(c.FanCoolers.Available);
        Assert.Contains("missing", c.FanCoolers.Reason);
        Assert.False(c.Presence.ContainsKey(Slot.RegisterOp));
    }

    [Fact]
    public void Without_the_independent_boost_the_legacy_byte_is_the_path()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus], [Slot.VoltVoltRailsSetControl]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.Equal(VoltBoostPath.Legacy, c.Boost);
        Assert.False(c.Pstates.Available);
    }

    [Fact]
    public void A_driver_without_nvvdd_has_no_voltage_path_at_all()
    {
        RailsFixture.SeedDriver();
        BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.Current(Slot.VoltVoltRailsGetInfo).AsSpan(4), 2);
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus], [Slot.VoltVoltRailsSetControl]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.False(c.VoltRead.Available);
        Assert.False(c.VoltWrite.Available);
        Assert.Equal(VoltBoostPath.None, c.Boost);
    }
}
