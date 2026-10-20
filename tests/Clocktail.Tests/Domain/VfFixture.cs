using System.Buffers.Binary;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// A fake Core V/F curve that behaves the way the RTX 5090 answered on driver 617.42:
/// regional mode, a 132-point mask, Status and Control reads that are queries (a zero mask gets nothing but the
/// mode byte back), a setter that applies only the selected points, and a status that reports
/// base + global + offset unless a test says otherwise. The P-state table carries the global core offset and the
/// firmware's ±1000 MHz range.
/// </summary>
internal sealed class FakeVf
{
    public static readonly uint InfoWord = Word(Slot.ClockClientClkVfPointsGetInfo);
    public static readonly uint StatusWord = Word(Slot.ClockClientClkVfPointsGetStatus);
    public static readonly uint ControlWord = Word(Slot.ClockClientClkVfPointsGetControl);
    public static readonly uint PstatesWord = Word(Slot.GetPstates20);
    private static uint Word(Slot s) => Slots.Info(s).Shape!.Value.Word;

    public VfMode Mode = VfMode.Regional;
    public int MaskPoints = 132;
    /// <summary>Points 0..Editable−1 are Core points (type 0, info flag 1).</summary>
    public int Editable = 127;
    /// <summary>0.700 V + 5 mV per point: point 80 sits at 1.100 V.</summary>
    public Func<int, uint> Voltage = i => 700_000 + (uint)i * 5_000;
    /// <summary>900 MHz + 20 MHz per point: 4 kHz per µV, so a 25 mV shift is exactly +100 MHz.</summary>
    public Func<int, uint> Base = i => 900_000 + (uint)i * 20_000;
    /// <summary>Status +0x08 minus status +0x10: the Core voltage-demand offset in force.</summary>
    public int ShiftUv;
    public int GlobalKhz;
    public int RangeKhz = 1_000_000;
    /// <summary>What the status reports for a point running an offset; null = base + global + offset.</summary>
    public Func<int, int, uint>? Effective;
    /// <summary>Runs on every status answer: lets a test make the driver contradict itself.</summary>
    public Action<byte[], byte[]>? StatusHook;
    /// <summary>Runs after each control query is answered (the number of control reads so far).</summary>
    public Action<int>? AfterControlRead;

    public byte[]? LastRequest;
    public int ControlReads;

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void I(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    public static int Off(byte[] b, int i) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(VfLayout.OffsetField(i)));

    /// <summary>Legacy curves add two (80-point) or three (128-point) type-1 points right after the Core points.</summary>
    private uint Type(int i)
    {
        if (i < Editable) return 0;
        if (Mode == VfMode.Legacy) return i < Editable + 2 + (Editable != 80 ? 1 : 0) ? 1u : 0u;
        return 1;
    }

    public byte[] Info()
    {
        var b = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetInfo);
        VfLayout.WriteMask(b, VfLayout.FirstPoints(MaskPoints));
        for (int i = 0; i < MaskPoints; i++)
        {
            W(b, VfLayout.Info(i), Type(i));
            b[VfLayout.Info(i) + 4] = (byte)(i < Editable ? 1 : 0);
        }
        return b;
    }

    public byte[] Control()
    {
        var b = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetControl);
        VfLayout.WriteMask(b, VfLayout.FirstPoints(MaskPoints));
        for (int i = 0; i < MaskPoints; i++) W(b, VfLayout.Control(i), Type(i));
        return b;
    }

    public byte[] Status(byte[] control)
    {
        var b = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetStatus);
        VfLayout.WriteMask(b, VfLayout.FirstPoints(MaskPoints));
        b[VfLayout.StatusMode] = (byte)Mode;
        for (int i = 0; i < MaskPoints; i++)
        {
            int s = VfLayout.Status(i);
            W(b, s, Type(i));
            if (i >= Editable) continue;
            int offset = Off(control, i);
            uint eff = Effective?.Invoke(i, offset) ?? (uint)(Base(i) + GlobalKhz + offset);
            uint v = (uint)(Voltage(i) + ShiftUv);
            if (Mode == VfMode.Regional) { W(b, s + 4, eff); W(b, s + 8, v); W(b, s + 0xc, Base(i)); W(b, s + 0x10, Voltage(i)); }
            else { W(b, s + 0x34, eff); W(b, s + 0x38, Voltage(i)); }
        }
        StatusHook?.Invoke(b, control);
        return b;
    }

    public byte[] Pstates()
    {
        var b = NvClient.NewBuffer(Slot.GetPstates20);
        W(b, 4, 1); W(b, 8, 1); W(b, 0xc, 1);
        int p = Pstates20Layout.Pstate(0); W(b, p, 0); W(b, p + 4, 1);
        int c = Pstates20Layout.Clock(0, 0);
        W(b, c, 0); W(b, c + 4, 1); W(b, c + 8, 1); I(b, c + 0xc, GlobalKhz); I(b, c + 0x10, -RangeKhz); I(b, c + 0x14, RangeKhz);
        I(b, c + 0x18, 180_000 + GlobalKhz); I(b, c + 0x1c, 3_090_000 + GlobalKhz);
        return b;
    }

    /// <summary>Seed the fake driver with this curve; <paramref name="extraGets"/> wires more readable slots (rails).</summary>
    public NvClient Install(params Slot[] extraGets)
    {
        FakeDriver.Reset();
        LastRequest = null;
        ControlReads = 0;
        FakeDriver.State[InfoWord] = Info();
        FakeDriver.State[ControlWord] = Control();
        FakeDriver.State[StatusWord] = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetStatus);
        FakeDriver.State[PstatesWord] = Pstates();
        FakeDriver.QueryResponder = q =>
        {
            uint word = BitConverter.ToUInt32(q);
            bool asked = q.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes).ContainsAnyExcept((byte)0);
            if (word == StatusWord)
            {
                if (asked) return Status(FakeDriver.State[ControlWord]);
                var bare = NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetStatus);
                bare[VfLayout.StatusMode] = (byte)Mode;
                return bare;
            }
            if (word == ControlWord)
            {
                ControlReads++;
                var answer = asked ? (byte[])FakeDriver.State[ControlWord].Clone() : NvClient.NewBuffer(Slot.ClockClientClkVfPointsGetControl);
                AfterControlRead?.Invoke(ControlReads);
                return answer;
            }
            return null;
        };
        // BOARDOBJGRP set semantics: only the selected entries are applied, and of those only the offset.
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != ControlWord) return req;
            LastRequest = (byte[])req.Clone();
            var next = (byte[])state.Clone();
            for (int i = 0; i < VfLayout.MaxPoints; i++)
                if (VfLayout.Has(req.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes), i))
                    req.AsSpan(VfLayout.OffsetField(i), 4).CopyTo(next.AsSpan(VfLayout.OffsetField(i)));
            return next;
        };
        Slot[] gets = [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20, .. extraGets];
        // The kind-7 setter is off by default until its live drill; the fake enables it unless a test asks for the default.
        var policies = DefaultPolicy ? WritePolicySet.Default : WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl);
        return new NvClient(FakeDriver.ApiFor(gets, [Slot.ClockClientClkVfPointsSetControl]), policies);
    }

    /// <summary>Install with the shipped write policy instead of one with the kind-7 setter enabled.</summary>
    public bool DefaultPolicy;

    public static int[] Offsets() => VfParser.Offsets(FakeDriver.State[ControlWord], 127);
}

/// <summary>The RTX 5090's stock Core curve as read live on driver 617.42 (2026-10-17): voltages
/// in µV and bases in kHz, points 0..126.</summary>
internal static class Live5090
{
    public static readonly uint[] Voltages =
    [
        450000, 460000, 465000, 470000, 475000, 485000, 490000, 495000, 500000, 510000, 515000, 520000, 525000, 535000, 540000, 545000,
        550000, 560000, 565000, 570000, 575000, 585000, 590000, 595000, 600000, 610000, 615000, 620000, 625000, 635000, 640000, 645000,
        650000, 660000, 665000, 670000, 675000, 685000, 690000, 695000, 700000, 710000, 715000, 720000, 725000, 735000, 740000, 745000,
        750000, 760000, 765000, 770000, 775000, 785000, 790000, 795000, 800000, 810000, 815000, 820000, 825000, 835000, 840000, 845000,
        850000, 860000, 865000, 870000, 875000, 885000, 890000, 895000, 900000, 910000, 915000, 920000, 925000, 935000, 940000, 945000,
        950000, 960000, 965000, 970000, 975000, 985000, 990000, 995000, 1000000, 1010000, 1015000, 1020000, 1025000, 1035000, 1040000, 1045000,
        1050000, 1060000, 1065000, 1070000, 1075000, 1085000, 1090000, 1095000, 1100000, 1110000, 1115000, 1120000, 1125000, 1135000, 1140000, 1145000,
        1150000, 1160000, 1165000, 1170000, 1175000, 1185000, 1190000, 1195000, 1200000, 1210000, 1215000, 1220000, 1225000, 1235000, 1240000,
    ];

    public static readonly uint[] Bases =
    [
        .. Enumerable.Repeat(180000u, 47),
        187000, 292000, 390000, 495000, 600000, 697000, 802000, 907000, 1005000, 1110000, 1207000, 1312000, 1417000, 1515000, 1620000, 1725000,
        1822000, 1927000, 2032000, 2130000, 2212000, 2235000, 2265000, 2287000, 2317000, 2340000, 2370000, 2392000, 2415000, 2445000, 2467000, 2490000,
        2512000, 2535000, 2557000, 2587000, 2602000, 2625000, 2647000, 2662000, 2685000, 2715000, 2737000, 2752000, 2775000, 2790000, 2805000, 2820000,
        2835000, 2842000, 2857000, 2872000, 2880000, 2895000, 2902000, 2917000, 2932000, 2940000, 2955000, 2962000, 2977000, 2985000, 3000000, 3007000,
        3022000, 3030000, 3037000, 3052000, 3060000, 3067000, 3082000, 3090000, 3097000, 3112000, 3120000, 3127000, 3135000, 3142000, 3157000, 3165000,
    ];

    /// <summary>The same curve's bases read during a 538 W burn at 75 °C (drill 19 preflight, 2026-10-17), effective equal
    /// to base on every point. Points 0..66 are unchanged; from the knee up the firmware lowered each base by 1 to 12
    /// clock steps, more at higher voltage (2932.5 → 2850 MHz at the ceiling).</summary>
    public static readonly uint[] Bases75C =
    [
        .. Enumerable.Repeat(180000u, 47),
        187000, 292000, 390000, 495000, 600000, 697000, 802000, 907000, 1005000, 1110000, 1207000, 1312000, 1417000, 1515000, 1620000, 1725000,
        1822000, 1927000, 2032000, 2130000, 2205000, 2227000, 2257000, 2280000, 2302000, 2325000, 2347000, 2370000, 2392000, 2415000, 2437000, 2452000,
        2475000, 2490000, 2512000, 2527000, 2550000, 2565000, 2580000, 2602000, 2617000, 2662000, 2677000, 2692000, 2707000, 2715000, 2730000, 2745000,
        2752000, 2767000, 2782000, 2790000, 2805000, 2812000, 2827000, 2842000, 2850000, 2857000, 2872000, 2880000, 2895000, 2902000, 2917000, 2925000,
        2932000, 2947000, 2955000, 2962000, 2977000, 2985000, 2992000, 3000000, 3015000, 3022000, 3030000, 3037000, 3045000, 3052000, 3067000, 3075000,
    ];

    /// <summary>The same curve's bases read at idle at 33 °C inside drill 19's write window (d03, 2026-10-17). From the
    /// fixture's warmer idle: −1 step at points 67 and 69, +1 or +2 from point 75 up (2940 MHz at the ceiling).</summary>
    public static readonly uint[] Bases33C =
    [
        .. Enumerable.Repeat(180000u, 47),
        187000, 292000, 390000, 495000, 600000, 697000, 802000, 907000, 1005000, 1110000, 1207000, 1312000, 1417000, 1515000, 1620000, 1725000,
        1822000, 1927000, 2032000, 2130000, 2205000, 2235000, 2257000, 2287000, 2317000, 2340000, 2370000, 2392000, 2422000, 2445000, 2467000, 2490000,
        2520000, 2542000, 2565000, 2587000, 2610000, 2632000, 2655000, 2677000, 2692000, 2722000, 2745000, 2767000, 2782000, 2805000, 2820000, 2827000,
        2842000, 2857000, 2872000, 2880000, 2895000, 2902000, 2917000, 2932000, 2940000, 2955000, 2962000, 2977000, 2985000, 3000000, 3007000, 3022000,
        3030000, 3045000, 3052000, 3067000, 3075000, 3082000, 3097000, 3105000, 3112000, 3120000, 3135000, 3142000, 3150000, 3157000, 3172000, 3180000,
    ];

    /// <summary>NVVDD MAX, the firmware's Core voltage ceiling on this board (read live).</summary>
    public const uint CeilingUv = 1_095_000;

    public static FakeVf Curve() => new() { Voltage = i => Voltages[i], Base = i => Bases[i] };

    /// <summary>The curve as the driver reported it at 75 °C.</summary>
    public static FakeVf Curve75C() => new() { Voltage = i => Voltages[i], Base = i => Bases75C[i] };
}
