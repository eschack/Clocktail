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
internal sealed partial class StatusControlReader(NvClient hw, GpuSession gpu, TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();
    public void CheckBudget()
    {
        if (clock.GetElapsedTime(_started) > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("status read budget exceeded");
    }
    private readonly Dictionary<string, (byte[]? Value, string? Error)> _tables = new();
    public Dictionary<string, StatusSetting> Observed { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Topologies { get; } = new(StringComparer.Ordinal);
    private IReadOnlyList<ClockTopologyEntry>? _topology;
    private BoardPowerInfo? _board;
    private RailsSnapshot? _rails;
    private static readonly HashSet<Slot> Allowed =
    [Slot.GetPstates20, Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl,
     Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.ClientVoltRailsGetControl,
     Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus,
     Slot.FanPolicyGetInfo, Slot.FanPolicyGetControl, Slot.ClientFanCoolersGetInfo, Slot.ClientFanCoolersGetStatus, Slot.ClientFanCoolersGetControl,
     Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo,
     Slot.PerfClientLimitsGetStatus, Slot.ClockClkPropTopRelsGetInfo, Slot.ClockClkPropTopRelsGetControl];
    private byte[] Read(Slot slot, Action<byte[]>? prepare = null)
    {
        if (!Allowed.Contains(slot)) throw new InvalidOperationException("not an allowlisted status getter");
        if (!Exposes(slot)) throw new NotSupportedException($"{slot}: getter interface unavailable");
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
        _rails = RailsParser.Parse(info, Read(Slot.VoltVoltRailsGetControl, Query), Read(Slot.VoltVoltRailsGetStatus, Query), null, false);
        Topologies["rails"] = string.Join(",", _rails.Rails.Select(r => $"{r.Rail}/{r.Kind}/{r.Type}/{r.DomainIndex}"));
        return _rails;
    }
    private ClockTopologyEntry Topology(string key)
    {
        _topology ??= ClockDomainsParser.ParseTopology(Read(Slot.ClockClkDomainsGetInfo));
        if (!Enum.TryParse<ClockDomainId>(key, true, out var domain) || !Enum.IsDefined(domain)) throw new InvalidDataException("unknown clock domain");
        var found = ClockDomainsParser.Find(_topology, (uint)domain);
        Topologies["clock/" + key] = found.Identity;
        return found;
    }
    public StatusSetting Setting(ProfileWrite w)
    {
        try
        {
            if (!AppliedCatalog.KeyKnown(w.Kind, w.Key)) throw new NotSupportedException("Unknown or unsupported canonical control key");
            var cell = Cell(w.Kind, w.Key);
            var raw = Read(cell.Spec.Get, cell.Spec.PrepareRead);
            if (w.Kind == "power" && w.Key == "fe" && !Board().DirectlyReadable && PowerRequestsParser.ParseControl(raw, Board().Board, PowerRequestKind.Ordinary).Mw == null)
                throw new NotSupportedException("An absent ordinary request on this board requires an unavailable policy cross-check");
            var value = w.Kind == "power" ? PowerCap.Encode(PowerRequestsParser.ParseControl(raw, Board().Board, w.Key == "fe" ? PowerRequestKind.Ordinary : PowerRequestKind.Additional).Mw) : cell.StateIn(raw);
            return Observed[w.Kind + "/" + w.Key] = new(w.Kind, w.Key, cell.Unit, value, value == null ? "control is unreadable" : null);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NotSupportedException or NvApiException
            or ArgumentException or FormatException or OverflowException)
        { return Observed[w.Kind + "/" + w.Key] = new(w.Kind, w.Key, "", null, e.Message); }
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
            case FanPolicyWrites.Kind: Policy(key); return FanPolicyWrites.Cell(FanPolicyWrites.ParseKey(key));
            case FanStopWrites.Kind: Policy(key); return FanStopWrites.Cell(FanPolicyWrites.ParseKey(key));
            case FanControl.Kind:
                uint id = uint.Parse(key, CultureInfo.InvariantCulture);
                if (!Fans().Any(f => f.Id == id)) throw new NotSupportedException("Fan channel is not present in validated topology");
                return FanControl.Cell(id, []);
            case CurrentLimitWrites.Kind:
                var railId = CurrentLimitWrites.ParseKey(key);
                return CurrentLimitWrites.Cell(railId, CurrentRecord(railId).Index);
            case ClockLimitWrites.Kind: return ClockLimitWrites.Cell(key);
            case ClockRatio.Kind when key == ClockRatio.Key:
                uint relations = BinaryPrimitives.ReadUInt32LittleEndian(Read(Slot.ClockClkPropTopRelsGetInfo).AsSpan(ClockRatio.InfoRelations));
                if ((relations & 1) == 0) throw new InvalidDataException("validated clock relationship 0 is absent");
                return ClockRatio.Cell(relations);
            default: throw new NotSupportedException($"{kind}/{key} has no allowlisted status reader");
        }
    }
    public static readonly ProfileWrite[] Common = [new("pstates20", "core", "0"), new("pstates20", "memory", "0"),
        new("clockdomain", "xbar", "0"), new("power", "fe", "0"), new("boost", "legacy", "0"), new("vdemand", "xbar", "0")];
    public static DailyComparison Compare(DailyProfile? profile, IReadOnlyList<StatusSetting> settings)
    {
        var targets = profile == null ? [] : profile.Writes.Concat(profile.Adopted ?? []).DistinctBy(w => (w.Kind, w.Key)).Where(w => w.Kind != "vf").ToArray();
        if (profile == null || profile.Writes.Count == 0) return DailyComparison.Unknown;
        bool missing = settings.Any(s => s.Value == null);
        foreach (var target in targets)
        {
            var observed = settings.FirstOrDefault(s => s.Kind == target.Kind && s.Key == target.Key);
            if (observed?.Value == null) missing = true;
            else if (AppliedCatalog.Equal(target.Kind, target.Key, observed.Value, target.Value) is not { } equal) missing = true;
            else if (!equal) return DailyComparison.Different;
        }
        return missing ? DailyComparison.Unknown : DailyComparison.Matched;
    }
}
