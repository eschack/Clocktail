namespace Clocktail.Domain.Broker;

/// <summary>
/// A lease on what only a watching agent may hold (a fixed fan duty, lowered current limits, a clock floor or lock, the
/// F8 request). An agent's MCP server says "alive" every <see cref="Every"/>; a clean disconnect hands these back at once
/// (the relay's "release"), but a server killed outright (the agent's process ended, the relay killed, the PC asleep with
/// the agent gone) says nothing. When no agent has been heard for <see cref="Timeout"/>, the lease asks for "release" on
/// every GPU: the fans go back to the curve and the limits to their defaults, the rest of the trial stays. A GPU whose
/// release did not all go back (a stability run holding the card, the write window busy, the driver refusing) is asked
/// again (<see cref="Again"/>) until it does or an agent is heard: asking is not handing back, and with the agent gone
/// nobody else would. While nothing trial-only is held, release writes nothing.
/// </summary>
public sealed class TrialLease(Func<BrokerRequest, BrokerResponse> perform, IReadOnlyList<string> gpuIds, Func<DateTimeOffset> lastAlive, Action<string> log)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The "alive" the lapse being worked on followed; another one is a new lapse.</summary>
    private DateTimeOffset? _lapse;
    /// <summary>The GPUs still to ask in this lapse: how many of their asks did not all go back, and when to ask next.</summary>
    private readonly Dictionary<string, (int Misses, DateTimeOffset Next)> _due = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The wait before asking a GPU again after its <paramref name="misses"/>th release that did not all go back:
    /// 30 s, doubling, at most <see cref="Timeout"/>. A stability run holds the card for minutes; this asks again soon
    /// after it ends without filling the log while it runs.</summary>
    public static TimeSpan Again(int misses) => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(misses, 1) - 1), Timeout.TotalSeconds));

    /// <summary>One look; true when it asked for release on any GPU: on every GPU once per lapse, and again on each whose
    /// release did not all go back, until it does or an agent is heard again.</summary>
    public bool Look(DateTimeOffset now)
    {
        var alive = lastAlive();
        if (now - alive < Timeout) return false;
        if (_lapse != alive)
        {
            _lapse = alive;
            _due.Clear();
            foreach (var id in gpuIds) _due[id] = (0, now);
        }
        bool asked = false;
        foreach (var id in gpuIds)
        {
            if (!_due.TryGetValue(id, out var due) || now < due.Next) continue;
            asked = true;
            // The next ask is set before this one, so an ask that throws waits its turn like one that failed (and does not
            // keep the GPUs after it from being asked).
            var wait = Again(due.Misses + 1);
            _due[id] = (due.Misses + 1, now + wait);
            BrokerResponse r;
            try
            {
                r = perform(new BrokerRequest(BrokerRequest.OpExperiment, id, Kind: "release",
                    Value: $"no agent has been heard from for {(now - alive).TotalMinutes:0} min"));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"gpu {id}: trial lease: the ask threw: {e}");
                r = new BrokerResponse(false, $"{e.GetType().Name}: {e.Message}");
            }
            bool released = r.Result is { } res && res.TryGetProperty("released", out var rel) && rel.ValueKind == System.Text.Json.JsonValueKind.Array && rel.GetArrayLength() > 0;
            if (r.Ok)
            {
                _due.Remove(id);
                string after = due.Misses > 0 ? $" (ask {due.Misses + 1})" : "";
                if (released) log($"gpu {id}: trial lease lapsed: trial-only settings handed back{after}");
                else if (due.Misses > 0) log($"gpu {id}: trial lease lapsed: nothing trial-only is held any more{after}");
            }
            else log($"gpu {id}: trial lease lapsed: could not hand them back: {r.Error}; asking again in {wait.TotalSeconds:0} s");
        }
        return asked;
    }
}
