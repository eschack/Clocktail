using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

public unsafe class NvClientTests
{
    private static GpuSession Gpu() => new(0x1234, "test gpu");

    private static byte[] Buf(Slot s) => NvClient.NewBuffer(s);

    [Fact]
    public void Successful_read_copies_the_reply_back()
    {
        var api = new FakeApi().With(Slot.VoltVoltRailsGetControl, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        var buf = Buf(Slot.VoltVoltRailsGetControl);
        var r = hw.Call(Gpu(), Slot.VoltVoltRailsGetControl, buf);
        Assert.True(r.IsOk, r.ToString());
        Assert.Equal(0x42, buf[8]);
    }

    [Fact]
    public void Failed_status_leaves_the_buffer_untouched()
    {
        var api = new FakeApi().With(Slot.VoltVoltRailsGetControl, &FakeApi.Fails);
        var buf = Buf(Slot.VoltVoltRailsGetControl);
        var r = new NvClient(api).Call(Gpu(), Slot.VoltVoltRailsGetControl, buf);
        Assert.Equal(NvOutcome.Nvapi, r.Outcome);
        Assert.Equal(-9, r.Status);
        Assert.Equal(0, buf[8]);
    }

    [Fact]
    public void Missing_interface_is_a_capability_gap_not_an_exception()
    {
        var r = new NvClient(new FakeApi()).Call(Gpu(), Slot.VoltVoltRailsGetControl, Buf(Slot.VoltVoltRailsGetControl));
        Assert.Equal(NvOutcome.Missing, r.Outcome);
    }

    [Theory]
    [InlineData(NvStatus.HandleInvalidated)]
    [InlineData(NvStatus.InvalidHandle)]
    public void Handle_loss_poisons_the_session_and_later_calls_never_reach_the_driver(int status)
    {
        var api = new FakeApi();
        if (status == NvStatus.HandleInvalidated) api.With(Slot.VoltVoltRailsGetControl, &FakeApi.HandleInvalidated);
        else api.With(Slot.VoltVoltRailsGetControl, &FakeApi.InvalidHandle);
        api.With(Slot.VoltVoltRailsGetStatus, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        var gpu = Gpu();

        var first = hw.Call(gpu, Slot.VoltVoltRailsGetControl, Buf(Slot.VoltVoltRailsGetControl));
        Assert.Equal(NvOutcome.Nvapi, first.Outcome);
        Assert.Equal(status, first.Status);
        Assert.True(gpu.Faulted);
        Assert.Equal(status, gpu.FaultStatus);

        FakeApi.Calls = 0;
        var second = hw.Call(gpu, Slot.VoltVoltRailsGetStatus, Buf(Slot.VoltVoltRailsGetStatus));
        Assert.Equal(NvOutcome.Poisoned, second.Outcome);
        Assert.Equal(0, FakeApi.Calls);

        gpu.Rebind(0x5678);
        Assert.False(gpu.Faulted);
        Assert.Equal(1, gpu.Generation);
        Assert.True(hw.Call(gpu, Slot.VoltVoltRailsGetStatus, Buf(Slot.VoltVoltRailsGetStatus)).IsOk);
        Assert.Equal(1, FakeApi.Calls);
    }

    [Fact]
    public void Writes_past_the_structure_are_caught_and_the_reply_discarded()
    {
        // ClientVoltRailsGetControl is 0x28 bytes; the stand-in writes byte 0x28.
        var api = new FakeApi().With(Slot.ClientVoltRailsGetControl, &FakeApi.WritesPastEndOfBoost);
        var buf = Buf(Slot.ClientVoltRailsGetControl);
        var r = new NvClient(api).Call(Gpu(), Slot.ClientVoltRailsGetControl, buf);
        Assert.Equal(NvOutcome.GuardBroken, r.Outcome);
        Assert.Contains("1 bytes past", r.Detail);
    }

    [Fact]
    public void Writes_before_the_structure_are_caught()
    {
        var api = new FakeApi().With(Slot.ClientVoltRailsGetControl, &FakeApi.WritesBefore);
        var r = new NvClient(api).Call(Gpu(), Slot.ClientVoltRailsGetControl, Buf(Slot.ClientVoltRailsGetControl));
        Assert.Equal(NvOutcome.GuardBroken, r.Outcome);
        Assert.Contains("1 bytes before", r.Detail);
    }

    [Fact]
    public void Wrong_size_or_version_word_never_reaches_the_driver()
    {
        var api = new FakeApi().With(Slot.VoltVoltRailsGetControl, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        FakeApi.Calls = 0;

        var wrongSize = new byte[0xac8 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(wrongSize, 0x20ac8);
        Assert.Equal(NvOutcome.Rejected, hw.Call(Gpu(), Slot.VoltVoltRailsGetControl, wrongSize).Outcome);

        var wrongWord = new byte[0xac8];
        BinaryPrimitives.WriteUInt32LittleEndian(wrongWord, 0x10ac8);
        Assert.Equal(NvOutcome.Rejected, hw.Call(Gpu(), Slot.VoltVoltRailsGetControl, wrongWord).Outcome);

        Assert.Equal(0, FakeApi.Calls);
        Assert.Empty(api.Resolved);   // rejected before resolution, so a bad request cannot even probe the driver
    }

    [Fact]
    public void Write_that_fails_its_policy_never_reaches_the_driver()
    {
        var api = new FakeApi().With(Slot.ClientVoltRailsSetControl, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        var buf = Buf(Slot.ClientVoltRailsSetControl);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 101);   // boost > 100 %
        FakeApi.Calls = 0;
        var r = hw.Call(Gpu(), Slot.ClientVoltRailsSetControl, buf);
        Assert.Equal(NvOutcome.Rejected, r.Outcome);
        Assert.Contains("at most 100", r.Detail);
        Assert.Equal(0, FakeApi.Calls);

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 100);
        Assert.True(hw.Call(Gpu(), Slot.ClientVoltRailsSetControl, buf).IsOk);
        Assert.Equal(1, FakeApi.Calls);
    }

    [Fact]
    public void Disabled_setters_are_refused_even_with_a_valid_buffer()
    {
        var api = new FakeApi().With(Slot.ThermalPolicySetControl, &FakeApi.WellBehaved);
        FakeApi.Calls = 0;
        var r = new NvClient(api).Call(Gpu(), Slot.ThermalPolicySetControl, Buf(Slot.ThermalPolicySetControl));
        Assert.Equal(NvOutcome.Rejected, r.Outcome);
        Assert.Contains("disabled", r.Detail);
        Assert.Equal(0, FakeApi.Calls);
    }

    [Fact]
    public void Forbidden_slot_is_refused_before_anything_else()
    {
        var api = new FakeApi().With(Slot.RegisterOp, &FakeApi.WellBehaved);
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x10010);
        var r = new NvClient(api).Call(Gpu(), Slot.RegisterOp, buf);
        Assert.Equal(NvOutcome.Rejected, r.Outcome);
        Assert.Empty(api.Resolved);
    }

    [Fact]
    public void Indexed_calls_pass_the_index_and_struct_calls_refuse_one()
    {
        var api = new FakeApi().With(Slot.GetCoolerSettings, &FakeApi.EchoesIndex).With(Slot.VoltVoltRailsGetControl, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        var buf = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x10020);   // legacy structure: size supplied by the caller
        Assert.True(hw.Call(Gpu(), Slot.GetCoolerSettings, 3, buf).IsOk);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)));
        Assert.Equal(NvOutcome.Rejected, hw.Call(Gpu(), Slot.GetCoolerSettings, buf).Outcome);
        Assert.Equal(NvOutcome.Rejected, hw.Call(Gpu(), Slot.VoltVoltRailsGetControl, 0, Buf(Slot.VoltVoltRailsGetControl)).Outcome);
    }

    [Fact]
    public void Unknown_shape_requires_the_caller_to_set_a_version_word()
    {
        var api = new FakeApi().With(Slot.GetCoolerSettings, &FakeApi.EchoesIndex);
        var r = new NvClient(api).Call(Gpu(), Slot.GetCoolerSettings, 0, new byte[0x20]);
        Assert.Equal(NvOutcome.Rejected, r.Outcome);
        Assert.Throws<InvalidOperationException>(() => NvClient.NewBuffer(Slot.GetCoolerSettings));
    }

    [Fact]
    public void Scalar_reads()
    {
        var api = new FakeApi().With(Slot.GetTachReading, &FakeApi.ScalarSeven);
        var hw = new NvClient(api);
        Assert.True(hw.ReadScalar(Gpu(), Slot.GetTachReading, out uint v).IsOk);
        Assert.Equal(7u, v);
        Assert.Equal(NvOutcome.Rejected, hw.ReadScalar(Gpu(), Slot.VoltVoltRailsGetInfo, out _).Outcome);
        Assert.Equal(NvOutcome.Rejected, hw.Call(Gpu(), Slot.GetTachReading, new byte[4]).Outcome);
    }

    [Fact]
    public void Sentinel_domain_read_checks_the_tail_and_the_minus_one_answer()
    {
        var ok = new NvClient(new FakeApi().With(Slot.SingleDomainFrequencyRead, &FakeApi.DomainTimesTen));
        Assert.True(ok.ReadDomain(Gpu(), Slot.SingleDomainFrequencyRead, 6, out uint v).IsOk);
        Assert.Equal(60u, v);

        var minusOne = new NvClient(new FakeApi().With(Slot.SingleDomainFrequencyRead, &FakeApi.DomainMinusOne));
        var r = minusOne.ReadDomain(Gpu(), Slot.SingleDomainFrequencyRead, 6, out _);
        Assert.Equal(NvOutcome.Nvapi, r.Outcome);
        Assert.Equal(NvStatus.Error, r.Status);

        var spills = new NvClient(new FakeApi().With(Slot.SingleDomainFrequencyRead, &FakeApi.DomainSpillsTail));
        Assert.Equal(NvOutcome.GuardBroken, spills.ReadDomain(Gpu(), Slot.SingleDomainFrequencyRead, 6, out _).Outcome);
    }

    [Fact]
    public void Typed_structs_must_have_the_slot_size()
    {
        var api = new FakeApi().With(Slot.ClientVoltRailsGetControl, &FakeApi.WellBehaved);
        var hw = new NvClient(api);
        var boost = NvClient.NewStruct<Boost>(Slot.ClientVoltRailsGetControl);
        Assert.Equal(0x10028u, boost.Version);
        Assert.True(hw.Call(Gpu(), Slot.ClientVoltRailsGetControl, ref boost).IsOk);
        Assert.Equal(0x42u, boost.Percent);   // the stand-in writes byte 8, the low byte of Percent
        Assert.Throws<InvalidOperationException>(() => NvClient.NewStruct<Boost>(Slot.VoltVoltRailsGetControl));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x28)]
    private struct Boost
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Version;
        [System.Runtime.InteropServices.FieldOffset(4)] public uint Reserved;
        [System.Runtime.InteropServices.FieldOffset(8)] public uint Percent;
    }

    [Fact]
    public void Presence_skips_forbidden_slots()
    {
        var api = new FakeApi().With(Slot.VoltVoltRailsGetInfo, &FakeApi.WellBehaved);
        var p = new NvClient(api).Presence();
        Assert.True(p[Slot.VoltVoltRailsGetInfo]);
        Assert.False(p[Slot.SetPstates20]);
        Assert.False(p.ContainsKey(Slot.RegisterOp));
    }
}
