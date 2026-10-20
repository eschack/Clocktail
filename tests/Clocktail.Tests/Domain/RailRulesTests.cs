using Clocktail.Domain;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class RailRulesTests
{
    private static RailRecord Nvvdd(bool devices = true) => RailsFixture.Snapshot(devices).Get(Rail.Nvvdd)!;
    private static RailRecord Msvdd() => RailsFixture.Snapshot().Get(Rail.Msvdd)!;

    private static Mutation.Apply Ok(Mutation m)
    {
        if (m is Mutation.Abort a) Assert.Fail(a.Reason);
        return Assert.IsType<Mutation.Apply>(m);
    }
    private static string Refused(Mutation m) => Assert.IsType<Mutation.Abort>(m).Reason;

    [Fact]
    public void Ceilings_follow_the_domain_table_and_the_mode()
    {
        Assert.Equal(1_150_000u, RailRules.Ceiling(Nvvdd(), xoc: false));   // min(1.2 V, 1.15 V)
        Assert.Equal(1_200_000u, RailRules.Ceiling(Nvvdd(), xoc: true));    // domain max
        Assert.Equal(1_150_000u, RailRules.Ceiling(Nvvdd(false), xoc: false));
        Assert.Equal(1_250_000u, RailRules.Ceiling(Nvvdd(false), xoc: true));
        Assert.Equal(RailsFixture.DomainMin, RailRules.Floor(Nvvdd()));
        Assert.Equal(0u, RailRules.Floor(Nvvdd(false)));
    }

    [Fact]
    public void Unchanged_request_yields_no_edits()
    {
        var m = Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, null), xoc: false));
        Assert.Empty(m.Edits);
        m = Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, RailsFixture.MsvddRelOffset, null, null), xoc: false));
        Assert.Empty(m.Edits);
    }

    [Fact]
    public void A_raise_within_the_ceiling_becomes_edits_at_the_right_offsets()
    {
        var rail = Nvvdd();
        var m = Ok(RailRules.Plan(rail, new RailRules.Request(null, 50_000, 50_000, null), xoc: false));   // 1.07 → 1.12 V
        Assert.Equal(2, m.Edits.Count);
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Rel) && BitConverter.ToInt32(e.Value) == 50_000);
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Alt) && BitConverter.ToInt32(e.Value) == 50_000);
    }

    [Fact]
    public void A_raise_past_the_ceiling_is_refused_unless_xoc()
    {
        var req = new RailRules.Request(null, 100_000, 100_000, null);   // 1.17 V
        Assert.Contains("1150 mV", Refused(RailRules.Plan(Nvvdd(), req, xoc: false)));
        Ok(RailRules.Plan(Nvvdd(), req, xoc: true));                      // ceiling 1.2 V
        Assert.Contains("1200 mV", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, 140_000, 140_000, null), xoc: true)));
    }

    [Fact]
    public void Ov_is_never_checked_against_the_ceiling_only_the_floor()
    {
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, 200_000), xoc: false));         // 1.3 V eval, allowed
        Assert.Contains("below", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, -600_000), xoc: false)));
    }

    [Fact]
    public void A_decrease_is_not_ceiling_checked_but_is_floor_checked()
    {
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, -100_000, -100_000, null), xoc: false));
        Assert.Contains("below the reported device minimum", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, -500_000, null, null), xoc: false)));
    }

    [Fact]
    public void Vmin_must_stay_under_rel_ov_and_ceiling()
    {
        // VMIN eval 0.7 V + 0.4 V = 1.1 V > REL 1.07 V
        Assert.Contains("minimum-voltage", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(400_000, null, null, null), xoc: false)));
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(300_000, null, null, null), xoc: false));   // 1.0 V
    }

    [Fact]
    public void Msvdd_without_a_reported_alt_cannot_change_alt_or_go_positive_on_rel()
    {
        Assert.Contains("ALT/OP", Refused(RailRules.Plan(Msvdd(), new RailRules.Request(null, null, 10_000, null), xoc: false)));
        Assert.Contains("positive REL", Refused(RailRules.Plan(Msvdd(), new RailRules.Request(null, 10_000, null, null), xoc: false)));
        Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, 10_000, null, null), xoc: false, relOnlyOk: true));
        Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, -25_000, null, null), xoc: false));   // the usual 5090 move
    }

    [Fact]
    public void Unwritable_rails_abort_with_their_reason()
    {
        var rail = RailsFixture.Snapshot(setAvailable: false).Get(Rail.Nvvdd)!;
        Assert.Contains("not writable", Refused(RailRules.Plan(rail, new RailRules.Request(null, 0, null, null), xoc: false)));
    }

    [Fact]
    public void Post_write_check_catches_max_over_ceiling()
    {
        var rail = Nvvdd();
        Assert.Null(RailRules.CheckAfterWrite(rail, xoc: false));
        var high = rail with { Limits = rail.Limits with { MaxUv = 1_160_000 } };
        Assert.Contains("exceed", RailRules.CheckAfterWrite(high, xoc: false));
        Assert.Null(RailRules.CheckAfterWrite(high, xoc: true));
    }

    [Fact]
    public void Rules_and_transaction_compose_end_to_end_on_the_fake_driver()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo], [Slot.VoltVoltRailsSetControl]);
        var hw = new NvClient(api);
        var gpu = new GpuSession(1, "fake");
        var snap = RailsReader.Read(hw, gpu);
        var r = new Transaction(hw).Run(gpu, new TransactionSpec(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl),
            _ => RailRules.Plan(snap.Get(Rail.Msvdd)!, new RailRules.Request(null, -25_000, null, null), xoc: false));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(-25_000, RailsReader.Read(hw, gpu).Get(Rail.Msvdd)!.Offsets.RelUv);
    }
}
