using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Broker;

internal sealed record AppliedCandidate(string Kind, string Key, string? Reference = null,
    AppliedReference Source = AppliedReference.Unknown, string? Context = null)
{
    public string Id => Kind + "/" + Key;
}
internal sealed partial class StatusControlReader
{
    private readonly Dictionary<Slot, bool> _presence = [];
    private bool Exposes(Slot slot)
    {
        if (!_presence.TryGetValue(slot, out bool value)) _presence[slot] = value = hw.Api.Resolve(slot) != 0;
        return value;
    }
    private BoardPowerInfo Board() => _board ??= PowerRequestsParser.ParseInfo(Read(Slot.ClientPwrPoliciesGetInfo));
    private FanPolicyInfo? _policies;
    private FanPolicyInfo Policies()
    {
        _policies ??= FanPolicyParser.ParseInfo(Read(Slot.FanPolicyGetInfo));
        Topologies["fan policies"] = string.Join(",", _policies.Policies.Select(p => $"{p.Index}/{p.Type}/{p.Link}/{p.Editable}"));
        return _policies;
    }
    private void Policy(string key)
    {
        int id = FanPolicyWrites.ParseKey(key);
        if (!Policies().Editable.Any(p => p.Index == id)) throw new NotSupportedException("Fan policy is absent or not an editable canonical policy");
    }
    private IReadOnlyList<FanChannel>? _fans;
    private IReadOnlyList<FanChannel> Fans()
    {
        if (_fans != null) return _fans;
        var ids = FanParser.ParseInfoIds(Read(Slot.ClientFanCoolersGetInfo));
        Topologies["fans"] = string.Join(",", ids.Order());
        var joined = FanParser.Join(FanParser.ParseStatus(Read(Slot.ClientFanCoolersGetStatus)), FanParser.ParseControl(Read(Slot.ClientFanCoolersGetControl)));
        if (!ids.Order().SequenceEqual(joined.Select(f => f.Id).Order())) throw new InvalidDataException("Fan info/status/control identities disagree");
        return _fans = joined;
    }
    private PolicyRecord CurrentRecord(Rail rail)
    {
        var records = CurrentLimitsParser.ParseInfo(Read(Slot.PowerPolicyGetInfo));
        var channels = CurrentLimitsParser.ParseMonitorInfo(Read(Slot.PowerMonitorGetInfo));
        int index = CurrentLimitsParser.Identify(records, channels, rail, out var reason) ?? throw new NotSupportedException(reason);
        Topologies["ocp/" + rail] = index.ToString(CultureInfo.InvariantCulture);
        return records.Single(r => r.Index == index);
    }
    public (IReadOnlyList<AppliedCandidate> Cells, IReadOnlyList<string> Coverage) Discover()
    {
        var cells = new List<AppliedCandidate>(); var coverage = new List<string>();
        void Part(string family, Action read)
        {
            try { CheckBudget(); read(); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NotSupportedException or NvApiException or ArgumentException or FormatException or OverflowException)
            { coverage.Add(family + ": " + e.Message); }
        }
        void Neutral(string kind, string key, string value = "0", string? context = null) => cells.Add(new(kind, key, value, AppliedReference.DocumentedNeutral, context));
        Part("pstates20", () => { foreach (var key in new[] { "core", "memory" }) if (Setting(new("pstates20", key, "0")) is { Value: not null }) Neutral("pstates20", key); else coverage.Add("pstates20/" + key + ": unavailable"); });
        // V/F is sourced exclusively from the separate full read, never from this getter catalog.
        cells.Add(new("vf", "core"));
        Part("power", () =>
        {
            var b = Board();
            cells.Add(new("power", "fe", b.DefaultMw.ToString(CultureInfo.InvariantCulture), AppliedReference.Firmware));
            Neutral("power", "f8", "non-limiting:" + b.MaxMw.ToString(CultureInfo.InvariantCulture));
        });
        Part("rails/boost legacy", () =>
        {
            var rails = Rails();
            foreach (var r in rails.Rails.Where(r => Enum.IsDefined(r.Rail))) cells.Add(new("rails", r.Rail.ToString().ToLowerInvariant()));
            Neutral("boost", "legacy");
        });
        Part("boost independent", () => { if (!Exposes(Slot.ClientVoltRailsGetControl)) throw new NotSupportedException("Getter interface unavailable"); Neutral("boost", "independent"); });
        Part("clock topology", () =>
        {
            _topology ??= ClockDomainsParser.ParseTopology(Read(Slot.ClockClkDomainsGetInfo));
            foreach (var t in _topology.Where(t => Enum.IsDefined((ClockDomainId)t.DomainId)))
            {
                var domain = (ClockDomainId)t.DomainId; string key = domain.ToString().ToLowerInvariant();
                Part("topology/" + key, () =>
                {
                    var found = Topology(key);
                    if (ClockDomainOffsets.Writable(domain)) Neutral("clockdomain", key);
                    if (ClockVdemand.Writable(domain))
                    {
                        int slot = ClockVdemand.Slot(found, ClockVdemand.RailInfo(Rails()));
                        Neutral("vdemand", key, context: "Demand on " + ((Rail)slot).ToString().ToUpperInvariant());
                    }
                });
            }
        });
        Part("fan", () => { foreach (var f in Fans()) Neutral("fan", FanControl.Key(f.Id), "auto", $"Firmware level range {f.Status.MinLevel}…{f.Status.MaxLevel}; tach reading {f.Status.Reading}"); });
        Part("fan policies", () => { foreach (var p in Policies().Editable) { cells.Add(new("fancurve", p.Index.ToString(CultureInfo.InvariantCulture))); cells.Add(new("fanstop", p.Index.ToString(CultureInfo.InvariantCulture), Context: "Policy link " + p.Link)); } });
        Part("clock limits", () => { Read(Slot.PerfClientLimitsGetStatus); foreach (string key in new[] { "max", "min", "voltage" }) Neutral("clocklimit", key); });
        Part("clockratio", () =>
        {
            uint relations = BinaryPrimitives.ReadUInt32LittleEndian(Read(Slot.ClockClkPropTopRelsGetInfo).AsSpan(ClockRatio.InfoRelations));
            if ((relations & 1) == 0) throw new NotSupportedException("The validated relation 0 is absent");
            cells.Add(new("clockratio", "msvdd"));
        });
        foreach (var rail in new[] { Rail.Nvvdd, Rail.Msvdd }) Part("ocp/" + rail, () =>
        {
            var record = CurrentRecord(rail);
            if (record.Default < record.Min || record.Default > record.Max) throw new InvalidDataException("Firmware current-limit range is invalid");
            cells.Add(new("ocp", CurrentLimitWrites.Key(rail), record.Default.ToString(CultureInfo.InvariantCulture), AppliedReference.Firmware,
                $"Regulator policy {record.Index}; range {record.Min}…{record.Max} mA"));
        });
        return (cells.DistinctBy(c => c.Id).ToArray(), coverage);
    }
}
