using Clocktail.Domain;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary><see cref="TransactionSpec.CheckAfterWrite"/>: a veto after a matching readback is handled like a mismatch.</summary>
[Collection("fake-driver")]
public class TransactionCheckAfterWriteTests
{
    private const int Rel0 = 0x48 + 4;        // rail 0 REL offset field

    private static (Transaction tx, GpuSession gpu) Setup()
    {
        FakeDriver.Reset();
        FakeDriver.Seed(Slot.VoltVoltRailsGetControl, b => BitConverter.GetBytes(3u).CopyTo(b, 4));
        var api = FakeDriver.Api(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl);
        return (new Transaction(new NvClient(api)), new GpuSession(1, "fake"));
    }

    private static TransactionSpec Rails(Func<NvClient, GpuSession, string?> check) =>
        new(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl, CheckAfterWrite: check);

    private static int Rel0Now => BitConverter.ToInt32(FakeDriver.Current(Slot.VoltVoltRailsGetControl), Rel0);

    [Fact]
    public void A_passing_check_sees_the_written_state_and_the_write_stands()
    {
        var (tx, gpu) = Setup();
        int seen = 0;
        var r = tx.Run(gpu, Rails((_, _) => { seen = Rel0Now; return null; }), _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(25_000, seen);
        Assert.Null(r.Veto);
        Assert.Equal(25_000, Rel0Now);
    }

    [Fact]
    public void A_veto_restores_the_edited_fields_and_says_why()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails((_, _) => "MAX 1.200 V is above the ceiling"), _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.Restored, r.Restore);
        Assert.StartsWith("MAX 1.200 V is above the ceiling; previous values restored", r.Detail);
        Assert.Contains("post-write check: MAX 1.200 V is above the ceiling", r.Log);
        Assert.Equal("MAX 1.200 V is above the ceiling", r.Veto);   // the reason on its own, for a program
        Assert.Equal(0, Rel0Now);
        Assert.Equal(2, FakeDriver.Sets);
    }

    [Fact]
    public void A_check_that_cannot_run_is_a_veto()
    {
        var (tx, gpu) = Setup();
        var r = tx.Run(gpu, Rails((_, _) => throw new InvalidDataException("status unreadable")), _ => Mutation.Of(Edit.Int32(Rel0, 25_000)));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.Restored, r.Restore);
        Assert.Contains("could not run (status unreadable)", r.Detail);
        Assert.Equal(0, Rel0Now);
    }

    [Fact]
    public void Nothing_written_nothing_checked()
    {
        var (tx, gpu) = Setup();
        int calls = 0;
        var spec = Rails((_, _) => { calls++; return "never"; });
        Assert.Equal(TxOutcome.NoChange, tx.Run(gpu, spec, _ => Mutation.Of(Edit.Int32(Rel0, 0))).Outcome);
        Assert.Equal(TxOutcome.Aborted, tx.Run(gpu, spec, _ => new Mutation.Abort("no")).Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_request_without_a_restore_request_reports_the_veto_and_an_unknown_state()
    {
        var (tx, gpu) = Setup();
        var payload = (byte[])FakeDriver.Current(Slot.VoltVoltRailsGetControl).Clone();
        BitConverter.GetBytes(25_000).CopyTo(payload, Rel0);
        var r = tx.Run(gpu, Rails((_, _) => "vetoed"), _ => new Mutation.Request(payload, [Edit.Int32(Rel0, 25_000)], null));
        Assert.Equal(TxOutcome.Failed, r.Outcome);
        Assert.Equal(RestoreState.NotAttempted, r.Restore);
        Assert.StartsWith("vetoed; this interface offers no way", r.Detail);
    }
}
