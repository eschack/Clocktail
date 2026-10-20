using System.Numerics;
using Clocktail.Core.Native;
using Clocktail.Hw;

namespace Clocktail.Core.Gpu;

public enum Rail { Nvvdd = 0, Msvdd = 1 }

/// <summary>Per-rail offsets in microvolts, exactly as the driver stores them.</summary>
public readonly record struct RailOffsets(int RelUv, int AltUv, int OvUv, int VminUv);

/// <summary>Evaluated (post-offset) limits reported by the driver, in microvolts.</summary>
public sealed record RailLimits(
    uint CurrentUv,   // status +0xa4: present voltage request
    uint RelUv,       // status +0xa8
    uint AltOpUv,     // status +0xac
    uint OvUv,        // status +0xb0
    uint MaxUv,       // status +0xb4: the rail's current maximum voltage (ceiling checks compare this field)
    uint VminUv);     // status +0xb8

public sealed record RailState(Rail Rail, uint ControlKind, RailOffsets Offsets, RailLimits Limits);

public sealed record VoltSnapshot(uint RailMask, IReadOnlyList<RailState> Rails, byte[] RawControl, byte[] RawStatus);

/// <summary>
/// Private VOLT interface. Control slot order is the field map {3,0,1,2}.
/// <list type="bullet">
/// <item>info   (id 0x2c73afdc) v2, 0x184c bytes: +4 rail mask, per-rail entries of 0xc0 bytes starting at 0x4c.</item>
/// <item>control(id 0xa3070db0 get / 0x87c55c8a set) v2, 0xac8 bytes: +4 rail mask, +8 legacy boost byte, per-rail entries
///   of 0x54 bytes starting at 0x48: [+0]=kind, [+4]=REL, [+8]=ALT/OP, [+0xc]=OV, [+0x10]=VMIN offsets.</item>
/// <item>status (id 0x5d0634ee) v2, 0x1620 bytes: +4 rail mask, per-rail entries of 0xac bytes starting at 0xa4.</item>
/// </list>
/// </summary>
public static class VoltRails
{
    private const int InfoSize = 0x184c, ControlSize = 0xac8, StatusSize = 0x1620;
    private const int ControlEntry = 0x54, ControlBase = 0x48;
    private const int StatusEntry = 0xac, StatusBase = 0xa4;

    public static VoltSnapshot Read(NvApi api, nint gpu)
    {
        var info = VoltProbe.Info(api, gpu);
        if (info.Status != 0 || info.Data == null) throw new NvApiException("voltage info", info.Status);
        uint mask = BitConverter.ToUInt32(info.Data, 4);
        if ((mask & 1) == 0) throw new NotSupportedException("The NVIDIA driver did not expose NVVDD.");

        var ctl = VoltProbe.Control(api, gpu, mask);
        if (ctl.Status != 0 || ctl.Data == null) throw new NvApiException("voltage control read", ctl.Status);
        var st = VoltProbe.Status(api, gpu, mask);
        if (st.Status != 0 || st.Data == null) throw new NvApiException("voltage status read", st.Status);

        if (BitConverter.ToUInt32(ctl.Data, 0) != 0x20ac8 || BitConverter.ToUInt32(ctl.Data, 4) != mask)
            throw new InvalidDataException("The voltage control read returned a different layout or rail mask.");
        if (BitConverter.ToUInt32(st.Data, 0) != 0x21620 || BitConverter.ToUInt32(st.Data, 4) != mask)
            throw new InvalidDataException("The voltage status read returned a different rail mask.");

        var rails = new List<RailState>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            int n = BitOperations.PopCount(mask & ((1u << bit) - 1));
            int c = ControlBase + n * ControlEntry;
            int s = StatusBase + n * StatusEntry;
            if (c + ControlEntry > ControlSize || s + StatusEntry > StatusSize)
                throw new InvalidDataException("The returned NVIDIA rail data exceeded its bounds.");

            uint U(byte[] b, int o) => BitConverter.ToUInt32(b, o);
            int I(byte[] b, int o) => BitConverter.ToInt32(b, o);
            rails.Add(new RailState((Rail)bit, U(ctl.Data, c),
                new RailOffsets(I(ctl.Data, c + 4), I(ctl.Data, c + 8), I(ctl.Data, c + 12), I(ctl.Data, c + 16)),
                new RailLimits(U(st.Data, s), U(st.Data, s + 4), U(st.Data, s + 8), U(st.Data, s + 12), U(st.Data, s + 16), U(st.Data, s + 20))));
        }

        // Refuse to continue when NVVDD/MSVDD report implausible (zero) evaluated limits.
        foreach (var r in rails)
            if (r.Limits.RelUv == 0 || r.Limits.OvUv == 0 || r.Limits.MaxUv == 0 || r.Limits.VminUv == 0)
                throw new InvalidDataException("NVVDD or MSVDD returned implausible evaluated limits.");

        return new VoltSnapshot(mask, rails, ctl.Data, st.Data);
    }

    public static RailOffsets Apply(NvApi api, nint gpu, Rail rail, RailOffsets want, int maxAbsUv)
    {
        foreach (var v in new[] { want.RelUv, want.AltUv, want.OvUv, want.VminUv })
            if (Math.Abs(v) > maxAbsUv)
                throw new ArgumentOutOfRangeException(nameof(want), $"Offset {v / 1000.0:0.###} mV exceeds the allowed range of ±{maxAbsUv / 1000.0:0.###} mV.");

        var before = Read(api, gpu);
        int bit = (int)rail;
        if ((before.RailMask >> bit & 1) == 0) throw new NotSupportedException($"{rail} is not present on this GPU.");
        int n = BitOperations.PopCount(before.RailMask & ((1u << bit) - 1));
        int c = ControlBase + n * ControlEntry;

        // Read-modify-write: start from what the driver just returned so every untouched field is preserved.
        var data = (byte[])before.RawControl.Clone();
        BitConverter.TryWriteBytes(data.AsSpan(c + 4), want.RelUv);
        BitConverter.TryWriteBytes(data.AsSpan(c + 8), want.AltUv);
        BitConverter.TryWriteBytes(data.AsSpan(c + 12), want.OvUv);
        BitConverter.TryWriteBytes(data.AsSpan(c + 16), want.VminUv);

        int rc = GuardedCall.Invoke("voltage_control_write", api.Require(0x87C55C8A, "voltage control write"), gpu, data);
        if (rc != 0)
        {
            TryRestore(api, gpu, before.RawControl);
            throw new NvApiException("Private voltage write", rc);
        }

        var after = Read(api, gpu);
        var got = after.Rails.First(r => r.Rail == rail).Offsets;
        if (got != want)
        {
            TryRestore(api, gpu, before.RawControl);
            throw new InvalidDataException($"Read-back mismatch after write (wanted {want}, driver reports {got}); previous offsets were restored.");
        }
        return got;
    }

    private static void TryRestore(NvApi api, nint gpu, byte[] original)
    {
        try { GuardedCall.Invoke("voltage_control_restore", api.Require(0x87C55C8A, "voltage control write"), gpu, (byte[])original.Clone()); }
        catch (Exception) { /* nothing more can be done here; the caller reports the original failure */ }
    }
}
