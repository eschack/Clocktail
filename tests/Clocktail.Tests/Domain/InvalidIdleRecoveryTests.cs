using Clocktail.Domain.Broker;
using System.Text.Json;
namespace Clocktail.Tests.Domain;
public class InvalidIdleRecoveryTests
{
    [Fact]
    public void Unusable_samples_request_one_handover_without_daily_application()
    {
        var now = DateTimeOffset.UtcNow; int reads = 0, handovers = 0;
        var requests = new List<BrokerRequest>(); var logs = new List<string>();
        var responder = new DriverResetResponder(r => { requests.Add(r); return new(true); }, ["gpu"], logs.Add,
            () => now, t => now += t, utilization: _ => { reads++; return reads % 2 == 0 ? null : 1099622400u; },
            unusableTelemetry: _ => handovers++);
        Assert.False(responder.Respond("reset"));
        Assert.Equal(6, reads); Assert.Equal(1, handovers);
        Assert.DoesNotContain(requests, r => r.Kind == "after-reset");
        Assert.Single(requests, r => r.Kind == "crashed");
        Assert.Contains(logs, x => x.Contains("telemetry unreadable or invalid"));
    }
    [Fact]
    public void Busy_and_unknown_break_idle_streak_then_three_valid_samples_restore()
    {
        var values = new Queue<uint?>([1, null, 1, 80, 1, 1, 1]);
        var now = DateTimeOffset.UtcNow; int handovers = 0;
        var requests = new List<BrokerRequest>();
        var responder = new DriverResetResponder(r => { requests.Add(r); return new(true); }, ["gpu"], _ => {},
            () => now, t => now += t, utilization: _ => values.Dequeue(), unusableTelemetry: _ => handovers++);
        Assert.True(responder.Respond("reset")); Assert.Empty(values); Assert.Equal(0, handovers);
        Assert.Single(requests, r => r.Kind == "after-reset");
    }
    [Fact]
    public void Fresh_worker_must_not_apply_over_incomplete_leftover_recovery()
    {
        var requests = new List<BrokerRequest>();
        var responder = new DriverResetResponder(r => {
            requests.Add(r);
            return r.Op == BrokerRequest.OpOwnership
                ? new(true, Result: JsonSerializer.SerializeToElement(new {reason="OwnerDied"})) : new(false);
        }, ["gpu"], _ => {}, () => DateTimeOffset.UtcNow, _ => {});
        responder.ApplyAtStart([]);
        Assert.Contains(requests, r => r.Op == BrokerRequest.OpRecover);
        Assert.DoesNotContain(requests, r => r.Op == BrokerRequest.OpProfile);
    }
}

