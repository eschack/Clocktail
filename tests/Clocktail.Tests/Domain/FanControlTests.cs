using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class FanControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public FanControlTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static int Level(int i) => FanLayout.ControlEntry(i) + FanLayout.ControlLevel;
    private static int Mode(int i) => FanLayout.ControlEntry(i) + FanLayout.ControlMode;

    /// <summary>Two channels (ids 1 and 2), firmware range [30, 100], both in auto. Mode carries a driver bit (0x10) that must survive.</summary>
    private static byte[] Status(uint min = 30, uint max = 100)
    {
        var b = NvClient.NewBuffer(Slot.ClientFanCoolersGetStatus);
        b[FanLayout.StatusCount] = 2;
        for (int i = 0; i < 2; i++)
        {
            int e = FanLayout.StatusEntry(i);
            W(b, e, (uint)i + 1); W(b, e + 4, 1200); W(b, e + 8, min); W(b, e + 0xc, max); W(b, e + 0x10, 36);
        }
        return b;
    }

    private static byte[] Control(uint mode0 = 0x10, uint level0 = 36)
    {
        var b = NvClient.NewBuffer(Slot.ClientFanCoolersGetControl);
        b[FanLayout.ControlCount] = 2;
        W(b, FanLayout.ControlEntry(0), 1); W(b, Level(0), level0); W(b, Mode(0), mode0);
        W(b, FanLayout.ControlEntry(1), 2); W(b, Level(1), 36); W(b, Mode(1), 0x10);
        return b;
    }

    private static uint Word(Slot s) => Slots.Info(s).Shape!.Value.Word;

    private (FanControl fan, NvClient hw) Setup(byte[]? status = null, byte[]? control = null)
    {
        FakeDriver.Reset();
        FakeDriver.State[Word(Slot.ClientFanCoolersGetStatus)] = status ?? Status();
        FakeDriver.State[Word(Slot.ClientFanCoolersGetControl)] = control ?? Control();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.ClientFanCoolersGetStatus, Slot.ClientFanCoolersGetControl], [Slot.ClientFanCoolersSetControl]));
        return (new FanControl(hw, _journal, _sentinel, _host), hw);
    }

    private static byte[] Stored => FakeDriver.Current(Slot.ClientFanCoolersGetControl);

    [Fact]
    public void Layout_sizes_are_the_arithmetic_of_header_plus_32_entries()
    {
        Assert.Equal(0x62c, FanLayout.InfoEntry(32));
        Assert.Equal(0x6a8, FanLayout.StatusEntry(32));
        Assert.Equal(0x5ac, FanLayout.ControlEntry(32));
        Assert.Equal(Slots.Info(Slot.ClientFanCoolersGetControl).Shape, Slots.Info(Slot.ClientFanCoolersSetControl).Shape);
    }

    [Fact]
    public void State_encoding_ignores_the_level_in_auto()
    {
        Assert.Equal("auto", FanState.FromControl(36, 0x10).ToString());
        Assert.Equal("manual:55", FanState.FromControl(55, 0x11).ToString());
        Assert.Equal(new FanState(true, 55), FanState.Parse("manual:55"));
        Assert.Equal(FanState.Auto, FanState.Parse("auto"));
        Assert.Throws<FormatException>(() => FanState.Parse("manual:-1"));
        Assert.Throws<FormatException>(() => FanState.Parse("50"));
    }

    [Fact]
    public void Parser_refuses_what_the_original_refuses()
    {
        var dup = Control(); W(dup, FanLayout.ControlEntry(1), 1);
        Assert.Throws<InvalidDataException>(() => FanParser.ParseControl(dup));
        var many = Control(); many[FanLayout.ControlCount] = 33;
        Assert.Throws<InvalidDataException>(() => FanParser.ParseControl(many));
        var inverted = Status(min: 60, max: 40);
        Assert.Throws<InvalidDataException>(() => FanParser.ParseStatus(inverted));
        var other = Control(); W(other, FanLayout.ControlEntry(1), 7);
        Assert.Throws<InvalidDataException>(() => FanParser.Join(FanParser.ParseStatus(Status()), FanParser.ParseControl(other)));
    }

    [Fact]
    public void Equal_state_is_no_change_and_claims_nothing()
    {
        var (fan, _) = Setup();
        var r = fan.Apply(_gpu, 1, FanState.Auto);
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.True(r.SentinelCleared);
    }

    [Theory]
    [InlineData(29u)]
    [InlineData(101u)]
    public void A_level_outside_the_firmware_range_never_reaches_the_driver(uint level)
    {
        var (fan, _) = Setup();
        var r = fan.Apply(_gpu, 1, new FanState(true, level));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("outside the firmware range [30, 100]", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void Range_edges_are_writable()
    {
        var (fan, _) = Setup();
        Assert.Equal(TxOutcome.Applied, fan.Apply(_gpu, 1, new FanState(true, 30)).Tx.Outcome);
        Assert.Equal(TxOutcome.Applied, fan.Apply(_gpu, 1, new FanState(true, 100)).Tx.Outcome);
    }

    [Fact]
    public void Manual_then_auto_claims_auto_and_releases_it()
    {
        var (fan, _) = Setup();
        string? originalAtWrite = null;
        FakeDriver.OnSet = () => originalAtWrite ??= _journal.Load()?.Items.Single().Original;

        var up = fan.Apply(_gpu, 1, new FanState(true, 55));
        Assert.Equal(TxOutcome.Applied, up.Tx.Outcome);
        Assert.Equal("auto", originalAtWrite);   // on disk before the first byte reached the driver
        Assert.Equal(FanState.Auto, up.Before);
        Assert.Equal(new FanState(true, 55), up.After);
        Assert.Equal(0x11u, U(Stored, Mode(0)));   // bit 0 set, the driver's 0x10 kept
        Assert.Equal(55u, U(Stored, Level(0)));
        Assert.Equal(0x10u, U(Stored, Mode(1)));   // the other channel untouched
        Assert.StartsWith("held", up.Ownership);

        var back = fan.Apply(_gpu, 1, FanState.Auto);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Equal(0x10u, U(Stored, Mode(0)));
        Assert.Equal(55u, U(Stored, Level(0)));   // auto: the level is the firmware's field, not rewritten
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void Two_steps_keep_the_first_original()
    {
        var (fan, _) = Setup();
        fan.Apply(_gpu, 1, new FanState(true, 55));
        var r = fan.Apply(_gpu, 1, new FanState(true, 60));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal("auto", _journal.Load()!.Items.Single().Original);
        Assert.Equal(2, FakeDriver.Sets);
    }

    [Fact]
    public void The_firmware_moving_an_auto_channels_level_is_not_a_failure()
    {
        var (fan, _) = Setup();
        // The driver mirrors the firmware's live duty into every auto channel's level as the write lands.
        FakeDriver.RequestApplier = (state, req) => { var n = (byte[])req.Clone(); W(n, Level(1), 41); return n; };
        var r = fan.Apply(_gpu, 1, new FanState(true, 55));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
    }

    [Fact]
    public void Going_back_to_auto_tolerates_the_firmware_taking_the_level_over()
    {
        var (fan, _) = Setup(control: Control(mode0: 0x11, level0: 55));
        _host.Pid = 100;
        FakeDriver.RequestApplier = (state, req) => { var n = (byte[])req.Clone(); W(n, Level(0), 38); return n; };
        var r = fan.Apply(_gpu, 1, FanState.Auto);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(FanState.Auto, r.After);
    }

    [Fact]
    public void A_manual_level_the_driver_changes_is_failed_and_restored()
    {
        var (fan, _) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.ClampField;
        FakeDriver.Field = Level(0);
        FakeDriver.MisbehaveOnce = true;
        var r = fan.Apply(_gpu, 1, new FanState(true, 55));
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(0x10u, U(Stored, Mode(0)));
        Assert.StartsWith("released", r.Ownership);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void An_unknown_channel_is_refused_before_any_write()
    {
        var (fan, _) = Setup();
        Assert.Throws<InvalidOperationException>(() => fan.Apply(_gpu, 9, new FanState(true, 50)));
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Recovery_gives_a_dead_owners_fan_back_to_the_firmware()
    {
        var (_, hw) = Setup(control: Control(mode0: 0x11, level0: 55));
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(FanControl.Kind, "1", "auto", _host.Now));
        _sentinel.Begin("manual", false);
        _host.Pid = 100;

        var r = new Recovery(hw, _journal, _sentinel, _host).Run(_gpu, canWrite: true);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Written, item.Action);
        Assert.Equal("manual:55", item.Current);
        Assert.Equal(0x10u, U(Stored, Mode(0)));
        Assert.True(r.Clean, r.Detail);
    }

    [Fact]
    public void The_whitelist_takes_a_real_control_block_and_refuses_broken_ones()
    {
        var on = WritePolicySet.Default;
        Assert.Null(on.Check(Slot.ClientFanCoolersSetControl, Control()));
        var dup = Control(); W(dup, FanLayout.ControlEntry(1), 1);
        Assert.Contains("repeats", on.Check(Slot.ClientFanCoolersSetControl, dup));
        var empty = Control(); empty[FanLayout.ControlCount] = 0;
        Assert.Contains("1..32", on.Check(Slot.ClientFanCoolersSetControl, empty));
        var huge = Control(); W(huge, Level(0), 0x80000000);
        Assert.Contains("exceeds", on.Check(Slot.ClientFanCoolersSetControl, huge));
    }

    [Fact]
    public void The_broker_ceiling_check_is_the_firmware_range()
    {
        Assert.Null(FanRules.CheckAgainstStatus(Control(mode0: 0x11, level0: 30), Status()));
        Assert.Contains("outside the firmware range", FanRules.CheckAgainstStatus(Control(mode0: 0x11, level0: 20), Status()));
        Assert.Null(FanRules.CheckAgainstStatus(Control(mode0: 0x10, level0: 0), Status()));   // auto: the level is not ours to bound
    }
}
