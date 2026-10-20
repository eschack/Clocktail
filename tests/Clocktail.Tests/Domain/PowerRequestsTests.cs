using System.Buffers.Binary;
using Clocktail.Domain.Power;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class PowerRequestsTests
{
    private const int Board = 2;

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

    /// <summary>A 5090-like INFO: one board, flag 1, 300 W min, 575 W default, 600 W max.</summary>
    private static byte[] Info(uint mask = 1u << Board, uint flag = 1, uint min = 300_000, uint def = 575_000, uint max = 600_000, int board = Board)
    {
        var b = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(b, 4, mask);
        int e = PowerRequestsLayout.InfoEntry(board);
        W(b, e, flag); W(b, e + 8, min); W(b, e + 0xc, def); W(b, e + 0x10, max);
        return b;
    }

    /// <summary>Answers a CONTROL query by filling the board's value; leaves any other structure (the INFO read) to the stored state.</summary>
    private static byte[]? Answer(byte[] query, uint value)
    {
        if (BitConverter.ToUInt32(query) != Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word) return null;
        var b = (byte[])query.Clone();
        W(b, PowerRequestsLayout.ControlEntry(Board), value);
        return b;
    }

    [Fact]
    public void Layout_fills_both_structures_exactly()
    {
        Assert.Equal(0x728, PowerRequestsLayout.InfoEntry(PowerRequestsLayout.Boards - 1) + PowerRequestsLayout.InfoStride);
        Assert.Equal(0x528, PowerRequestsLayout.ControlEntry(PowerRequestsLayout.Boards - 1) + PowerRequestsLayout.ControlStride);
    }

    [Fact]
    public void Info_picks_the_single_usable_board()
    {
        var i = PowerRequestsParser.ParseInfo(Info());
        Assert.Equal(Board, i.Board);
        Assert.Equal(300_000u, i.MinMw);
        Assert.Equal(575_000u, i.DefaultMw);
        Assert.Equal(600_000u, i.MaxMw);
        Assert.True(i.DirectlyReadable);
    }

    [Fact]
    public void Info_rejects_malformed_or_ambiguous_records()
    {
        Assert.Contains("not usable", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseInfo(Info(max: 0xFFFFFFFF))).Message);
        Assert.Contains("not usable", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseInfo(Info(def: 650_000))).Message);
        var two = Info(mask: (1u << Board) | (1u << 5));
        int e = PowerRequestsLayout.InfoEntry(5);
        W(two, e, 1); W(two, e + 8, 1); W(two, e + 0xc, 2); W(two, e + 0x10, 3);
        Assert.Contains("exactly one", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseInfo(two)).Message);
        var reserved = Info(); W(reserved, PowerRequestsLayout.InfoEntry(Board) + 4, 7);
        Assert.Contains("reserved", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseInfo(reserved)).Message);
        Assert.Contains("No board", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseInfo(Info(mask: 0))).Message);
    }

    [Fact]
    public void Query_carries_one_board_bit_and_the_command_and_nothing_else()
    {
        var q = PowerRequestsParser.BuildControlQuery(Board, PowerRequestKind.Additional);
        Assert.Equal(1u << Board, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(4)));
        Assert.Equal(0xf8u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(PowerRequestsLayout.ControlEntry(Board) + 4)));
        int nonZero = 0;
        for (int o = 4; o < q.Length; o += 4) if (BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(o)) != 0) nonZero++;
        Assert.Equal(2, nonZero);
    }

    [Fact]
    public void Control_requires_the_echo_and_reads_absence_as_0_or_minus_1()
    {
        var q = PowerRequestsParser.BuildControlQuery(Board, PowerRequestKind.Ordinary);
        Assert.Equal(575_000u, PowerRequestsParser.ParseControl(Answer(q, 575_000)!, Board, PowerRequestKind.Ordinary).Mw);
        Assert.False(PowerRequestsParser.ParseControl(Answer(q, 0)!, Board, PowerRequestKind.Ordinary).Present);
        Assert.False(PowerRequestsParser.ParseControl(Answer(q, 0xFFFFFFFF)!, Board, PowerRequestKind.Ordinary).Present);
        Assert.Contains("echo command", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseControl(Answer(q, 1)!, Board, PowerRequestKind.Additional)).Message);
        var wrongBoard = Answer(q, 1)!; W(wrongBoard, 4, 1);
        Assert.Contains("echo board", Assert.Throws<InvalidDataException>(() => PowerRequestsParser.ParseControl(wrongBoard, Board, PowerRequestKind.Ordinary)).Message);
    }

    [Fact]
    public void Reader_asks_once_per_request_and_derives_the_cap()
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = Info();
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        var commands = new List<uint>();
        FakeDriver.QueryResponder = q =>
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(PowerRequestsLayout.ControlEntry(Board) + 4));
            if (BitConverter.ToUInt32(q) != Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word) return null;
            commands.Add(cmd);
            return Answer(q, cmd == 0xfe ? 575_000u : 0xFFFFFFFF);
        };
        var hw = new NvClient(FakeDriver.ApiFor([Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl]));
        var s = PowerRequestsReader.Read(hw, new GpuSession(1, "fake"));
        Assert.Equal([0xfeu, 0xf8u], commands);
        Assert.Equal(575_000u, s.Ordinary.Mw);
        Assert.False(s.Additional.Present);
        Assert.Equal(575_000u, s.CapMw);
        Assert.Equal(3, FakeDriver.Gets);

        FakeDriver.QueryResponder = q => Answer(q, 450_000);
        s = PowerRequestsReader.Read(hw, new GpuSession(1, "fake"));
        Assert.Equal(450_000u, s.CapMw);
    }

    [Fact]
    public void Reader_refuses_a_driver_that_does_not_echo()
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = Info();
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);   // a stored zero structure: no board bit, no command
        var hw = new NvClient(FakeDriver.ApiFor([Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl]));
        Assert.Contains("echo board", Assert.Throws<InvalidDataException>(() => PowerRequestsReader.Read(hw, new GpuSession(1, "fake"))).Message);
    }
}
