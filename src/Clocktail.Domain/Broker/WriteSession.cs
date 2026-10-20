using System.Text.Json;

namespace Clocktail.Domain.Broker;

/// <summary>
/// One optimizer step: a whole candidate state as an ordered list of writes, each through the broker's generic write
/// path (firmware limits, readback, journaled original). The ownership journal is the session: what it holds is
/// exactly what recovery gives back, after a verdict, a crashed optimizer or a TDR. So a session keeps no state of its
/// own, and a step that fails part-way ends in the one state known without trusting that step: the journal's originals.
/// </summary>
public static class WriteSession
{
    public sealed record Step(string Kind, string Key, string Value);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<Step> Load(string path) => Parse(File.ReadAllText(path), path);

    public static IReadOnlyList<Step> Parse(string json, string source = "session")
    {
        List<Step>? steps;
        try { steps = JsonSerializer.Deserialize<List<Step>>(json, Json); }
        catch (JsonException e) { throw new ArgumentException($"{source}: not a JSON array of {{kind, key, value}} ({e.Message})"); }
        if (steps == null || steps.Count == 0) throw new ArgumentException($"{source}: no steps");
        foreach (var s in steps)
            if (string.IsNullOrWhiteSpace(s.Kind) || string.IsNullOrWhiteSpace(s.Key) || s.Value == null)
                throw new ArgumentException($"{source}: every step needs kind, key and value");
        return steps;
    }

    /// <summary>Perform each step through <paramref name="perform"/> (the broker, or the local dispatcher); on the first
    /// one that did not land, ask for recovery and report both. Returns the exit code.</summary>
    public static int Apply(Func<BrokerRequest, BrokerResponse> perform, string? gpuId, IReadOnlyList<Step> steps, bool takeOver, string[]? enable, out object report)
    {
        var applied = new List<JsonElement>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var r = perform(new BrokerRequest(BrokerRequest.OpWrite, gpuId, Kind: s.Kind, Key: s.Key, Value: s.Value, TakeOver: takeOver, EnableSlots: enable));
            JsonElement? item = r.Result is { } res && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("applied", out var a) && a.GetArrayLength() > 0 ? a[0] : null;
            if (r.Ok && item != null) { applied.Add(item.Value); continue; }

            var back = perform(new BrokerRequest(BrokerRequest.OpRecover, gpuId));
            report = new
            {
                success = false,
                failed_at = i,
                failed_step = new { kind = s.Kind, key = s.Key, value = s.Value },
                code = r.Code,
                error = r.Error ?? Detail(item),
                failed = item,
                applied,
                given_back_ok = back.Ok,
                given_back = back.Result,
                given_back_error = back.Error,
            };
            return 1;
        }
        report = new { success = true, steps = steps.Count, applied };
        return 0;
    }

    private static string? Detail(JsonElement? item) =>
        item is { } it && it.TryGetProperty("veto", out var v) && v.ValueKind == JsonValueKind.String ? $"vetoed: {v.GetString()}"
        : item is { } it2 && it2.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
}
