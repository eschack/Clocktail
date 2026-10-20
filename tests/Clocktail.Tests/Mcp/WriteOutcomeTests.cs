using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>"Did that reach the card?" from the broker's answer, the way the transactions and give-backs report it.</summary>
public class WriteOutcomeTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_refusal_before_the_op_ran_wrote_nothing_and_a_failure_while_it_ran_may_have()
    {
        Assert.Equal("none", WriteOutcome.Of(false, BrokerCode.JudgeBusy, null));
        Assert.Equal("none", WriteOutcome.Of(false, BrokerCode.WindowBusy, null));
        Assert.Equal("none", WriteOutcome.Of(false, null, J("""{"success":false}""")));   // "no daily profile is kept"
        Assert.Equal("unknown", WriteOutcome.Of(false, BrokerCode.Failed, null));
    }

    [Fact]
    public void Transactions_say_it_by_their_outcome_and_restore()
    {
        Assert.Equal("applied", WriteOutcome.Of(true, null, J("""{"applied":[{"outcome":"NoChange","restore":"NotNeeded"},{"outcome":"Applied","restore":"NotNeeded"}]}""")));
        Assert.Equal("none", WriteOutcome.Of(true, null, J("""{"applied":[{"outcome":"NoChange","restore":"NotNeeded"}]}""")));
        Assert.Equal("none", WriteOutcome.Of(false, null, J("""{"applied":[{"outcome":"Failed","restore":"Restored"}]}""")));
        Assert.Equal("unknown", WriteOutcome.Of(false, null, J("""{"applied":[{"outcome":"Applied","restore":"NotNeeded"},{"outcome":"Failed","restore":"RestoreFailed"}]}""")));
        Assert.Equal("unknown", WriteOutcome.Of(false, null, J("""{"applied":[{"outcome":"Failed","restore":"NotAttempted"}]}""")));
    }

    [Fact]
    public void Give_backs_say_it_by_their_action_wherever_they_sit()
    {
        Assert.Equal("applied", WriteOutcome.Of(true, null, J("""{"items":[{"Kind":"vf","Key":"core","action":"Written"}]}""")));
        Assert.Equal("none", WriteOutcome.Of(true, null, J("""{"items":[{"Kind":"vf","Key":"core","action":"Released"}]}""")));
        Assert.Equal("unknown", WriteOutcome.Of(false, null, J("""{"items":[{"Kind":"vf","Key":"core","action":"Failed"}]}""")));
        Assert.Equal("applied", WriteOutcome.Of(true, null, J("""{"point":{},"back":{"profile":{},"applied":[{"outcome":"Applied","restore":"NotNeeded"}]}}""")));
    }

    [Fact]
    public void A_request_that_writes_no_card_setting_has_no_outcome()
    {
        Assert.Null(WriteOutcome.Of(true, null, J("""{"success":true,"path":"x"}""")));
        Assert.Null(WriteOutcome.Of(true, null, null));
    }
}
