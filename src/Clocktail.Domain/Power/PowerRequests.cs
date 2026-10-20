using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Domain.Power;

/// <summary>
/// The two client power requests exposed as watt caps. Values are the
/// command codes the driver keys the request by.
/// </summary>
public enum PowerRequestKind : uint
{
    /// <summary>0xFE, the "ordinary" request: has a lower bound (the board minimum) and an upper bound (the board maximum).</summary>
    Ordinary = 0xfe,
    /// <summary>0xF8, the "additional" request: upper bound only.</summary>
    Additional = 0xf8,
}

/// <summary>One board's record from ClientPwrPoliciesGetInfo. Values are milliwatts.</summary>
public sealed record BoardPowerInfo(int Board, uint Flag, uint MinMw, uint DefaultMw, uint MaxMw)
{
    /// <summary>
    /// With the flag at 1 the requests are read directly; with 0 a cross-check against the raw policy table
    /// (slots 15/35) would come first before an absent ordinary request is accepted. The cross-check is not implemented.
    /// </summary>
    public bool DirectlyReadable => Flag == 1;
}

/// <summary>One request as the driver reported it. 0 and 0xFFFFFFFF both mean "no such request".</summary>
public sealed record PowerRequest(PowerRequestKind Kind, int Board, uint RawValue)
{
    public bool Present => RawValue != 0 && RawValue != uint.MaxValue;
    public uint? Mw => Present ? RawValue : null;
    /// <summary>Offset of this request's value inside the 0x528 control structure.</summary>
    public int ValueOffset => PowerRequestsLayout.ControlEntry(Board);
}

public sealed record PowerRequestsSnapshot(BoardPowerInfo Board, PowerRequest Ordinary, PowerRequest Additional, byte[] InfoRaw, byte[] OrdinaryRaw, byte[] AdditionalRaw)
{
    /// <summary>The tightest present request, or null when neither is present. Which request actually limits the GPU is a separate readback (NVML enforced).</summary>
    public uint? CapMw => (Ordinary.Mw, Additional.Mw) switch
    {
        (uint a, uint b) => Math.Min(a, b),
        (uint a, null) => a,
        (null, uint b) => b,
        _ => null,
    };
}

/// <summary>
/// Both structures share one shape: a 0x28-byte header (version, board mask) followed by 32 board entries.
/// INFO (0x728 r1) entries are 0x38 bytes: flag, reserved (must be 0), min, default, max (mW).
/// CONTROL (0x528 r1) entries are 0x28 bytes: value (mW), command code. Only one board bit is ever set.
/// 0x28 + 32 x 0x38 = 0x728, 0x28 + 32 x 0x28 = 0x528.
/// </summary>
public static class PowerRequestsLayout
{
    public const int Boards = 32;
    public const int Header = 0x28;
    public const int MaskOffset = 4;
    public const int InfoStride = 0x38;
    public const int ControlStride = 0x28;

    public static int InfoEntry(int board) => Header + board * InfoStride;
    public static int ControlEntry(int board) => Header + board * ControlStride;
}

public static class PowerRequestsParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static bool Unset(uint v) => v == 0 || v == uint.MaxValue;

    private static void CheckShape(byte[] raw, Slot slot, string what)
    {
        var shape = Slots.Info(slot).Shape!.Value;
        if (raw.Length != shape.Size || U(raw, 0) != shape.Word)
            throw new InvalidDataException($"The {what} read returned an unexpected version or size.");
    }

    /// <summary>
    /// The rule: exactly one board in the mask carries a usable record (flag 0 or 1, reserved word 0,
    /// min/default/max all set and ordered). A second usable board, or a malformed one, invalidates the whole read.
    /// </summary>
    public static BoardPowerInfo ParseInfo(byte[] raw)
    {
        CheckShape(raw, Slot.ClientPwrPoliciesGetInfo, "power-request INFO");
        uint mask = U(raw, PowerRequestsLayout.MaskOffset);
        BoardPowerInfo? found = null;
        for (int b = 0; b < PowerRequestsLayout.Boards; b++)
        {
            if ((mask >> b & 1) == 0) continue;
            int e = PowerRequestsLayout.InfoEntry(b);
            uint flag = U(raw, e), reserved = U(raw, e + 4), min = U(raw, e + 8), def = U(raw, e + 0xc), max = U(raw, e + 0x10);
            if (flag > 1) continue;                                    // not a client power record
            if (reserved != 0) throw new InvalidDataException($"Board {b}: the reserved word is {reserved:x}, not 0.");
            if (Unset(min) || Unset(def) || Unset(max) || def < min || max < def)
                throw new InvalidDataException($"Board {b}: the driver range {min}/{def}/{max} mW is not usable.");
            if (found != null) throw new InvalidDataException($"Boards {found.Board} and {b} both carry a power record; exactly one is expected.");
            found = new BoardPowerInfo(b, flag, min, def, max);
        }
        return found ?? throw new InvalidDataException($"No board in mask 0x{mask:x} carries a client power record.");
    }

    /// <summary>The query: version, one board bit, the command code in that board's entry, everything else zero.</summary>
    public static byte[] BuildControlQuery(int board, PowerRequestKind kind)
    {
        if ((uint)board >= PowerRequestsLayout.Boards) throw new ArgumentOutOfRangeException(nameof(board));
        var b = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetControl);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PowerRequestsLayout.MaskOffset), 1u << board);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PowerRequestsLayout.ControlEntry(board) + 4), (uint)kind);
        return b;
    }

    /// <summary>The driver must echo the version, the board bit and the command before the value is believed.</summary>
    public static PowerRequest ParseControl(byte[] raw, int board, PowerRequestKind kind)
    {
        CheckShape(raw, Slot.ClientPwrPoliciesGetControl, "power-request CONTROL");
        if (U(raw, PowerRequestsLayout.MaskOffset) != 1u << board)
            throw new InvalidDataException($"The driver did not echo board {board} (mask 0x{U(raw, PowerRequestsLayout.MaskOffset):x}).");
        int e = PowerRequestsLayout.ControlEntry(board);
        if (U(raw, e + 4) != (uint)kind)
            throw new InvalidDataException($"The driver did not echo command 0x{(uint)kind:x} (got 0x{U(raw, e + 4):x}).");
        return new PowerRequest(kind, board, U(raw, e));
    }
}

public static class PowerRequestsReader
{
    /// <summary>The board that carries the client power record, with its driver range. Read-only; not a request.</summary>
    public static BoardPowerInfo ReadBoard(NvClient hw, GpuSession gpu) => ReadBoard(hw, gpu, out _);

    private static BoardPowerInfo ReadBoard(NvClient hw, GpuSession gpu, out byte[] info)
    {
        info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        var r = hw.Call(gpu, Slot.ClientPwrPoliciesGetInfo, info);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return PowerRequestsParser.ParseInfo(info);
    }

    public static PowerRequestsSnapshot Read(NvClient hw, GpuSession gpu)
    {
        var board = ReadBoard(hw, gpu, out var info);

        PowerRequest Query(PowerRequestKind kind, out byte[] raw)
        {
            raw = PowerRequestsParser.BuildControlQuery(board.Board, kind);
            var q = hw.Call(gpu, Slot.ClientPwrPoliciesGetControl, raw);
            if (!q.IsOk) throw new InvalidOperationException(q.ToString());
            return PowerRequestsParser.ParseControl(raw, board.Board, kind);
        }
        var ordinary = Query(PowerRequestKind.Ordinary, out var feRaw);
        var additional = Query(PowerRequestKind.Additional, out var f8Raw);
        return new PowerRequestsSnapshot(board, ordinary, additional, info, feRaw, f8Raw);
    }
}
