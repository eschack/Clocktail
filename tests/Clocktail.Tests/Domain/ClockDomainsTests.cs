using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// A driver shaped like the RTX 5090 (617.42, read-only dump 2026-10-17): 9 topology entries, all type 0xf, XBAR at
/// entry 1, SYS at 3, domain 0x15 at 4, graphics/memory at 0/2 with the Pstates20 ranges. The control GET answers
/// only the entry its mask selects; a SET merges the selected entry into the driver's state.
/// </summary>
internal static class ClockDomainsFixture
{
    public static readonly (uint Domain, short Min, short Max)[] Entries =
        [(0, -1000, 1000), (1, -1000, 1000), (4, -1000, 3000), (2, -1000, 1000), (0x15, -1000, 1000), (0x14, 0, 0), (0x1f, 0, 0), (6, 0, 0), (3, 0, 0)];

    public static byte[] Topology(IEnumerable<(uint Domain, short Min, short Max)>? entries = null, uint type = 0xf)
    {
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsGetInfo);
        int i = 0;
        uint mask = 0;
        foreach (var (d, min, max) in entries ?? Entries)
        {
            int e = ClockDomainsLayout.TopologyAt(i);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e), type);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e + 4), d);
            int r = e + (type == 0xf ? 0x40 : 0x3e);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(r), min);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(r + 2), max);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e + 0x78), d == 0 ? 1u : 2u);
            b[d == 0 ? e + 0x80 : e + 0xc4] = 1;
            mask |= 1u << i++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClockDomainsLayout.TopologyMask), mask);
        return b;
    }

    /// <summary>The driver's whole control state: every entry filled with its type, header bytes as seen live.</summary>
    public static byte[] FullControl(int count = 9, uint type = 0xf)
    {
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsGetControl);
        b[0xe] = 1; b[0xf] = 1;
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClockDomainsLayout.ControlAt(i)), type);
        return b;
    }

    public static void Seed(byte[]? topology = null, byte[]? control = null)
    {
        FakeDriver.Reset();
        var t = topology ?? Topology();
        var c = control ?? FullControl();
        FakeDriver.State[BinaryPrimitives.ReadUInt32LittleEndian(t)] = t;
        FakeDriver.State[BinaryPrimitives.ReadUInt32LittleEndian(c)] = c;
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(c);
        FakeDriver.QueryResponder = req =>
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(req) != word) return null;
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(8));
            var state = FakeDriver.State[word];
            var answer = new byte[state.Length];
            state.AsSpan(0, ClockDomainsLayout.ControlBase).CopyTo(answer);
            BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(8), mask);
            for (int i = 0; i < 32; i++)
                if ((mask >> i & 1) != 0) state.AsSpan(ClockDomainsLayout.ControlAt(i), ClockDomainsLayout.ControlEntry).CopyTo(answer.AsSpan(ClockDomainsLayout.ControlAt(i)));
            return answer;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(req) != word) return req;
            var next = (byte[])state.Clone();
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(8));
            for (int i = 0; i < 32; i++)
                if ((mask >> i & 1) != 0) req.AsSpan(ClockDomainsLayout.ControlAt(i), ClockDomainsLayout.ControlEntry).CopyTo(next.AsSpan(ClockDomainsLayout.ControlAt(i)));
            return next;
        };
    }

    public static int OffsetKhz(int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.Current(Slot.ClockClkDomainsGetControl).AsSpan(ClockDomainsLayout.ControlAt(index) + 0x114));

    public static NvClient Client(bool enabled = true)
    {
        var api = FakeDriver.ApiFor([Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl], [Slot.ClockClkDomainsSetControl]);
        var policies = enabled ? WritePolicySet.Default : WritePolicySet.Default.WithDisabled(Slot.ClockClkDomainsSetControl, "off for this test");
        return new NvClient(api, policies);
    }
}

public class ClockDomainsParserTests
{
    [Fact]
    public void The_5090_topology_names_xbar_sys_and_video_by_domain_id_not_by_position()
    {
        var t = ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology());
        Assert.Equal(9, t.Count);
        var xbar = ClockDomainsParser.Find(t, (uint)ClockDomainId.Xbar);
        var sys = ClockDomainsParser.Find(t, (uint)ClockDomainId.Sys);
        var video = ClockDomainsParser.Find(t, (uint)ClockDomainId.Video);
        Assert.Equal((1, -1000, 1000), (xbar.Index, xbar.MinOffsetMhz, xbar.MaxOffsetMhz));
        Assert.Equal(3, sys.Index);
        Assert.Equal(4, video.Index);
        Assert.Equal(3000, ClockDomainsParser.Find(t, (uint)ClockDomainId.Memory).MaxOffsetMhz);
        Assert.False(ClockDomainsParser.Find(t, 0x14).Adjustable);
    }

    [Fact]
    public void A_duplicate_domain_is_ambiguous_and_an_unknown_type_is_refused()
    {
        var dup = ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology([(1, -100, 100), (1, -100, 100)]));
        Assert.Contains("ambiguous", Assert.Throws<InvalidDataException>(() => ClockDomainsParser.Find(dup, 1)).Message);
        var odd = ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology([(1, -100, 100)], type: 2));
        Assert.Contains("unsupported layout", Assert.Throws<InvalidDataException>(() => ClockDomainsParser.Find(odd, 1)).Message);
        Assert.Contains("no clock domain", Assert.Throws<InvalidDataException>(() => ClockDomainsParser.Find(odd, 0x15)).Message);
    }

    [Fact]
    public void Type_one_entries_keep_their_range_two_bytes_earlier_and_their_offset_eight_bytes_earlier()
    {
        var t = ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology([(1, -250, 500)], type: 1));
        Assert.Equal((-250, 500), (t[0].MinOffsetMhz, t[0].MaxOffsetMhz));
        Assert.Equal(ClockDomainsLayout.ControlAt(0) + 0x10c, ClockDomainsLayout.OffsetAt(0, 1));
        Assert.Equal(ClockDomainsLayout.ControlAt(0) + 0x114, ClockDomainsLayout.OffsetAt(0, 0xf));
        // Absolute offsets: idx*0x304 + 0x230 / 0x238.
        Assert.Equal(3 * 0x304 + 0x238, ClockDomainsLayout.OffsetAt(3, 0xf));
    }

    [Fact]
    public void A_control_answer_for_another_entry_is_refused()
    {
        var b = ClockDomainsFixture.FullControl();
        ClockDomainsParser.PrepareQuery(b, 3);
        Assert.Throws<InvalidDataException>(() => ClockDomainsParser.ParseControl(b, 1));
    }
}

public class ClockDomainRulesTests
{
    private static readonly ClockTopologyEntry Xbar = ClockDomainsParser.Find(ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology()), 1);

    private static byte[] Control(int offsetKhz = 0, int reserved = 0)
    {
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsGetControl);
        ClockDomainsParser.PrepareQuery(b, 1);
        int e = ClockDomainsLayout.ControlAt(1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(e), 0xf);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(e + 0x114), offsetKhz);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(e + 0x120), reserved);
        return b;
    }

    [Fact]
    public void The_range_is_the_topology_s_own_and_both_ends_are_inclusive()
    {
        Assert.IsType<Mutation.Apply>(ClockDomainRules.Plan(Xbar, Control(), 1_000_000));
        Assert.IsType<Mutation.Apply>(ClockDomainRules.Plan(Xbar, Control(), -1_000_000));
        Assert.Contains("driver range", Assert.IsType<Mutation.Abort>(ClockDomainRules.Plan(Xbar, Control(), 1_000_001)).Reason);
    }

    [Fact]
    public void Equal_value_is_an_empty_plan_and_a_stray_rail_slot_blocks_the_write()
    {
        Assert.Empty(Assert.IsType<Mutation.Apply>(ClockDomainRules.Plan(Xbar, Control(15_000), 15_000)).Edits);
        Assert.Contains("rail offset", Assert.IsType<Mutation.Abort>(ClockDomainRules.Plan(Xbar, Control(0, 5), 15_000)).Reason);
    }

    [Fact]
    public void A_domain_without_a_range_cannot_be_offset()
    {
        var fixedDomain = ClockDomainsParser.Find(ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology([(0x14, 0, 0), (1, -1, 1)])), 0x14);
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsGetControl);
        ClockDomainsParser.PrepareQuery(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClockDomainsLayout.ControlAt(0)), 0xf);
        Assert.Contains("no offset range", Assert.IsType<Mutation.Abort>(ClockDomainRules.Plan(fixedDomain, b, 0)).Reason);
    }
}

public class ClockDomainPolicyTests
{
    private static byte[] Write(int idx, int khz, uint type = 0xf)
    {
        var b = NvClient.NewBuffer(Slot.ClockClkDomainsSetControl);
        ClockDomainsParser.PrepareQuery(b, idx);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClockDomainsLayout.ControlAt(idx)), type);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ClockDomainsLayout.ControlAt(idx) + (type == 0xf ? 0x114 : 0x10c)), khz);
        return b;
    }

    private static string? Check(byte[] b) => WritePolicySet.Default.Check(Slot.ClockClkDomainsSetControl, b);

    /// <summary>On since its live drill (2026-10-20).</summary>
    [Fact]
    public void On_by_default_since_its_drill()
    {
        Assert.Null(WritePolicySet.Default.Check(Slot.ClockClkDomainsSetControl, Write(1, 15_000)));
        Assert.True(WritePolicySet.Default.Get(Slot.ClockClkDomainsSetControl)!.Complete);
    }

    [Fact]
    public void Kind_0xf_accepts_any_single_entry_including_the_5090_s_sys_and_video_positions()
    {
        Assert.Null(Check(Write(1, 15_000)));
        Assert.Null(Check(Write(3, -15_000)));
        Assert.Null(Check(Write(4, 0x1f3fc18)));
        Assert.Null(Check(Write(7, 15_000, type: 1)));
    }

    [Fact]
    public void Kind_0xf_refuses_unsupported_entries_and_stray_bytes_elsewhere()
    {
        Assert.Contains("exactly one", Check(Write(1, 0).Also(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x6))));
        Assert.Contains("no known layout", Check(Write(1, 15_000, type: 2)));
        Assert.Contains("gateway bound", Check(Write(1, 0x1f3fc19)));
        Assert.Contains("must be zero", Check(Write(1, 15_000).Also(b => b[ClockDomainsLayout.ControlAt(1) + 0x114 + 0xc] = 1)));
        Assert.Contains("outside the selected entry", Check(Write(1, 15_000).Also(b => b[ClockDomainsLayout.ControlAt(2)] = 0xf)));
        Assert.Contains("outside the selected entry", Check(Write(1, 15_000).Also(b => b[ClockDomainsLayout.ControlAt(0) + 4] = 1)));
    }
}

internal static class ByteArrayTestExtensions
{
    public static byte[] Also(this byte[] b, Action<byte[]> f) { f(b); return b; }
}

[Collection("fake-driver")]
public class ClockDomainOffsetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;

    public ClockDomainOffsetsTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ClockDomainOffsets Setup(bool enabled = true)
    {
        ClockDomainsFixture.Seed();
        return new ClockDomainOffsets(ClockDomainsFixture.Client(enabled), _journal, _sentinel, _host);
    }

    private static readonly GpuSession Gpu = new(1, "fake");

    [Fact]
    public void Sys_is_written_at_its_own_entry_and_nothing_else_moves()
    {
        var co = Setup();
        var before = (byte[])FakeDriver.Current(Slot.ClockClkDomainsGetControl).Clone();
        var r = co.Apply(new GpuSession(1, "fake"), ClockDomainId.Sys, 15_000);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(3, r.Index);
        Assert.Equal((0, 15_000), (r.BeforeKhz, r.AfterKhz));
        Assert.Equal(15_000, ClockDomainsFixture.OffsetKhz(3));
        var after = FakeDriver.Current(Slot.ClockClkDomainsGetControl);
        int f = ClockDomainsLayout.OffsetAt(3, 0xf);
        Assert.Equal(f, Transaction.FirstDifference(before, after, []));
        Assert.Null(Transaction.FirstDifference(before.AsSpan(f + 4), after.AsSpan(f + 4), []));
        Assert.StartsWith("held", r.Ownership);
        Assert.Equal("0", _journal.Load()!.Items.Single(i => i.Kind == "clockdomain" && i.Key == "sys").Original);
    }

    [Fact]
    public void Going_back_to_zero_releases_the_record()
    {
        var co = Setup();
        co.Apply(Gpu, ClockDomainId.Xbar, -30_000);
        var back = co.Apply(Gpu, ClockDomainId.Xbar, 0);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Equal_value_writes_nothing_and_holds_nothing()
    {
        var co = Setup();
        var r = co.Apply(Gpu, ClockDomainId.Video, 0);
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.StartsWith("nothing held", r.Ownership);
    }

    [Fact]
    public void Graphics_and_memory_belong_to_pstates20()
    {
        var co = Setup();
        Assert.Throws<InvalidOperationException>(() => co.Apply(Gpu, ClockDomainId.Graphics, 15_000));
        Assert.Throws<InvalidOperationException>(() => co.Apply(Gpu, ClockDomainId.Memory, 15_000));
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Out_of_range_aborts_before_the_driver_and_claims_nothing()
    {
        var co = Setup();
        var r = co.Apply(Gpu, ClockDomainId.Xbar, 1_500_000);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void With_the_setter_off_the_write_is_refused_before_the_driver()
    {
        var co = Setup(enabled: false);
        var r = co.Apply(Gpu, ClockDomainId.Xbar, 15_000);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("disabled", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_driver_that_drops_the_offset_is_caught_and_nothing_is_owed()
    {
        var co = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.ClampField;
        FakeDriver.Field = ClockDomainsLayout.OffsetAt(1, 0xf);
        FakeDriver.MisbehaveOnce = true;
        var r = co.Apply(Gpu, ClockDomainId.Xbar, 15_000);
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.NotNeeded, r.Tx.Restore);   // the entry still reads as before the write
        Assert.Equal(0, ClockDomainsFixture.OffsetKhz(1));
        Assert.StartsWith("released", r.Ownership);
    }

    [Fact]
    public void The_registry_entry_writes_and_recovers_through_the_bare_writer()
    {
        Setup();
        var hw = ClockDomainsFixture.Client(true);
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        var (cell, plan) = ClockDomainOffsets.Prepare(hw, Gpu, "xbar", "15000");
        Assert.Equal("clockdomain/xbar", cell.Id);
        Assert.Equal(TxOutcome.Applied, writer.Apply(Gpu, cell, plan!).Tx.Outcome);
        Assert.Equal(15_000, ClockDomainsFixture.OffsetKhz(1));

        var item = _journal.Load()!.Items.Single();
        var (rc, rp) = ClockDomainOffsets.Prepare(hw, Gpu, item.Key, item.Original);
        Assert.StartsWith("released", writer.Apply(Gpu, rc, rp!).Ownership);
        Assert.Equal(0, ClockDomainsFixture.OffsetKhz(1));
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void The_registry_entry_refuses_foreign_keys_and_unreadable_values()
    {
        Setup();
        var hw = ClockDomainsFixture.Client(true);
        Assert.Null(ClockDomainOffsets.Prepare(hw, Gpu, "sys", "fast").Plan);
        Assert.Throws<ArgumentException>(() => ClockDomainOffsets.Prepare(hw, Gpu, "graphics", "0"));
        Assert.Throws<ArgumentException>(() => ClockDomainOffsets.Prepare(hw, Gpu, "memory", "0"));
        var (cell, plan) = ClockDomainOffsets.Prepare(hw, Gpu, "video", "2000000");
        Assert.Equal(TxOutcome.Aborted, new OwnedWriter(hw, _journal, _sentinel, _host).Apply(Gpu, cell, plan!).Tx.Outcome);
    }
}
