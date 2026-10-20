using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Mcp;

/// <summary>
/// Whether a broker request can have changed the card, for an agent deciding whether to try again: "none" (nothing reached
/// it: refused before the op ran, or every write aborted, matched what was there or was undone and verified), "applied"
/// (at least one write landed and verified), "unknown" (a write could not be confirmed or undone, the op failed while it
/// ran, or the service stopped answering after the request was sent: read the card before trying again). Null for a
/// request that does not write the card. Read from the transactions' own outcomes in the answer (outcome and restore on
/// a write, action on a give-back), wherever they sit in it.
/// </summary>
public static class WriteOutcome
{
    public const string None = "none", Applied = "applied", Unknown = "unknown";

    /// <summary>What the agent should do when the card's state is unknown.</summary>
    public const string ReadFirst = "the card's state is unknown: read card_settings (or card_report) before trying again, and say so";

    public static string? Of(bool ok, string? code, JsonElement? result)
    {
        bool any = false, applied = false, unknown = false;
        if (result is { } r) Walk(r, ref any, ref applied, ref unknown, 0);
        if (unknown) return Unknown;
        if (applied) return Applied;
        if (any) return None;
        if (ok) return null;
        return code == BrokerCode.Failed ? Unknown : None;   // an op that threw while it ran may have written part of it
    }

    private static void Walk(JsonElement e, ref bool any, ref bool applied, ref bool unknown, int depth)
    {
        if (depth > 12) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) Walk(x, ref any, ref applied, ref unknown, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        string? outcome = Text(e, "outcome", "Outcome"), restore = Text(e, "restore", "Restore"), action = Text(e, "action", "Action");
        if (outcome != null || action != null) any = true;
        if (restore is "RestoreFailed" or "NotAttempted" || outcome == "Failed" && restore is null || action == "Failed") unknown = true;
        if (outcome == "Applied" || action == "Written") applied = true;
        foreach (var p in e.EnumerateObject()) Walk(p.Value, ref any, ref applied, ref unknown, depth + 1);
    }

    private static string? Text(JsonElement e, string a, string b) =>
        (e.TryGetProperty(a, out var v) || e.TryGetProperty(b, out v)) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>A broker answer as the tools use it: the broker's own, its refusal code kept beside the message, and
/// <see cref="WriteOutcome"/> for a request that can write the card.</summary>
public readonly record struct BrokerAnswer(bool Ok, JsonElement? Result, string? Error, string? Code = null, string? Written = null)
{
    public static implicit operator (bool Ok, JsonElement? Result, string? Error)(BrokerAnswer a) => (a.Ok, a.Result, a.Error);
}
