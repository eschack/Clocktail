using System.Text.Json;
using Clocktail.Domain.Broker;
using Xunit;

namespace Clocktail.Tests.Domain;

public class WriteSessionTests
{
    private static BrokerResponse Applied(string outcome, string? veto = null) =>
        new(veto == null, Result: JsonSerializer.SerializeToElement(new { success = veto == null, applied = new[] { new { outcome, veto, detail = veto == null ? "ok" : "vetoed" } } }));

    [Fact]
    public void All_steps_land_in_order_and_nothing_is_given_back()
    {
        var seen = new List<string>();
        var steps = WriteSession.Parse("""[{"kind":"rails","key":"nvvdd","value":"rel=-25000"},{"kind":"pstates20","key":"core","value":"15000"}]""");
        int code = WriteSession.Apply(r => { seen.Add($"{r.Op}:{r.Kind}/{r.Key}={r.Value}"); return Applied("Applied"); }, "gpu", steps, false, null, out var report);
        Assert.Equal(0, code);
        Assert.Equal(["write:rails/nvvdd=rel=-25000", "write:pstates20/core=15000"], seen);
        Assert.True(JsonSerializer.SerializeToElement(report).GetProperty("success").GetBoolean());
    }

    [Fact]
    public void The_first_step_that_does_not_land_stops_the_session_and_gives_everything_back()
    {
        var seen = new List<string>();
        var steps = WriteSession.Parse("""[{"kind":"rails","key":"nvvdd","value":"rel=-25000"},{"kind":"vf","key":"core","value":"-7500*127"},{"kind":"power","key":"fe","value":"600000"}]""");
        int code = WriteSession.Apply(r =>
        {
            seen.Add(r.Op == BrokerRequest.OpWrite ? $"write:{r.Kind}" : r.Op);
            return r.Op == BrokerRequest.OpRecover ? new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { reason = "OwnerDied" }))
                : r.Kind == "vf" ? Applied("Restored", veto: "the curve no longer validates") : Applied("Applied");
        }, "gpu", steps, false, null, out var report);
        Assert.Equal(1, code);
        Assert.Equal(["write:rails", "write:vf", BrokerRequest.OpRecover], seen);   // the power step never ran
        var j = JsonSerializer.SerializeToElement(report);
        Assert.Equal(1, j.GetProperty("failed_at").GetInt32());
        Assert.Equal("vetoed: the curve no longer validates", j.GetProperty("error").GetString());
        Assert.True(j.GetProperty("given_back_ok").GetBoolean());
    }

    [Fact]
    public void A_refusal_before_any_write_is_reported_with_its_code()
    {
        var steps = WriteSession.Parse("""[{"kind":"power","key":"fe","value":"600000"}]""");
        int code = WriteSession.Apply(r => r.Op == BrokerRequest.OpRecover ? new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { reason = "Nothing" }))
            : new BrokerResponse(false, Error: "a stability run is in progress", Code: BrokerCode.JudgeBusy), "gpu", steps, false, null, out var report);
        Assert.Equal(1, code);
        Assert.Equal(BrokerCode.JudgeBusy, JsonSerializer.SerializeToElement(report).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"kind":"rails","key":"nvvdd"}]""")]
    public void A_malformed_session_file_is_refused_before_anything_is_written(string json) =>
        Assert.Throws<ArgumentException>(() => WriteSession.Parse(json));
}
