using Clocktail.Domain;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>Runs the write template against the fake driver using the rails control structure (0xac8).</summary>
[Collection("fake-driver")]
public class TransactionTests
{
    private const int Rel0 = 0x48 + 4;        // rail 0 REL offset field
    private const int Rel1 = 0x48 + 0x54 + 4;  // rail 1 REL offset field
    private static readonly TransactionSpec Rails = new(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl);

    private static (Transaction tx, GpuSession gpu) Setup()
    {
        FakeDriver.Reset();
        FakeDriver.Seed(Slot.VoltVoltRailsGetControl, b =>
        {
            BitConverter.GetBytes(3u).CopyTo(b, 4);              // rail mask NVVDD | MSVDD
            BitConverter.GetBytes(-50_000).CopyTo(b, Rel1);      // MSVDD REL baseline -50 mV, as seen on the 5090
        });
        var api = FakeDriver.Api(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl);
        return (new Transaction(new NvClient(api)), new GpuSession(1, "fake"));
    }

    [Fact]
    public void Happy_path_reads_edits_writes_and_verifies()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(RestoreState.NotNeeded, r.Restore);
        Assert.Equal(25_000, BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel0));
        Assert.Equal(-50_000, BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel1));   // untouched rail kept
        Assert.Equal(2, FakeDriver.Gets);
        Assert.Equal(1, FakeDriver.Sets);
    }

    [Fact]
    public void Equal_value_write_is_a_no_op()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel1, -50_000)));
        Assert.Equal(TxOutcome.NoChange, r.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Domain_abort_writes_nothing()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, snap => new Mutation.Abort("ceiling exceeded"));
        Assert.Equal(TxOutcome.Aborted, r.Outcome);
        Assert.Equal("ceiling exceeded", r.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Mutate_sees_the_fresh_snapshot()
    {
        var (tx, gpu) = Setup();
        int seen = 0;
        tx.Run(gpu, Rails, snap => { seen = BitConverter.ToInt32(snap.Span[Rel1..]); return new Mutation.Abort("just looking"); });
        Assert.Equal(-50_000, seen);
    }

    [Fact]
    public void Write_ahead_runs_once_after_the_decision_and_before_the_write()
    {
        var (tx, gpu) = Setup();
        int calls = 0, setsAtHook = -1, seenRel1 = 0;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)), writeAhead: snap =>
        {
            calls++;
            setsAtHook = FakeDriver.Sets;
            seenRel1 = BitConverter.ToInt32(snap, Rel1);
            return "recorded";
        });
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0, setsAtHook);                 // before the first byte reached the driver
        Assert.Equal(-50_000, seenRel1);             // with the snapshot the decision was made on
        Assert.Equal(["read", "recorded", "write"], r.Log.Take(3).Select(l => l.Split(' ')[0]));
    }

    [Fact]
    public void Write_ahead_does_not_run_when_nothing_is_written()
    {
        var (tx, gpu) = Setup();
        int calls = 0;
        Func<byte[], string?> hook = _ => { calls++; return null; };
        Assert.Equal(TxOutcome.NoChange, tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel1, -50_000)), hook).Outcome);
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, Rails, _ => new Mutation.Abort("no"), hook).Outcome);
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, Rails, _ => Mutation.Of(Edit.UInt32(0, 1)), hook).Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_failing_write_ahead_aborts_before_the_driver()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)), _ => throw new IOException("disk full"));
        Assert.Equal(TxOutcome.Aborted, r.Outcome);
        Assert.Contains("could not record ownership", r.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Domain_code_that_throws_is_an_abort_not_an_exception()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, _ => throw new InvalidDataException("unexpected layout"));
        Assert.Equal(TxOutcome.Aborted, r.Outcome);
        Assert.Contains("unexpected layout", r.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_failed_read_leaves_no_snapshot()
    {
        var (tx, gpu) = Setup();
        FakeDriver.FailNextGets = 1;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Aborted, r.Outcome);
        Assert.Empty(r.Before);
    }

    [Fact]
    public void Refused_write_with_untouched_state_needs_no_restore()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.RefuseUntouched;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.NotNeeded, r.Restore);
        Assert.Equal(1, FakeDriver.Sets);
        Assert.Equal(0, BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel0));
    }

    [Fact]
    public void Error_status_but_matching_readback_counts_as_applied_with_a_note()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.AcceptReportError;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Contains("driver reported", r.Detail);
    }

    [Fact]
    public void Clamped_value_is_detected_and_the_field_is_restored()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.ClampField;
        FakeDriver.Field = Rel1;
        FakeDriver.MisbehaveOnce = true;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel1, -60_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.Restored, r.Restore);
        Assert.Contains($"+0x{Rel1:x}", r.Detail);
        Assert.Equal(-50_000, BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel1));
        Assert.Equal(2, FakeDriver.Sets);
    }

    [Fact]
    public void A_change_to_another_rail_is_caught_even_though_the_requested_field_landed()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.TouchNeighbour;   // flips a byte in rail 1 while applying rail 0
        FakeDriver.Field = Rel0;
        FakeDriver.MisbehaveOnce = true;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Contains($"+0x{Rel1:x}", r.Detail);
        // Restore puts rail 0 back on a fresh copy. Rail 1's stray flip is outside this transaction's edits, so it
        // is reported in the detail, not silently undone: the transaction only ever writes back what it changed.
        Assert.Equal(RestoreState.Restored, r.Restore);
        Assert.Equal(0, BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel0));
        Assert.Equal(1, FakeDriver.Current(Slot.VoltVoltRailsGetControl)[Rel1] & 1);
    }

    [Fact]
    public void Restore_is_verified_and_its_failure_is_reported()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.ClampField;   // every write clamps, including the restore
        FakeDriver.Field = Rel1;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel1, -60_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.RestoreFailed, r.Restore);
        Assert.Contains("could not be verified", r.Detail);
    }

    [Fact]
    public void Readback_failure_after_a_write_is_reported_as_unknown_state()
    {
        var (tx, gpu) = Setup();
        FakeDriver.FailNextGets = 0;
        var r = tx.Run(gpu, Rails, _ => { FakeDriver.FailNextGets = 1; return Mutation.Of(Edit.Int32(Rel0, 25_000)); });
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.NotAttempted, r.Restore);
        Assert.Contains("unknown", r.Detail);
    }

    [Fact]
    public void Handle_loss_during_the_write_poisons_and_stops()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.LoseHandle;
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.NotAttempted, r.Restore);
        Assert.True(gpu.Faulted);
        Assert.Equal(1, FakeDriver.Gets);   // the readback was short-circuited by the poisoned session
    }

    [Fact]
    public void Policy_rejection_aborts_before_the_driver()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails, _ => Mutation.Of(Edit.UInt32(4, 2)));   // MSVDD-only mask is not writable
        Assert.Equal(TxOutcome.Aborted, r.Outcome);
        Assert.Contains("refused before", r.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Edits_are_validated()
    {
        var (tx, gpu) = Setup();
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, Rails, _ => Mutation.Of(Edit.UInt32(0, 1))).Outcome);          // version word
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, Rails, _ => Mutation.Of(Edit.UInt32(0xac8 - 2, 1))).Outcome);  // past the end
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, Rails, _ => Mutation.Of(Edit.UInt32(8, 1), Edit.Byte(9, 1))).Outcome); // overlap
        Assert.Equal(TxOutcome.NoChange, tx.Run(gpu, Rails, _ => new Mutation.Apply([])).Outcome);   // nothing to change is a result, not an error
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Volatile_ranges_are_ignored_in_comparison()
    {
        var (tx, gpu) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.TouchNeighbour;
        FakeDriver.Field = Rel0;
        var spec = Rails with { Volatile = [new ByteRange(Rel1, 4)] };
        var r = tx.Run(gpu, spec, _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
    }

    [Fact]
    public void Busy_gpu_aborts_without_reading()
    {
        var (tx, gpu) = Setup();
        Assert.True(gpu.TryEnter(TimeSpan.Zero, out var held));
        TxResult? r = null;
        var t = new Thread(() => r = tx.Run(gpu, Rails with { LockTimeout = TimeSpan.FromMilliseconds(20) }, _ => Mutation.Of(Edit.Int32(Rel0, 1))));
        t.Start(); t.Join();
        held.Dispose();
        Assert.Equal(TxOutcome.Aborted, r!.Outcome);
        Assert.Contains("another operation", r.Detail);
        Assert.Equal(0, FakeDriver.Gets);
    }
}

[CollectionDefinition("fake-driver", DisableParallelization = true)]
public class FakeDriverCollection { }
