using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class Pstates20Tests
{
    private const int CoreDelta = 0x14 + 8 + 0xc;               // P0, clock 0 delta
    private const int MemDelta = 0x14 + 8 + 0x2c + 0xc;          // P0, clock 1 delta

    private static byte[] Table(int coreDelta = 0, int memDelta = 0, bool editable = true)
    {
        var b = NvClient.NewBuffer(Slot.GetPstates20);
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        W(4, editable ? 1u : 0u); W(8, 1); W(0xc, 2); W(0x10, 1);
        int p = Pstates20Layout.Pstate(0);
        W(p, 0); W(p + 4, 1);
        int c = Pstates20Layout.Clock(0, 0);
        W(c, 0); W(c + 4, 1); W(c + 8, 1); I(c + 0xc, coreDelta); I(c + 0x10, -1_000_000); I(c + 0x14, 1_000_000); I(c + 0x18, 2_407_000 + coreDelta); I(c + 0x1c, 3_090_000 + coreDelta);
        int m = Pstates20Layout.Clock(0, 1);
        W(m, 4); W(m + 4, 0); W(m + 8, 1); I(m + 0xc, memDelta); I(m + 0x10, -2_000_000); I(m + 0x14, 6_000_000);
        return b;
    }

    [Fact]
    public void Parses_the_public_layout()
    {
        var s = Pstates20Parser.Parse(Table(coreDelta: 150_000, memDelta: 1_000_000));
        Assert.True(s.Editable);
        Assert.Equal(2, s.Clocks.Count);
        var core = s.P0(ClockDomain.Core)!;
        Assert.Equal(150_000, core.DeltaKhz);
        Assert.Equal(-1_000_000, core.MinKhz);
        Assert.Equal(CoreDelta, core.DeltaOffset);
        Assert.Equal(2_557_000, core.FreqKhz);
        Assert.Equal(3_240_000, core.FreqMaxKhz);
        Assert.Equal(1_000_000, s.P0(ClockDomain.Memory)!.DeltaKhz);
        Assert.Equal(MemDelta, s.P0(ClockDomain.Memory)!.DeltaOffset);
    }

    [Fact]
    public void The_request_passes_the_write_policy_and_carries_domain_and_delta()
    {
        var req = Pstates20Parser.BuildRequest(ClockDomain.Memory, 500_000);
        Assert.Null(WritePolicySet.Default.Check(Slot.SetPstates20, req));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(0x1c)));
        Assert.Equal(500_000, BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(0x28)));
    }

    [Fact]
    public void Out_of_range_or_uneditable_requests_abort()
    {
        var s = Pstates20Parser.Parse(Table());
        Assert.Contains("exceeds the driver range", Assert.IsType<Mutation.Abort>(Pstates20Rules.Plan(s, ClockDomain.Core, 1_500_000)).Reason);
        Assert.IsType<Mutation.Apply>(Pstates20Rules.Plan(s, ClockDomain.Core, 0));   // equal value: no edits
        var locked = Pstates20Parser.Parse(Table(editable: false));
        Assert.Contains("not editable", Assert.IsType<Mutation.Abort>(Pstates20Rules.Plan(locked, ClockDomain.Core, 1)).Reason);
    }

    /// <summary>
    /// The first live +15 MHz on 617.42: the delta landed and the driver also moved data.range.minFreq_kHz (+0x18 in
    /// the entry). A strict whole-structure compare called that a rejection and then could not verify its own
    /// correct restore. With the data union declared volatile the same driver answer is simply Applied.
    /// </summary>
    [Fact]
    public void A_driver_that_moves_the_frequency_with_the_offset_is_applied_not_rejected()
    {
        var (tx, gpu, _) = Setup();
        FakeDriver.RequestApplier = (state, req) =>
        {
            var next = (byte[])state.Clone();
            int delta = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(0x28));
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreDelta), delta);
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreDelta + 0xc), 2_407_000 + delta);   // +0x18 of the entry
            return next;
        };
        Func<ReadOnlyMemory<byte>, Mutation> plus15 = raw => Pstates20Rules.Plan(Pstates20Parser.Parse(raw.ToArray()), ClockDomain.Core, 15_000);

        var strict = tx.Run(gpu, new TransactionSpec(Slot.GetPstates20, Slot.SetPstates20), plus15);
        Assert.Equal(TxOutcome.Failed, strict.Outcome);
        Assert.Equal(RestoreState.RestoreFailed, strict.Restore);
        Assert.Contains("+0x34", strict.Detail);                        // exactly the live message
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.Current(Slot.GetPstates20).AsSpan(CoreDelta)));   // the restore did land

        var lenient = tx.Run(gpu, Spec, plus15);
        Assert.Equal(TxOutcome.Applied, lenient.Outcome);
        Assert.Equal(15_000, Pstates20Parser.Parse(lenient.After!).P0(ClockDomain.Core)!.DeltaKhz);
        Assert.Equal(2_422_000, Pstates20Parser.Parse(lenient.After!).P0(ClockDomain.Core)!.FreqKhz);
    }

    private static (Transaction tx, GpuSession gpu, NvClient hw) Setup(int coreDelta = 0)
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.GetPstates20).Shape!.Value.Word] = Table(coreDelta);
        FakeDriver.RequestApplier = (state, req) =>
        {
            var next = (byte[])state.Clone();
            uint domain = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(0x1c));
            int delta = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(0x28));
            if (Math.Abs(delta) > 400_000) delta = 400_000 * Math.Sign(delta);   // a driver that silently clamps
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(domain == 0 ? CoreDelta : MemDelta), delta);
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor([Slot.GetPstates20], [Slot.SetPstates20]));
        return (new Transaction(hw), new GpuSession(1, "fake"), hw);
    }

    private static readonly TransactionSpec Spec = Pstates20Layout.Spec;

    [Fact]
    public void Request_mode_writes_the_request_and_verifies_the_table()
    {
        var (tx, gpu, hw) = Setup();
        var r = tx.Run(gpu, Spec, raw => Pstates20Rules.Plan(Pstates20Parser.Parse(raw.ToArray()), ClockDomain.Core, 150_000));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(150_000, Pstates20Reader.Read(hw, gpu).P0(ClockDomain.Core)!.DeltaKhz);
        Assert.Equal(1, FakeDriver.Sets);
    }

    [Fact]
    public void A_clamped_request_is_detected_and_the_old_offset_restored_by_a_request()
    {
        var (tx, gpu, hw) = Setup(coreDelta: 100_000);
        var r = tx.Run(gpu, Spec, raw => Pstates20Rules.Plan(Pstates20Parser.Parse(raw.ToArray()), ClockDomain.Core, 900_000));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.Restored, r.Restore);
        Assert.Equal(100_000, Pstates20Reader.Read(hw, gpu).P0(ClockDomain.Core)!.DeltaKhz);
        Assert.Equal(2, FakeDriver.Sets);
    }

    [Fact]
    public void Request_mode_without_a_restore_request_says_so()
    {
        var (tx, gpu, _) = Setup();
        var r = tx.Run(gpu, Spec, _ => new Mutation.Request(Pstates20Parser.BuildRequest(ClockDomain.Core, 900_000), [Edit.Int32(CoreDelta, 900_000)], null));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.NotAttempted, r.Restore);
        Assert.Contains("no way to restore", r.Detail);
    }
}
