using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Broker;

/// <summary>Explicit getter-only allowlist. Never constructs a write plan, calls VfReader or resolves a setter.</summary>
internal sealed class StatusControlReader(NvClient hw, GpuSession gpu, TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();
    public void CheckBudget()
    {
        if (clock.GetElapsedTime(_started) > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("status read budget exceeded");
    }
    private readonly Dictionary<string, (byte[]? Value, string? Error)> _tables = new();
    private IReadOnlyList<ClockTopologyEntry>? _topology;
    private BoardPowerInfo? _board;
    private RailsSnapshot? _rails;
    private static readonly HashSet<Slot> Allowed =
    [Slot.GetPstates20, Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl,
     Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.ClientVoltRailsGetControl,
     Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus,
     Slot.FanPolicyGetControl, Slot.PerfClientLimitsGetStatus, Slot.ClockClkPropTopRelsGetInfo, Slot.ClockClkPropTopRelsGetControl];
    private byte[] Read(Slot slot, Action<byte[]>? prepare = null)
    {
        if (!Allowed.Contains(slot)) throw new InvalidOperationException("not an allowlisted status getter");
        var buffer = NvClient.NewBuffer(slot); prepare?.Invoke(buffer);
        string key = $"{slot}:{Convert.ToHexString(SHA256.HashData(buffer))}";
        if (!_tables.TryGetValue(key, out var result))
        {
            CheckBudget();
            var read = hw.Call(gpu, slot, buffer);
            result = read.IsOk ? (buffer, null) : (null, read.ToString());
            _tables[key] = result;
        }
        return result.Value ?? throw new InvalidOperationException(result.Error);
    }
    private RailsSnapshot Rails()
    {
        if (_rails != null) return _rails;
        var info = Read(Slot.VoltVoltRailsGetInfo);
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4));
        void Query(byte[] b) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), mask);
        return _rails = RailsParser.Parse(info, Read(Slot.VoltVoltRailsGetControl, Query), Read(Slot.VoltVoltRailsGetStatus, Query), null, false);
    }
    private ClockTopologyEntry Topology(string key)
    {
        _topology ??= ClockDomainsParser.ParseTopology(Read(Slot.ClockClkDomainsGetInfo));
        if (!Enum.TryParse<ClockDomainId>(key, true, out var domain) || !Enum.IsDefined(domain)) throw new InvalidDataException("unknown clock domain");
        return ClockDomainsParser.Find(_topology, (uint)domain);
    }
    public StatusSetting Setting(ProfileWrite w)
    {
        try
        {
            var cell = Cell(w.Kind, w.Key);
            var value = cell.StateIn(Read(cell.Spec.Get, cell.Spec.PrepareRead));
            return new(w.Kind, w.Key, cell.Unit, value, value == null ? "control is unreadable" : null);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NotSupportedException or NvApiException
            or ArgumentException or FormatException or OverflowException)
        { return new(w.Kind, w.Key, "", null, e.Message); }
    }
    private OwnedCell Cell(string kind, string key)
    {
        switch (kind)
        {
            case ClockOffsets.Kind:
                if (!Enum.TryParse<ClockDomain>(key, true, out var p0) || !Enum.IsDefined(p0)) throw new InvalidDataException("unknown P0 domain");
                return ClockOffsets.Cell(p0);
            case PowerCap.Kind:
                _board ??= PowerRequestsParser.ParseInfo(Read(Slot.ClientPwrPoliciesGetInfo));
                return PowerCap.Cell(_board, key switch { "fe" => PowerRequestKind.Ordinary, "f8" => PowerRequestKind.Additional, _ => throw new InvalidDataException("unknown power request") });
            case ClockDomainOffsets.Kind:
                var topo = Topology(key);
                var domain = (ClockDomainId)topo.DomainId;
                if (!ClockDomainOffsets.Writable(domain)) throw new InvalidDataException("not an offset domain");
                return ClockDomainOffsets.Cell(domain, topo);
            case ClockVdemand.Kind:
                var t = Topology(key);
                return ClockVdemand.Cell((ClockDomainId)t.DomainId, t, ClockVdemand.Slot(t, ClockVdemand.RailInfo(Rails())));
            case VoltBoost.Kind:
                return key switch { "legacy" => VoltBoost.Cell(BoostPath.Legacy, Rails().Mask), "independent" => VoltBoost.Cell(BoostPath.Independent, 0), _ => throw new InvalidDataException("unknown boost path") };
            case RailWrites.Kind:
                if (!Enum.TryParse<Rail>(key, true, out var rail) || !Enum.IsDefined(rail)) throw new InvalidDataException("unknown rail");
                return RailWrites.Cell(rail, Rails().Mask);
            case FanPolicyWrites.Kind: return FanPolicyWrites.Cell(FanPolicyWrites.ParseKey(key));
            case FanStopWrites.Kind: return FanStopWrites.Cell(FanPolicyWrites.ParseKey(key));
            case ClockLimitWrites.Kind: return ClockLimitWrites.Cell(key);
            case ClockRatio.Kind when key == ClockRatio.Key:
                uint relations = BinaryPrimitives.ReadUInt32LittleEndian(Read(Slot.ClockClkPropTopRelsGetInfo).AsSpan(ClockRatio.InfoRelations));
                if (relations == 0) throw new InvalidDataException("no clock relationships");
                return ClockRatio.Cell(relations);
            default: throw new NotSupportedException($"{kind}/{key} has no allowlisted status reader");
        }
    }
    public static readonly ProfileWrite[] Common = [new("pstates20", "core", "0"), new("pstates20", "memory", "0"),
        new("clockdomain", "xbar", "0"), new("power", "fe", "0"), new("boost", "legacy", "0"), new("vdemand", "xbar", "0")];
    public static DailyComparison Compare(DailyProfile? profile, IReadOnlyList<StatusSetting> settings)
    {
        var targets = profile?.Writes.Where(w => w.Kind != "vf").ToArray() ?? [];
        if (profile == null || profile.Writes.Count == 0) return DailyComparison.Unknown;
        bool missing = settings.Any(s => s.Value == null);
        foreach (var target in targets)
        {
            var observed = settings.FirstOrDefault(s => s.Kind == target.Kind && s.Key == target.Key);
            if (observed?.Value == null) missing = true;
            else if (observed.Value != target.Value) return DailyComparison.Different;
        }
        return missing ? DailyComparison.Unknown : DailyComparison.Matched;
    }
}
