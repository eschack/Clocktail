using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    private sealed record AppliedMemory(IReadOnlyList<AppliedCandidate> Inventory, AppliedSettings Snapshot);
    private readonly ConcurrentDictionary<string, AppliedMemory> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _appliedReferenceReset = new(StringComparer.OrdinalIgnoreCase);

    private AppliedSettings Applied(IGpuTarget target, DailyProfile? profile, Experiment? experiment, long generation,
        StatusControlReader? reader, bool discover, StatusVerification? full, bool savedKnown = true)
    {
        string id = target.StableId;
        _applied.TryGetValue(id, out var memory);
        if (!savedKnown)
        {
            const string unknown = "Saved daily record unavailable; retained saved values are last known and unconfirmed.";
            var retained = (memory?.Snapshot.Rows ?? []).Select(r => r with
            {
                CurrentValue = null, LastValue = r.CurrentValue ?? r.LastValue, Daily = DailyComparison.Unknown,
                Modification = AppliedModification.Pending, Error = unknown, Context = unknown
            }).ToArray();
            var unconfirmed = new AppliedSettings(_statusInstance, generation, memory?.Snapshot.InventoryAt, false,
                memory?.Snapshot.CandidateCount ?? 0, memory?.Snapshot.OmittedCount ?? 0, [unknown], retained, id);
            _applied[id] = new(memory?.Inventory ?? [], unconfirmed);
            return unconfirmed;
        }
        var coverage = new List<string>(memory?.Snapshot.Coverage ?? []);
        var inventory = memory?.Inventory ?? [];
        DateTimeOffset? inventoryAt = memory?.Snapshot.InventoryAt;
        if (discover && reader != null)
        {
            var found = reader.Discover(); inventory = found.Cells; coverage = found.Coverage.ToList(); inventoryAt = _host.Now;
        }
        var saved = (profile?.Writes ?? []).Concat(profile?.Adopted ?? []).DistinctBy(w => (w.Kind, w.Key)).ToDictionary(w => w.Kind + "/" + w.Key);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        var journalErrors = new List<string>();
        var journal = ReadStatusFile<OwnershipState>(target.Journal.Path, journalErrors);
        coverage.AddRange(journalErrors);
        if (journal != null)
        {
            bool valid = journal.Version == OwnershipState.CurrentVersion && journal.Identity == id && journal.Pid > 0
                && BootIdentity.SameBoot(journal.BootId, _host.BootId) && journal.Items is { Count: <= 256 }
                && journal.Items.All(i => i != null && !string.IsNullOrWhiteSpace(i.Kind) && !string.IsNullOrWhiteSpace(i.Key) && i.Original != null)
                && (journal.Pid == _host.Pid || journal.Items.Count > 0 && _host.IsProcessAlive(journal.Pid, journal.Items.Min(i => i.ClaimedAt)));
            if (valid) foreach (var item in journal.Items)
            {
                active.Add(item.Id);
                if (item.ClaimedAt <= _host.Now && (!_appliedReferenceReset.TryGetValue(id, out var reset) || item.ClaimedAt > reset)) original[item.Id] = item.Original;
                else coverage.Add(item.Id + ": captured reference predates topology change or has a future timestamp; unconfirmed.");
            }
            else coverage.Add("Ownership references ignored: identity, boot, process or record validation failed.");
        }
        var trial = experiment?.Trial;
        foreach (var w in trial?.Writes ?? []) active.Add(w.Kind + "/" + w.Key);
        void Trial(string kind, string key, bool present) { if (present) active.Add(kind + "/" + key); }
        Trial("vf", "core", trial != null); Trial("power", "fe", trial?.PowerMw != null);
        Trial("ocp", "nvvdd", trial?.NvvddMa != null); Trial("ocp", "msvdd", trial?.MsvddMa != null);
        Trial("rails", "msvdd", trial?.MsvddRelUv != null || trial?.MsvddVminUv != null);
        Trial("pstates20", "memory", trial?.MemoryKhz != null); Trial("clockdomain", "xbar", trial?.XbarKhz != null);
        Trial("vdemand", "xbar", trial?.XbarVdemandUv != null);
        foreach (var c in inventory)
            if (c.Kind == "fan" && trial?.FanPct != null || c.Kind == "fancurve" && trial?.FanCurve != null) active.Add(c.Id);
        if ((trial?.FanPct != null || trial?.FanCurve != null) && inventoryAt == null) coverage.Add("Trial fan override awaits supported channel/policy discovery.");
        var prior = (memory?.Snapshot.Rows ?? []).ToDictionary(r => r.Id);
        var priorCells = prior.Values.Select(r => new AppliedCandidate(r.Kind, r.Key)).DistinctBy(c => c.Id);
        var all = saved.Keys.Select(k => new AppliedCandidate(saved[k].Kind, saved[k].Key))
            .Concat(active.Select(k => { int slash = k.IndexOf('/'); return new AppliedCandidate(k[..slash], k[(slash + 1)..]); }))
            .Concat(discover ? inventory : inventory.Where(c => saved.ContainsKey(c.Id) || active.Contains(c.Id) || prior.Values.Any(r => r.Kind == c.Kind && r.Key == c.Key))).Concat(priorCells)
            .DistinctBy(c => c.Id).ToArray();
        int omitted = Math.Max(0, all.Length - AppliedCatalog.MaxCells);
        if (omitted > 0) coverage.Add($"Cell limit {AppliedCatalog.MaxCells}: {omitted} candidates not examined; coverage incomplete.");
        var rows = new List<AppliedRow>();
        foreach (var candidate in all.Take(AppliedCatalog.MaxCells))
        {
            var c = inventory.FirstOrDefault(i => i.Id == candidate.Id) ?? candidate;
            saved.TryGetValue(c.Id, out var targetValue);
            var previous = prior.Values.FirstOrDefault(r => r.Kind == c.Kind && r.Key == c.Key);
            string? reference = c.Reference; AppliedReference source = c.Source;
            if (reference == null && original.TryGetValue(c.Id, out var origin) && AppliedCatalog.Equal(c.Kind, c.Key, origin, origin) == true)
            { reference = origin; source = AppliedReference.CapturedOriginal; }
            if (reference == null && previous is { Reference: AppliedReference.CapturedOriginal or AppliedReference.AdoptedBaseline, ReferenceValue: not null, Child: null } && !_appliedReferenceReset.ContainsKey(id))
            { reference = previous.ReferenceValue; source = previous.Reference; }
            if (reference == null && profile?.Adopted?.FirstOrDefault(w => w.Kind == c.Kind && w.Key == c.Key) is { } adopted)
            { reference = adopted.Value; source = AppliedReference.AdoptedBaseline; }
            bool isVf = c.Kind == "vf";
            StatusSetting? setting = reader == null || isVf ? null : reader.Setting(new(c.Kind, c.Key, targetValue?.Value ?? ""));
            if (isVf && full != null) setting = full.Settings.FirstOrDefault(s => s.Kind == "vf") ?? new("vf", "core", "", null, full.Error ?? "Full curve unavailable");
            string? current = setting?.Value;
            DateTimeOffset? at = setting == null ? previous?.SampledAt : current == null ? previous?.SampledAt : isVf ? full!.At : _host.Now;
            long? sampleGeneration = setting == null || current == null ? previous?.SampleGeneration : generation;
            DateTimeOffset? attemptAt = setting == null ? previous?.AttemptAt : _host.Now;
            string? error = setting?.Error;
            if (isVf && current != null)
            {
                try { reference = Vf.VfOffsets.Encode(new int[Vf.VfOffsets.Decode(current).Length]); source = AppliedReference.DocumentedNeutral; }
                catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { error = e.Message; }
            }
            if (setting == null && previous?.SampleGeneration == generation && c.Kind != "rails") current = previous.CurrentValue;
            string? context = c.Context;
            if (isVf && targetValue == null && saved.ContainsKey("pstates20/core")) context = "Not explicitly saved; effective target follows saved core. P0/point representations are not added together.";
            bool derived = isVf && targetValue == null && saved.ContainsKey("pstates20/core");
            bool splitRails = c.Kind == "rails";
            Dictionary<string, string>? nowSlots = null, savedSlots = null, refSlots = null;
            try
            {
                if (splitRails)
                {
                    if (current != null) nowSlots = AppliedCatalog.Slots(current);
                    if (targetValue != null) savedSlots = AppliedCatalog.Slots(targetValue.Value);
                    if (reference != null) refSlots = AppliedCatalog.Slots(reference);
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException) { splitRails = false; error = "Invalid structured rail state: " + e.Message; }
            foreach (var child in splitRails ? new string?[] { "rel", "alt", "ov", "vmin" } : [null])
            {
                string rowId = c.Id + (child == null ? "" : "/" + child);
                prior.TryGetValue(rowId, out var old);
                string? value = child == null ? current : nowSlots?.GetValueOrDefault(child);
                if (setting == null && old?.SampleGeneration == generation) value = old.CurrentValue;
                string? savedValue = child == null ? targetValue?.Value : savedSlots?.GetValueOrDefault(child);
                string? baseline = child == null ? reference : refSlots?.GetValueOrDefault(child);
                var rowSource = source;
                if (baseline == null && old is { Reference: AppliedReference.CapturedOriginal, ReferenceValue: not null } && !_appliedReferenceReset.ContainsKey(id)) { baseline = old.ReferenceValue; rowSource = old.Reference; }
                bool present = targetValue != null && (child == null || savedSlots?.ContainsKey(child) == true);
                bool? sameRef = child == null ? AppliedCatalog.Equal(c.Kind, c.Key, value, baseline) : AppliedCatalog.Equal("vdemand", "graphics", value, baseline);
                // FactoryStock's FE rule: a proven absent request is neutral; it does not request a cap below the board default.
                // Saved-target equality remains separate: absence is never fabricated into an explicit saved watt request.
                if (c.Kind == "power" && c.Key == "fe" && value == "absent" && rowSource == AppliedReference.Firmware) sameRef = true;
                bool? sameSaved = child == null ? AppliedCatalog.Equal(c.Kind, c.Key, value, savedValue) : AppliedCatalog.Equal("vdemand", "graphics", value, savedValue);
                var modification = setting != null && value == null ? AppliedModification.Unavailable
                    : sampleGeneration != generation ? AppliedModification.Pending
                    : value == null ? AppliedModification.Unavailable : sameRef is true ? AppliedModification.Unchanged : sameRef is false ? AppliedModification.Modified : AppliedModification.Unconfirmed;
                bool keep = present || derived || modification == AppliedModification.Modified || (active.Contains(c.Id) && modification != AppliedModification.Unchanged)
                    || old != null && modification != AppliedModification.Unchanged;
                if (!keep)
                {
                    if (value == null && setting?.Error != null) coverage.Add(c.Id + ": " + setting.Error);
                    if (baseline == null && value != null) coverage.Add(c.Id + ": no reliable reference; unsaved modification cannot be classified.");
                    continue;
                }
                string? limitError = null;
                string? Bounded(string? raw)
                {
                    if (raw?.Length <= AppliedCatalog.MaxValueChars || raw == null) return raw;
                    limitError = $"Canonical value exceeds {AppliedCatalog.MaxValueChars} characters; value omitted, not interpreted."; return null;
                }
                value = Bounded(value); savedValue = Bounded(savedValue); baseline = Bounded(baseline);
                if (limitError != null) { error = limitError; coverage.Add(c.Id + ": " + limitError); modification = AppliedModification.Unconfirmed; sameSaved = null; }
                bool longIdentity = c.Kind.Length > 64 || c.Key.Length > 128;
                if (longIdentity) { error = "Canonical identity exceeds the display bound; a stable hash identifies this diagnostic row."; coverage.Add(error); }
                rows.Add(new(BoundedIdentity(rowId), longIdentity ? "unsupported" : c.Kind, longIdentity ? BoundedIdentity(c.Id) : c.Key, child, AppliedCatalog.Find(c.Kind)?.Group ?? "Unsupported records", longIdentity ? "Oversized unsupported setting identity" : AppliedCatalog.Label(c.Kind, c.Key, child), AppliedCatalog.Type(c.Kind, c.Key, child),
                    present, savedValue, !DailyProfile.TrialOnly(c.Kind, c.Key), profile?.Suspended ?? false, value,
                    Bounded(value ?? old?.CurrentValue ?? old?.LastValue), at ?? old?.SampledAt, attemptAt ?? old?.AttemptAt, sampleGeneration ?? old?.SampleGeneration,
                    error ?? (modification == AppliedModification.Pending ? "Awaiting current-generation readback" : setting == null ? old?.Error : null), baseline, baseline == null ? AppliedReference.Unknown : rowSource,
                    modification, sameSaved is true ? DailyComparison.Matched : sameSaved is false ? DailyComparison.Different : DailyComparison.Unknown,
                    isVf ? AppliedReadClass.Full : AppliedReadClass.Light, context));
            }
        }
        var ordered = rows.OrderBy(r => AppliedCatalog.Find(r.Kind)?.Order ?? 99).ThenBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Child, StringComparer.Ordinal).ToArray();
        if (ordered.Length > AppliedCatalog.MaxRows) { omitted += ordered.Length - AppliedCatalog.MaxRows; coverage.Add("Row limit reached; coverage incomplete."); }
        if (inventoryAt == null) coverage.Add("Supported inventory not discovered yet; Check now performs full discovery.");
        coverage.Add("New external changes in unscanned controls are discovered at the next full check; light reads cover saved, visible and known-active cells.");
        var allCoverage = coverage.Distinct().ToArray();
        var uniqueCoverage = allCoverage.Length <= 96 ? allCoverage : allCoverage.Take(95).Append($"{allCoverage.Length - 95} additional coverage notices omitted by the 96-notice bound; coverage incomplete.").ToArray();
        bool complete = inventoryAt != null && omitted == 0 && uniqueCoverage.Length == 1 && rows.All(r => r.Error == null);
        var result = new AppliedSettings(_statusInstance, generation, inventoryAt, complete, all.Length, omitted, uniqueCoverage, ordered.Take(AppliedCatalog.MaxRows).ToArray(), id);
        _applied[id] = new(inventory, result);
        return result;
    }
}
