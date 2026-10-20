using System.Buffers.Binary;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>Four structures shaped like the RTX 5090's: NVVDD (type 1, domain 0) and MSVDD (type 3, domain 1).</summary>
internal static class RailsFixture
{
    public const uint NvvddRel = 1_070_000, NvvddAlt = 1_070_000, NvvddOv = 1_100_000, NvvddMax = 1_100_000, NvvddVmin = 700_000;
    public const uint MsvddRel = 900_000, MsvddOv = 950_000, MsvddMax = 950_000, MsvddVmin = 650_000;
    public const int MsvddRelOffset = -50_000;
    public const uint DomainMin = 600_000, DomainMax = 1_200_000, DomainStep = 6_250;

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);

    public static byte[] Info()
    {
        var b = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
        W(b, 4, 3u);
        Rail(b, 0, type: 1, domainIndex: 0);
        Rail(b, 1, type: 3, domainIndex: 1);
        return b;

        static void Rail(byte[] b, int bit, uint type, byte domainIndex)
        {
            int ib = RailsLayout.InfoBase + bit * RailsLayout.InfoEntry;
            W(b, ib, 0u); W(b, ib + 4, type); b[ib + 8] = domainIndex; W(b, ib + 0x18, 1u << domainIndex);
        }
    }

    public static byte[] Control()
    {
        var b = NvClient.NewBuffer(Slot.VoltVoltRailsGetControl);
        W(b, 4, 3u);
        W(b, RailsLayout.ControlBase + RailsLayout.ControlEntry + 4, MsvddRelOffset);
        return b;
    }

    public static byte[] Status()
    {
        var b = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        W(b, 4, 3u);
        int s0 = RailsLayout.StatusBase, s1 = RailsLayout.StatusBase + RailsLayout.StatusEntry;
        W(b, s0 + 4, NvvddRel); W(b, s0 + 8, NvvddAlt); W(b, s0 + 12, NvvddOv); W(b, s0 + 16, NvvddMax); W(b, s0 + 20, NvvddVmin);
        W(b, s1 + 4, MsvddRel); W(b, s1 + 8, 0u); W(b, s1 + 12, MsvddOv); W(b, s1 + 16, MsvddMax); W(b, s1 + 20, MsvddVmin);
        return b;
    }

    public static byte[] Devices()
    {
        var b = NvClient.NewBuffer(Slot.VoltVoltDevicesGetInfo);
        for (int i = 0; i < 2; i++)
        {
            int d = RailsLayout.DevicesBase + i * RailsLayout.DevicesEntry;
            b[d + 4] = (byte)(i == 0 ? 1 : 3); b[d + 6] = 1;
            W(b, d + 8, DomainMin); W(b, d + 12, DomainMax); W(b, d + 16, DomainStep);
        }
        return b;
    }

    public static RailsSnapshot Snapshot(bool withDevices = true, bool setAvailable = true)
        => RailsParser.Parse(Info(), Control(), Status(), withDevices ? Devices() : null, setAvailable);

    public static void SeedDriver()
    {
        FakeDriver.Reset();
        foreach (var (slot, bytes) in new[] { (Slot.VoltVoltRailsGetInfo, Info()), (Slot.VoltVoltRailsGetControl, Control()), (Slot.VoltVoltRailsGetStatus, Status()), (Slot.VoltVoltDevicesGetInfo, Devices()) })
            FakeDriver.State[BinaryPrimitives.ReadUInt32LittleEndian(bytes)] = bytes;
    }
}
