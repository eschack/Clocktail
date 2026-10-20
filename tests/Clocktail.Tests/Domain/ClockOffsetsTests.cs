using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class ClockOffsetsTests : IDisposable
{
    private const int CoreDelta = 0x14 + 8 + 0xc;
    private const int CoreFreq = 0x14 + 8 + 0x18;    // data.range.minFreq_kHz: the driver moves it with the offset
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;

    public ClockOffsetsTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Table(int coreDelta)
    {
        var b = NvClient.NewBuffer(Slot.GetPstates20);
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        W(4, 1); W(8, 1); W(0xc, 1);
        int p = Pstates20Layout.Pstate(0); W(p, 0); W(p + 4, 1);
        int c = Pstates20Layout.Clock(0, 0); W(c, 0); W(c + 4, 1); W(c + 8, 1); I(c + 0xc, coreDelta); I(c + 0x10, -1_000_000); I(c + 0x14, 1_000_000); I(c + 0x18, 2_407_000 + coreDelta); I(c + 0x1c, 3_090_000 + coreDelta);
        return b;
    }

    private (ClockOffsets co, GpuSession gpu, NvClient hw) Setup(int coreDelta = 0, int clampKhz = 400_000)
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.GetPstates20).Shape!.Value.Word] = Table(coreDelta);
        FakeDriver.RequestApplier = (state, req) =>
        {
            var next = (byte[])state.Clone();
            int delta = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(0x28));
            if (Math.Abs(delta) > clampKhz) delta = clampKhz * Math.Sign(delta);
            int old = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(CoreDelta));
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreDelta), delta);
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreFreq), BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(CoreFreq)) + delta - old);
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreFreq + 4), BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(CoreFreq + 4)) + delta - old);
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor([Slot.GetPstates20], [Slot.SetPstates20]));
        return (new ClockOffsets(hw, _journal, _sentinel, _host), new GpuSession(1, "fake"), hw);
    }

    /// <summary>The live 2026-10-17 17:14 case: `--core-offset 15` in one process, `--core-offset 0` in the next.
    /// Every CLI run is a new pid, so the record's owner is always "gone" by the time the user writes again.</summary>
    [Fact]
    public void A_record_left_by_a_finished_process_is_adopted_and_writing_the_original_releases_it()
    {
        var (co, gpu, _) = Setup();
        _host.Pid = 18812;
        var up = co.Apply(gpu, ClockDomain.Core, 15_000);
        Assert.Equal(TxOutcome.Applied, up.Tx.Outcome);
        _host.Alive.Remove(18812); _host.Pid = 100;   // the next `dotnet run`
        Assert.Equal(RecoveryReason.OwnerDied, _journal.Inspect().Reason);

        var back = co.Apply(gpu, ClockDomain.Core, 0);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Equal(0, back.AfterKhz);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void An_adopted_record_keeps_the_first_original_through_further_writes()
    {
        var (co, gpu, _) = Setup();
        _host.Pid = 18812;
        co.Apply(gpu, ClockDomain.Core, 15_000);
        _host.Alive.Remove(18812); _host.Pid = 100;
        var more = co.Apply(gpu, ClockDomain.Core, 30_000);
        Assert.Equal(TxOutcome.Applied, more.Tx.Outcome);
        var state = _journal.Load()!;
        Assert.Equal(100, state.Pid);
        Assert.Equal("0", Assert.Single(state.Items).Original);   // not 15000: the hardware was at 0 before the first of ours
        Assert.StartsWith("held: original 0", more.Ownership);
    }

    /// <summary>
    /// The write-ahead rule in one test: when the driver is told to change the offset, the journal already holds the
    /// original, and that original comes from the one read the write was planned on. Before the shared owned write,
    /// the original came from an earlier read of its own; a tool changing the offset in between would have made
    /// the record wrong.
    /// </summary>
    [Fact]
    public void The_original_is_on_disk_before_the_write_and_comes_from_the_read_the_write_was_planned_on()
    {
        var (co, gpu, _) = Setup(coreDelta: 30_000);
        string? originalAtWrite = null;
        int getsAtWrite = -1;
        FakeDriver.OnSet = () =>
        {
            if (getsAtWrite >= 0) return;
            getsAtWrite = FakeDriver.Gets;
            originalAtWrite = _journal.Load()?.Items.Single().Original;
        };
        var r = co.Apply(gpu, ClockDomain.Core, 45_000);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal("30000", originalAtWrite);
        Assert.Equal(1, getsAtWrite);                       // one read before the write: the snapshot the plan and the record share
        Assert.Contains(r.Tx.Log, l => l.StartsWith("journal: pstates20/core claimed"));
    }

    /// <summary>Live 2026-10-18: a memory offset written 22 s after a curve reset every point offset to 0 in the middle
    /// of a stability run. A P0 offset that would change is refused while the curve holds any offset.</summary>
    [Fact]
    public void A_P0_offset_that_would_wipe_a_curve_in_force_is_refused()
    {
        var raw = Table(0);
        var curve = new VfBudget([(29, 0), (30, 15_000)], null);
        var refused = Assert.IsType<Mutation.Abort>(ClockOffsets.Plan(ClockDomain.Core, 15_000, curve)(raw));
        Assert.Contains("resets every one of them to 0", refused.Reason);
        Assert.Contains("1 V/F point offset(s)", refused.Reason);

        Assert.IsType<Mutation.Apply>(ClockOffsets.Plan(ClockDomain.Core, 0, curve)(raw));              // equal value: nothing is written, nothing wiped
        Assert.IsType<Mutation.Request>(ClockOffsets.Plan(ClockDomain.Core, 15_000, new VfBudget([(30, 0)], null))(raw));   // a flat curve has nothing to lose
        Assert.IsType<Mutation.Request>(ClockOffsets.Plan(ClockDomain.Core, 15_000)(raw));            // no V/F structure at all
    }

    [Fact]
    public void A_table_the_parser_cannot_read_aborts_without_a_write_or_a_sentinel()
    {
        var (co, gpu, _) = Setup();
        BitConverter.GetBytes(1000u).CopyTo(FakeDriver.Current(Slot.GetPstates20), 8);   // claims 1000 P-states
        var r = co.Apply(gpu, ClockDomain.Core, 15_000);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("nothing was written", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));           // Aborted is total: no byte reached the driver
        Assert.StartsWith("nothing held", r.Ownership);
    }

    [Fact]
    public void The_drill_equal_then_plus_then_back_claims_and_releases()
    {
        var (co, gpu, hw) = Setup();

        var same = co.Apply(gpu, ClockDomain.Core, 0);
        Assert.True(same.Tx.Outcome == TxOutcome.NoChange, same.Tx.ToString());
        Assert.Null(_journal.Load());                       // nothing claimed for an equal-value request
        Assert.StartsWith("nothing held", same.Ownership);  // and the result does not pretend something was released
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.Equal(0, FakeDriver.Sets);

        var up = co.Apply(gpu, ClockDomain.Core, 15_000);
        Assert.Equal(TxOutcome.Applied, up.Tx.Outcome);   // the frequency field moved too (live 617.42 behaviour); it is status, not a rejection
        Assert.Equal(15_000, up.AfterKhz);
        Assert.Equal(2_407_000, up.FreqBeforeKhz);
        Assert.Equal(2_422_000, up.FreqAfterKhz);
        var owned = Assert.Single(_journal.Load()!.Items);
        Assert.Equal("0", owned.Original);
        Assert.StartsWith("held", up.Ownership);
        Assert.False(_sentinel.IsBlocked(out _));

        var up2 = co.Apply(gpu, ClockDomain.Core, 30_000);   // a second step keeps the first original
        Assert.Equal("0", Assert.Single(_journal.Load()!.Items).Original);
        Assert.Equal(15_000, up2.BeforeKhz);

        var back = co.Apply(gpu, ClockDomain.Core, 0);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Null(_journal.Load());
        Assert.StartsWith("released", back.Ownership);
        Assert.Equal(0, Pstates20Reader.Read(hw, gpu).P0(ClockDomain.Core)!.DeltaKhz);
    }

    [Fact]
    public void A_clamped_write_is_restored_and_the_journal_released()
    {
        var (co, gpu, hw) = Setup(coreDelta: 100_000);
        var r = co.Apply(gpu, ClockDomain.Core, 900_000);
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Null(_journal.Load());
        Assert.True(r.SentinelCleared);
        Assert.Equal(100_000, Pstates20Reader.Read(hw, gpu).P0(ClockDomain.Core)!.DeltaKhz);
    }

    [Fact]
    public void A_stale_sentinel_blocks_unless_taken_over()
    {
        var (co, gpu, _) = Setup();
        File.WriteAllText(_sentinel.Path, "Clocktail auto apply in progress\n");
        var e = Assert.Throws<InvalidOperationException>(() => co.Apply(gpu, ClockDomain.Core, 15_000));
        Assert.Contains("did not complete", e.Message);
        Assert.Equal(0, FakeDriver.Sets);

        var r = co.Apply(gpu, ClockDomain.Core, 15_000, takeOver: true);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_record_held_by_a_live_process_stops_the_write()
    {
        var (co, gpu, _) = Setup();
        new OwnershipJournal(_journal.Path, new FakeHost { Pid = 7 }, "gpu-A")
            .Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now));
        _host.Alive.Add(7);   // a dead pid 7 would be adopted instead
        var ex = Assert.Throws<InvalidOperationException>(() => co.Apply(gpu, ClockDomain.Core, 15_000));
        Assert.Contains("OwnedByLiveProcess", ex.Message);
        Assert.Equal(7, _journal.Load()!.Pid);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.False(_sentinel.IsBlocked(out _));   // refused before the transaction: nothing touched, no sentinel left (live 17:34 case)
    }
}
