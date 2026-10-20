using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The rail write path against a fake driver that behaves like the RTX 5090 did live:
/// evaluated = base + offset, MAX = min(REL, ALT, OV), domain 445–1280 mV in 5 mV steps.
/// </summary>
[Collection("fake-driver")]
public class RailWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    /// <summary>The driver's own bases per rail: REL, ALT/OP, OV, VMIN (µV).</summary>
    private static readonly uint[][] Bases = [[1_095_000, 1_115_000, 1_200_000, 800_000], [1_095_000, 1_115_000, 1_200_000, 825_000]];
    /// <summary>Added to the derived MAX of every rail: a driver whose MAX is not what the offsets predict.</summary>
    private uint _maxBias;
    /// <summary>MSVDD's REL/ALT/OV bases follow NVVDD's evaluated values: the coupling the 5090's numbers allow.</summary>
    private bool _coupled;
    private static readonly uint StatusWord = Slots.Info(Slot.VoltVoltRailsGetStatus).Shape!.Value.Word;
    private static readonly uint ControlWord = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;

    public RailWritesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    private static int Field(Rail r, RailSlot s) => RailsLayout.ControlBase + (int)r * RailsLayout.ControlEntry + 4 + 4 * (int)s;

    private (RailWrites rails, NvClient hw) Setup()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
        W(info, 4, 3u);
        for (int bit = 0; bit < 2; bit++)
        {
            int ib = RailsLayout.InfoBase + bit * RailsLayout.InfoEntry;
            byte domain = (byte)(bit == 0 ? 0 : 2);
            W(info, ib, 3u); W(info, ib + 4, bit == 0 ? 1u : 3u); info[ib + 8] = domain; W(info, ib + 0x18, 1u << domain);
        }
        var control = NvClient.NewBuffer(Slot.VoltVoltRailsGetControl);
        W(control, 4, 3u);
        W(control, RailsLayout.ControlBase, 3u);
        W(control, RailsLayout.ControlBase + RailsLayout.ControlEntry, 3u);
        W(control, Field(Rail.Msvdd, RailSlot.Rel), -50_000);
        var devices = NvClient.NewBuffer(Slot.VoltVoltDevicesGetInfo);
        foreach (var (i, type) in new[] { (0, 1), (2, 3) })
        {
            int d = RailsLayout.DevicesBase + i * RailsLayout.DevicesEntry;
            devices[d + 4] = (byte)type; devices[d + 6] = 1;
            W(devices, d + 8, 445_000u); W(devices, d + 12, 1_280_000u); W(devices, d + 16, 5_000u);
        }
        FakeDriver.State[BitConverter.ToUInt32(info)] = info;
        FakeDriver.State[ControlWord] = control;
        FakeDriver.State[BitConverter.ToUInt32(devices)] = devices;
        FakeDriver.Seed(Slot.VoltVoltRailsGetStatus);
        FakeDriver.QueryResponder = q => BitConverter.ToUInt32(q) == StatusWord ? Status(FakeDriver.State[ControlWord]) : null;

        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.VoltVoltRailsSetControl]));
        return (new RailWrites(hw, _journal, _sentinel, _host), hw);
    }

    /// <summary>Status derived from the control structure the way the 5090 reported it.</summary>
    private byte[] Status(byte[] control)
    {
        var s = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        W(s, 4, 3u);
        var nv = RailWrites.OffsetsIn(control, Rail.Nvvdd)!.Value;
        for (int n = 0; n < 2; n++)
        {
            var o = RailWrites.OffsetsIn(control, (Rail)n)!.Value;
            var b = n == 1 && _coupled
                ? [Bases[0][0] + nv.RelUv, Bases[0][1] + nv.AltUv, Bases[0][2] + nv.OvUv, Bases[1][3]]
                : Bases[n].Select(x => (long)x).ToArray();
            uint rel = (uint)(b[0] + o.RelUv), alt = (uint)(b[1] + o.AltUv), ov = (uint)(b[2] + o.OvUv), vmin = (uint)(b[3] + o.VminUv);
            int sb = RailsLayout.StatusBase + n * RailsLayout.StatusEntry;
            W(s, sb + 4, rel); W(s, sb + 8, alt); W(s, sb + 12, ov); W(s, sb + 16, Math.Min(rel, Math.Min(alt, ov)) + _maxBias); W(s, sb + 20, vmin);
        }
        return s;
    }

    private static RailRules.Request Rel(int uv) => new(null, uv, null, null);
    private static int ControlField(Rail r, RailSlot s) => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.State[ControlWord].AsSpan(Field(r, s)));

    [Fact]
    public void The_canonical_encoding_round_trips_and_reads_from_the_control_structure_alone()
    {
        var o = new RailOffsets(-50_000, 0, 5_000, -10_000);
        Assert.Equal("rel=-50000,alt=0,ov=5000,vmin=-10000", RailWrites.Encode(o));
        Assert.Equal(o, RailWrites.Decode(RailWrites.Encode(o)));
        Setup();
        Assert.Equal("rel=-50000,alt=0,ov=0,vmin=0", RailWrites.Cell(Rail.Msvdd, 3).StateIn(FakeDriver.State[ControlWord]));
        var nvvddOnly = (byte[])FakeDriver.State[ControlWord].Clone(); W(nvvddOnly, 4, 1u);
        Assert.Null(RailWrites.Cell(Rail.Msvdd, 1).StateIn(nvvddOnly));
    }

    [Fact]
    public void Equal_value_is_no_change_with_no_set_call_and_nothing_held()
    {
        var (rails, _) = Setup();
        var r = rails.Apply(_gpu, Rail.Msvdd, Rel(-50_000));
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.True(r.SentinelCleared);
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.StartsWith("nothing held", r.Ownership);
        Assert.Equal(r.LimitsBefore, r.LimitsAfter);
    }

    [Fact]
    public void Smallest_step_down_lands_moves_MAX_and_is_released_when_put_back()
    {
        var (rails, _) = Setup();
        var down = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.True(down.Succeeded, down.Tx.ToString());
        Assert.Equal(TxOutcome.Applied, down.Tx.Outcome);
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        Assert.Equal(1_090_000u, down.LimitsAfter!.Value.MaxUv);
        Assert.Empty(down.Surprises);
        Assert.Null(down.Violation);
        Assert.StartsWith("held: original rel=0,alt=0,ov=0,vmin=0", down.Ownership);
        Assert.Contains(down.Tx.Log, l => l.StartsWith("journal: rails/nvvdd claimed"));
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));   // the other rail is untouched

        var back = rails.Apply(_gpu, Rail.Nvvdd, Rel(0));
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_raise_is_allowed_up_to_the_standard_ceiling_and_refused_one_step_beyond_without_a_set_call()
    {
        var (rails, _) = Setup();
        var over = rails.Apply(_gpu, Rail.Nvvdd, Rel(60_000));   // 1.095 + 0.060 = 1.155 V > 1.15 V
        Assert.Equal(TxOutcome.Aborted, over.Tx.Outcome);
        Assert.Contains("above the firmware-reported ceiling of 1150 mV", over.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());

        var top = rails.Apply(_gpu, Rail.Nvvdd, Rel(55_000));    // exactly 1.150 V
        Assert.Equal(TxOutcome.Applied, top.Tx.Outcome);
        Assert.Equal(1_150_000u, top.LimitsAfter!.Value.RelUv);
        Assert.Equal(1_115_000u, top.LimitsAfter!.Value.MaxUv);   // ALT/OP 1.115 V now bounds MAX
    }

    [Fact]
    public void OV_can_be_lowered_but_never_raised_because_it_already_sits_above_the_ceiling()
    {
        var (rails, _) = Setup();
        var up = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, null, 5_000));
        Assert.Equal(TxOutcome.Aborted, up.Tx.Outcome);
        Assert.Contains("OV would rise to 1205 mV", up.Tx.Detail);
        var down = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, null, -5_000));
        Assert.Equal(TxOutcome.Applied, down.Tx.Outcome);
    }

    [Fact]
    public void A_change_that_is_not_a_whole_number_of_voltage_steps_is_refused_before_the_write()
    {
        var (rails, _) = Setup();
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-3_000));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("not a whole number of the rail's 5 mV steps", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void VMIN_cannot_be_raised_past_REL()
    {
        var (rails, _) = Setup();
        // MSVDD: REL 1.045 V, VMIN 0.825 V; VMIN +225 mV = 1.050 V > REL.
        var r = rails.Apply(_gpu, Rail.Msvdd, new RailRules.Request(225_000, null, null, null));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("above the rail's resulting MAX", r.Tx.Detail);
    }

    [Fact]
    public void A_driver_whose_MAX_lands_above_the_ceiling_gets_the_rail_back()
    {
        var (rails, _) = Setup();
        _maxBias = 40_000;   // MAX = min(...) + 40 mV: 1.135 V at stock, 1.155 V after +20 mV REL
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(20_000));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.False(r.Succeeded);
        Assert.Contains("nvvdd:", r.Violation);
        Assert.NotNull(r.RolledBack);
        Assert.Equal(TxOutcome.Applied, r.RolledBack!.Tx.Outcome);
        Assert.Equal(0, ControlField(Rail.Nvvdd, RailSlot.Rel));
        Assert.StartsWith("released", r.Ownership);
        Assert.Equal(new RailOffsets(0, 0, 0, 0), r.AfterOffsets);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.Empty(r.Surprises);   // REL itself moved as predicted; only the derived MAX was off
    }

    [Fact]
    public void Offsets_that_moved_between_the_status_read_and_the_write_snapshot_abort_the_plan()
    {
        var (rails, _) = Setup();
        int controlReads = 0;
        FakeDriver.QueryResponder = q =>
        {
            uint word = BitConverter.ToUInt32(q);
            if (word == StatusWord) return Status(FakeDriver.State[ControlWord]);
            // A concurrent tool lowers MSVDD between the reader's control read and the transaction's.
            if (word == ControlWord && ++controlReads == 2) W(FakeDriver.State[ControlWord], Field(Rail.Msvdd, RailSlot.Rel), -60_000);
            return null;
        };
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("msvdd offsets changed between the status read and the write snapshot", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_side_effect_on_the_other_rail_is_reported_because_the_restore_only_puts_back_what_this_write_changed()
    {
        var (rails, _) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.TouchNeighbour;
        FakeDriver.Field = Field(Rail.Msvdd, RailSlot.Rel);
        FakeDriver.Neighbour = -RailsLayout.ControlEntry;   // lands on NVVDD's REL
        FakeDriver.MisbehaveOnce = true;
        var r = rails.Apply(_gpu, Rail.Msvdd, Rel(-55_000));
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));
        Assert.StartsWith("released", r.Ownership);          // MSVDD itself is back and owes nothing
        Assert.Equal(1, ControlField(Rail.Nvvdd, RailSlot.Rel));   // the side effect stays: field-wise restore
        Assert.Contains("nvvdd rel=0,alt=0,ov=0,vmin=0 → rel=1,alt=0,ov=0,vmin=0", r.Violation);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Another_rail_s_limits_moving_after_a_clean_write_is_a_finding()
    {
        var (rails, _) = Setup();
        // A driver that couples MSVDD's REL base to NVVDD's REL offset.
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != StatusWord) return null;
            var s = Status(FakeDriver.State[ControlWord]);
            int sb = RailsLayout.StatusBase + RailsLayout.StatusEntry;
            W(s, sb + 4, (uint)(1_045_000 + ControlField(Rail.Nvvdd, RailSlot.Rel)));
            return s;
        };
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.True(r.Succeeded);
        Assert.Contains(r.Surprises, n => n.StartsWith("msvdd limits moved although it was not written"));
    }

    [Fact]
    public void The_legacy_boost_byte_is_not_this_path_s_to_touch()
    {
        var (rails, _) = Setup();
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.State[ControlWord][RailsLayout.ControlBoostByte]);
        var edits = Transaction.FirstDifference(r.Tx.Before, r.Tx.After!, []);
        Assert.Equal(Field(Rail.Nvvdd, RailSlot.Rel), edits);
    }

    [Fact]
    public void Give_back_writes_the_recorded_original_from_a_fresh_snapshot()
    {
        var (rails, hw) = Setup();
        Assert.Equal(TxOutcome.Applied, rails.Apply(_gpu, Rail.Msvdd, Rel(-60_000)).Tx.Outcome);
        var original = _journal.Load()!.Items.Single().Original;
        Assert.Equal("rel=-50000,alt=0,ov=0,vmin=0", original);

        var (cell, plan) = RailWrites.GiveBack(hw, _gpu, Rail.Msvdd, original);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));
    }

    [Fact]
    public void Prepare_takes_a_partial_value_for_a_write_and_the_full_original_for_recovery()
    {
        Assert.Equal(new RailRules.Request(null, -5_000, null, null), RailWrites.ParseRequest("rel=-5000"));
        Assert.Equal(new RailRules.Request(0, -50_000, 0, 0), RailWrites.ParseRequest("rel=-50000,alt=0,ov=0,vmin=0"));
        Assert.Throws<FormatException>(() => RailWrites.ParseRequest("max=5000"));
        Assert.Throws<FormatException>(() => RailWrites.ParseRequest(""));

        var (rails, hw) = Setup();
        Assert.Throws<ArgumentException>(() => RailWrites.Prepare(hw, _gpu, "fbvdd", "rel=0"));
        var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", "rel=-5000");
        Assert.Equal("rails/nvvdd", cell.Id);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        var (cell2, plan2) = RailWrites.Prepare(hw, _gpu, "nvvdd", _journal.Load()!.Items.Single().Original);
        Assert.Equal(Hold.Released, new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell2, plan2).Hold);
    }

    [Fact]
    public void A_rail_left_above_the_ceiling_by_another_tool_can_still_be_moved_back_towards_it()
    {
        var (rails, _) = Setup();
        // Another tool's XOC left NVVDD at REL +100 / ALT +100: MAX = min(1.195, 1.215, 1.2) = 1.195 V > 1.15 V.
        W(FakeDriver.State[ControlWord], Field(Rail.Nvvdd, RailSlot.Rel), 100_000);
        W(FakeDriver.State[ControlWord], Field(Rail.Nvvdd, RailSlot.Alt), 100_000);

        var lower = rails.Apply(_gpu, Rail.Nvvdd, Rel(75_000));   // MAX 1.17 V: still above, but lower
        Assert.Equal(TxOutcome.Applied, lower.Tx.Outcome);
        Assert.Null(lower.Violation);
        Assert.Null(lower.RolledBack);
        Assert.Equal(75_000, ControlField(Rail.Nvvdd, RailSlot.Rel));

        var msvdd = rails.Apply(_gpu, Rail.Msvdd, Rel(-55_000));  // the other rail's old excess does not trap it
        Assert.True(msvdd.Succeeded, msvdd.Violation ?? msvdd.Tx.ToString());

        // From above the ceiling a raise may go only as far as the state the hardware held before we owned the cell.
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(80_000)).Succeeded);
        Assert.Equal(TxOutcome.Aborted, rails.Apply(_gpu, Rail.Nvvdd, Rel(105_000)).Tx.Outcome);
    }

    [Fact]
    public void ALT_cannot_be_lowered_under_VMIN_because_ALT_is_part_of_MAX()
    {
        var (rails, _) = Setup();
        // NVVDD ALT 1.115 V − 320 mV = 0.795 V < VMIN 0.8 V.
        var r = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, -320_000, null));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("above the rail's resulting MAX of 795 mV", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        // −315 mV is fine for NVVDD (0.8 V) but would put a tracking MSVDD's ALT (and MAX) at 0.8 V, under its 0.825 V VMIN.
        var coupled = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, -315_000, null));
        Assert.Equal(TxOutcome.Aborted, coupled.Tx.Outcome);
        Assert.Contains("under its VMIN of 825 mV", coupled.Tx.Detail);
        Assert.Equal(TxOutcome.Applied, rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, -290_000, null)).Tx.Outcome);
    }

    [Fact]
    public void Range_bounds_are_exactly_what_the_plan_accepts()
    {
        Setup();
        var snap = RailsReader.Read(new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.VoltVoltRailsSetControl])), _gpu);
        foreach (var rail in snap.Rails)
            foreach (var slot in new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin })
            {
                var range = RailWriteRules.Range(rail, slot);
                Assert.NotNull(range);
                var (lo, hi) = range!.Value;
                RailRules.Request Req(int v) => slot switch
                {
                    RailSlot.Rel => new(null, v, null, null), RailSlot.Alt => new(null, null, v, null),
                    RailSlot.Ov => new(null, null, null, v), _ => new(v, null, null, null),
                };
                bool Ok(int v) => RailWriteRules.Check(rail, Req(v)) == null && RailRules.Plan(rail, Req(v), xoc: false) is Mutation.Apply;
                Assert.True(Ok(lo), $"{rail.Rail} {slot} low {lo}");
                Assert.True(Ok(hi), $"{rail.Rail} {slot} high {hi}");
                Assert.False(Ok(lo - 5_000), $"{rail.Rail} {slot} below {lo}");
                Assert.False(Ok(hi + 5_000), $"{rail.Rail} {slot} above {hi}");
            }
        // 5090 numbers: NVVDD REL up to +55 mV (1.15 V); OV cannot rise; VMIN at most up to MAX 1.095 V.
        var nv = snap.Get(Rail.Nvvdd)!;
        Assert.Equal(55_000, RailWriteRules.Range(nv, RailSlot.Rel)!.Value.MaxUv);
        Assert.Equal(0, RailWriteRules.Range(nv, RailSlot.Ov)!.Value.MaxUv);
        Assert.Equal(295_000, RailWriteRules.Range(nv, RailSlot.Vmin)!.Value.MaxUv);
    }

    [Fact]
    public void The_snapshot_Range_is_exactly_what_the_plan_accepts_coupling_and_origin_included()
    {
        var (rails, hw) = Setup();
        RailRules.Request Req(RailSlot slot, int v) => slot switch
        {
            RailSlot.Rel => new(null, v, null, null), RailSlot.Alt => new(null, null, v, null),
            RailSlot.Ov => new(null, null, null, v), _ => new(v, null, null, null),
        };
        void Exact(RailsSnapshot snap, RailOffsets? origin)
        {
            foreach (var rail in snap.Rails)
                foreach (var slot in new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin })
                {
                    var (lo, hi) = RailWriteRules.Range(snap, rail.Rail, slot, rail.Rail == Rail.Nvvdd ? origin : null)!.Value;
                    bool Ok(int v) => RailWrites.Decide(snap, rail.Rail, Req(slot, v), rail.Rail == Rail.Nvvdd ? origin : null) is Mutation.Apply;
                    Assert.True(Ok(lo) && Ok(hi), $"{rail.Rail} {slot} [{lo}, {hi}]");
                    Assert.False(Ok(lo - 5_000), $"{rail.Rail} {slot} below {lo}");
                    Assert.False(Ok(hi + 5_000), $"{rail.Rail} {slot} above {hi}");
                }
        }
        var stock = RailsReader.Read(hw, _gpu);
        Exact(stock, null);
        // The stock 5090 has the tracking signature: MSVDD's VMIN 0.825 V bounds NVVDD from below (UI thread's numbers).
        Assert.Equal(-220_000, RailWriteRules.Range(stock, Rail.Nvvdd, RailSlot.Rel)!.Value.MinUv);
        Assert.Equal(-290_000, RailWriteRules.Range(stock, Rail.Nvvdd, RailSlot.Alt)!.Value.MinUv);
        Assert.Equal(-375_000, RailWriteRules.Range(stock, Rail.Nvvdd, RailSlot.Ov)!.Value.MinUv);
        Assert.Equal(-355_000, RailWriteRules.Range(stock, Rail.Nvvdd, RailSlot.Vmin)!.Value.MinUv);
        Assert.Equal(55_000, RailWriteRules.Range(stock, Rail.Nvvdd, RailSlot.Rel)!.Value.MaxUv);

        // After OV −5 the slider reaches back to stock 1.2 V (the origin), and no further.
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Ov(-5_000)).Succeeded);
        var after = RailsReader.Read(hw, _gpu);
        var origin = RailWrites.Decode(_journal.Load()!.Items.Single().Original);
        Exact(after, origin);
        Assert.Equal(0, RailWriteRules.Range(after, Rail.Nvvdd, RailSlot.Ov, origin)!.Value.MaxUv);
        Assert.Equal(-5_000, RailWriteRules.Range(after, Rail.Nvvdd, RailSlot.Ov)!.Value.MaxUv);   // without the origin: stuck
    }

    private static RailRules.Request Ov(int uv) => new(null, null, null, uv);

    [Fact]
    public void A_lowered_OV_can_be_put_back_although_stock_OV_sits_above_the_ceiling()
    {
        var (rails, _) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Ov(-5_000)).Succeeded);
        var back = rails.Apply(_gpu, Rail.Nvvdd, Ov(0));   // 1.2 V again: the journal's original, a return
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Null(back.Violation);
        Assert.StartsWith("released", back.Ownership);
        Assert.Equal(0, ControlField(Rail.Nvvdd, RailSlot.Ov));
    }

    [Fact]
    public void Recovery_gives_a_lowered_OV_back()
    {
        var (rails, hw) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Ov(-5_000)).Succeeded);
        var (cell, plan) = RailWrites.GiveBack(hw, _gpu, Rail.Nvvdd, _journal.Load()!.Items.Single().Original);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.Empty(_journal.Load()?.Items ?? []);
    }

    [Fact]
    public void The_rollback_after_a_violating_OV_write_reaches_the_state_before_it()
    {
        var (rails, _) = Setup();
        _maxBias = unchecked((uint)-100_000);   // MAX derived 100 mV under min(REL, ALT, OV): OV -305 lands with VMIN > MAX
        var r = rails.Apply(_gpu, Rail.Nvvdd, Ov(-305_000));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Contains("VMIN 800 mV is above MAX", r.Violation);
        Assert.Equal(TxOutcome.Applied, r.RolledBack!.Tx.Outcome);
        Assert.Equal(0, ControlField(Rail.Nvvdd, RailSlot.Ov));
        Assert.StartsWith("released", r.Ownership);
    }

    [Fact]
    public void A_foreign_original_above_the_ceiling_can_be_restored_but_never_exceeded()
    {
        var (rails, hw) = Setup();
        W(FakeDriver.State[ControlWord], Field(Rail.Nvvdd, RailSlot.Rel), 100_000);   // another tool's XOC: REL 1.195 V
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(75_000)).Succeeded);
        Assert.Equal(TxOutcome.Aborted, rails.Apply(_gpu, Rail.Nvvdd, Rel(105_000)).Tx.Outcome);   // past the original: refused

        var back = rails.Apply(_gpu, Rail.Nvvdd, Rel(100_000));   // exactly the original: a return
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Null(back.Violation);
        Assert.StartsWith("released", back.Ownership);

        // Once released there is no original any more: the same value is now an ordinary request, and a raise.
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(75_000)).Succeeded);
        _journal.Clear();
        Assert.Equal(TxOutcome.Aborted, rails.Apply(_gpu, Rail.Nvvdd, Rel(100_000)).Tx.Outcome);
    }

    [Fact]
    public void Only_a_give_back_skips_the_direction_rules_a_complete_user_value_does_not()
    {
        var (_, hw) = Setup();
        var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", "rel=100000,alt=0,ov=0,vmin=0");
        Assert.Equal(TxOutcome.Aborted, new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan).Tx.Outcome);
        Assert.Throws<FormatException>(() => RailWrites.Prepare(hw, _gpu, "nvvdd", "ov=0", original: "ov=0"));   // an original is complete
    }

    [Fact]
    public void An_NVVDD_raise_that_would_lift_a_tracking_MSVDD_over_the_ceiling_is_refused_before_the_write()
    {
        var (rails, _) = Setup();
        _coupled = true;
        // The fixture has the 5090's signature: MSVDD bases (1.095/1.115/1.2) equal NVVDD's evaluated limits.
        // MSVDD REL −50 → +50 (1.145 V) and ALT +35 (1.15 V): MSVDD MAX 1.145 V, inside the ceiling.
        Assert.True(rails.Apply(_gpu, Rail.Msvdd, new RailRules.Request(null, 50_000, 35_000, null)).Succeeded);
        // NVVDD REL +55 (1.15 V) would shift MSVDD REL to 1.2 V and its MAX to 1.15 V: still fine.
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(55_000)).Succeeded);
        // NVVDD ALT +35: in its own plan fine (1.15 V), but a tracking MSVDD's MAX would go to 1.185 V.
        var r = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, null, 35_000, null));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("MSVDD tracks NVVDD", r.Tx.Detail);
    }

    [Fact]
    public void Without_the_tracking_signature_NVVDD_is_judged_on_its_own()
    {
        var (rails, _) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000)).Succeeded);   // NVVDD REL eval now 1.090 ≠ MSVDD REL base 1.095
        Assert.True(rails.Apply(_gpu, Rail.Msvdd, new RailRules.Request(null, 50_000, 35_000, null)).Succeeded);
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, 55_000, 35_000, null)).Succeeded);
    }

    [Fact]
    public void PrepareVerified_judges_relative_to_its_own_snapshot()
    {
        var (_, hw) = Setup();
        _maxBias = 100_000;   // MAX 1.195 V at stock: above the ceiling before anything is written
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        Assert.NotNull(RailWriteRules.CheckAfterWrite(RailsReader.Read(hw, _gpu)));   // the absolute form traps it
        var (cell, plan, verify) = RailWrites.PrepareVerified(hw, _gpu, "nvvdd", "rel=-5000");
        Assert.Equal(TxOutcome.Applied, writer.Apply(_gpu, cell, plan).Tx.Outcome);
        Assert.Null(verify(hw, _gpu));   // MAX went down: not worse
        (cell, plan, verify) = RailWrites.PrepareVerified(hw, _gpu, "nvvdd", "rel=5000");
        Assert.Equal(TxOutcome.Applied, writer.Apply(_gpu, cell, plan).Tx.Outcome);
        Assert.Contains("above the ceiling", verify(hw, _gpu));   // MAX went up while outside
    }

    [Fact]
    public void A_lowered_OV_goes_back_slot_by_slot_even_while_other_slots_stay_changed()
    {
        var (rails, _) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, -5_000, null, -5_000)).Succeeded);
        // OV alone back to stock 1.2 V (above the ceiling) while REL stays at −5: the target is not the whole
        // original, but OV only goes as far as its origin.
        var r = rails.Apply(_gpu, Rail.Nvvdd, Ov(0));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Null(r.Violation);
        Assert.Equal(0, ControlField(Rail.Nvvdd, RailSlot.Ov));
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        Assert.Equal(TxOutcome.Aborted, rails.Apply(_gpu, Rail.Nvvdd, Ov(5_000)).Tx.Outcome);   // past the origin: never
    }

    [Fact]
    public void Every_write_can_be_undone_through_Prepare_with_the_journal_original()
    {
        var (rails, hw) = Setup();
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        foreach (var value in new[] { "ov=-5000", "rel=-5000,ov=-305000", "alt=-290000", "vmin=-5000", "rel=50000" })
        {
            var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", value);
            Assert.Equal(TxOutcome.Applied, writer.Apply(_gpu, cell, plan).Tx.Outcome);
            var original = _journal.Load()!.Items.Single().Original;
            (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", original, original);
            var back = writer.Apply(_gpu, cell, plan);
            Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
            Assert.Empty(_journal.Load()?.Items ?? []);
        }
    }

    [Fact]
    public void The_whitelist_accepts_the_read_modify_write_buffer_and_refuses_a_foreign_mask()
    {
        Setup();
        var buf = (byte[])FakeDriver.State[ControlWord].Clone();
        Assert.Null(WritePolicySet.Default.Check(Slot.VoltVoltRailsSetControl, buf));
        W(buf, 4, 2u);
        Assert.NotNull(WritePolicySet.Default.Check(Slot.VoltVoltRailsSetControl, buf));
    }

    [Fact]
    public void The_top_names_the_binding_slot_the_next_wall_and_how_far_the_rules_let_it_move()
    {
        var (_, hw) = Setup();
        var stock = RailsReader.Read(hw, _gpu);
        var nv = RailTop.Of(stock, Rail.Nvvdd)!;
        Assert.Equal(1_095_000u, nv.MaxUv);
        Assert.Equal([RailSlot.Rel], nv.Binding);
        Assert.Equal((RailSlot?)RailSlot.Alt, nv.Next);
        Assert.Equal(1_115_000u, nv.NextUv);
        Assert.Equal(1_150_000u, nv.ReachUpUv);     // REL and ALT both stop at the ceiling; OV stays at 1.2 V
        Assert.Equal(825_000u, nv.ReachDownUv);     // ALT/OV go down to MSVDD's VMIN (the tracking guard)
        Assert.Equal(5_000u, nv.StepUv);

        var ms = RailTop.Of(stock, Rail.Msvdd)!;
        Assert.Equal(1_045_000u, ms.MaxUv);          // stock REL −50 mV is what binds MSVDD
        Assert.Equal([RailSlot.Rel], ms.Binding);
        Assert.Equal(1_150_000u, ms.ReachUpUv);
    }

    [Fact]
    public void A_tie_binds_both_slots_and_the_next_wall_moves_up()
    {
        var (rails, hw) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(20_000)).Succeeded);
        var origin = RailWrites.Decode(_journal.Load()!.Items.Single().Original);
        var top = RailTop.Of(RailsReader.Read(hw, _gpu), Rail.Nvvdd, origin)!;
        Assert.Equal(1_115_000u, top.MaxUv);
        Assert.Equal([RailSlot.Rel, RailSlot.Alt], top.Binding);
        Assert.Equal((RailSlot?)RailSlot.Ov, top.Next);
        Assert.Equal(1_200_000u, top.NextUv);
    }

    [Fact]
    public void A_max_under_all_three_binds_no_slot_and_cannot_be_lifted()
    {
        var (_, hw) = Setup();
        _maxBias = unchecked((uint)-10_000);
        var top = RailTop.Of(RailsReader.Read(hw, _gpu), Rail.Nvvdd)!;
        Assert.Equal(1_085_000u, top.MaxUv);
        Assert.Empty(top.Binding);
        Assert.Equal((RailSlot?)RailSlot.Rel, top.Next);
        Assert.Equal(top.MaxUv, top.ReachUpUv);
    }

    [Theory]
    [InlineData(1_095_000u, 1_090_000u, true)]    // drill 20: light load, stock
    [InlineData(1_070_000u, 1_065_000u, true)]    // light load under REL −25
    [InlineData(1_070_000u, 1_015_000u, false)]   // power-capped burn: the wall holds it, not the top
    [InlineData(1_095_000u, 1_020_000u, false)]
    public void The_top_holds_a_load_only_within_two_steps_of_it(uint max, uint operating, bool holds)
    {
        var top = new RailTop(Rail.Nvvdd, max, [RailSlot.Rel], RailSlot.Alt, 1_115_000, 1_150_000, 825_000, 5_000);
        Assert.Equal(holds, top.Holds(operating));
    }
}
