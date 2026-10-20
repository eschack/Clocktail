using System.Text.Json.Nodes;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The relay between the agent and the worker that holds the GPU: the agent keeps its tools when the worker
/// dies (2026-10-19: a device restart killed the server and the agent had to reconnect by hand).</summary>
public class RelayTests
{
    private sealed class FakeChild : IRelayChild
    {
        public readonly List<string> Got = [];
        public bool Killed;
        public required Action<string> Line;
        public required Action<int> Exit;
        public void Send(string line) => Got.Add(line);
        public void Kill() => Killed = true;
    }

    private readonly List<FakeChild> _children = [];
    private readonly List<string> _out = [], _log = [];
    private readonly List<(TimeSpan Wait, Action Then)> _scheduled = [];
    private DateTimeOffset _now = new(2026, 10, 20, 22, 0, 0, TimeSpan.Zero);

    private RelayCore Relay()
    {
        var r = new RelayCore((line, _, exit) => { var c = new FakeChild { Line = line, Exit = exit }; _children.Add(c); return c; },
            _out.Add, _log.Add, () => _now, (wait, then) => _scheduled.Add((wait, then)));
        r.Start();
        return r;
    }

    private static string Req(int id, string method, string? tool = null) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
        ["params"] = tool == null ? new JsonObject() : new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() },
    }.ToJsonString();

    private static string Res(JsonNode id, string text = "ok") => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["text"] = text } }.ToJsonString();

    private static JsonNode Last(List<string> lines) => JsonNode.Parse(lines[^1])!;

    private static JsonObject ToolText(string line) => JsonNode.Parse((string)JsonNode.Parse(line)!["result"]!["content"]![0]!["text"]!)!.AsObject();

    private RelayCore Connected()
    {
        var r = Relay();
        r.FromClient(Req(1, "initialize"));
        _children[0].Line(Res(1));
        r.FromClient("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return r;
    }

    [Fact]
    public void Lines_pass_both_ways_unchanged()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "gpu_status"));
        Assert.Equal(Req(2, "tools/call", "gpu_status"), _children[0].Got[^1]);
        _children[0].Line(Res(2, "NVVDD 930 mV"));
        Assert.Contains("NVVDD 930 mV", _out[^1]);
        Assert.Single(_children);
    }

    [Fact]
    public void A_worker_that_dies_mid_call_answers_the_call_and_comes_back_initialized()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "adjust_curve"));
        r.FromClient(Req(3, "tools/call", "gpu_status"));
        _children[0].Exit(75);

        var write = ToolText(_out[^2]);
        Assert.Equal("unknown", (string)write["written"]!);   // it may have reached the broker
        Assert.Contains("display driver went away", (string)write["error"]!);
        Assert.Null(ToolText(_out[^1])["written"]);           // a read wrote nothing
        Assert.True((bool)Last(_out)["result"]!["isError"]!);

        // A call while it is down waits for the new worker; the new worker first gets the agent's initialize again.
        r.FromClient(Req(4, "tools/call", "card_report"));
        Assert.Equal(TimeSpan.FromSeconds(2), _scheduled.Single().Wait);
        _scheduled[0].Then();
        var fresh = _children[1];
        var replay = JsonNode.Parse(fresh.Got.Single())!;
        Assert.Equal("initialize", (string)replay["method"]!);
        fresh.Line(Res(replay["id"]!));
        Assert.Equal("notifications/initialized", (string)JsonNode.Parse(fresh.Got[1])!["method"]!);
        Assert.Equal(Req(4, "tools/call", "card_report"), fresh.Got[2]);
        Assert.DoesNotContain(_out, l => l.Contains("clocktail-relay-initialize"));   // the replay's answer is the relay's own

        _children[0].Line(Res(4, "late, from the dead worker"));   // an old worker's line is not the agent's business
        Assert.DoesNotContain(_out, l => l.Contains("late, from the dead worker"));
        fresh.Line(Res(4, "report"));
        Assert.Contains("report", _out[^1]);
    }

    [Fact]
    public void A_worker_that_stays_down_gets_its_waiting_calls_answered_with_the_reason()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/list"));
        _children[0].Line("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"gpu_status"}]}}""");
        _children[0].Exit(unchecked((int)0xC0000005));
        r.FromClient(Req(3, "tools/call", "set_power_limit"));
        r.FromClient(Req(4, "tools/list"));
        _now += RelayCore.AnswerAfter + TimeSpan.FromSeconds(1);
        r.Tick();

        var call = ToolText(_out[^2]);
        Assert.Equal("none", (string)call["written"]!);   // it never reached a worker
        Assert.Contains("0xC0000005", (string)call["error"]!);
        Assert.Equal("gpu_status", (string)Last(_out)["result"]!["tools"]![0]!["name"]!);   // the last list seen

        r.FromClient(Req(5, "ping"));   // still down: answered at once
        Assert.NotNull(Last(_out)["result"]);
    }

    [Fact]
    public void Quick_deaths_back_off_and_stopping_kills_the_worker()
    {
        var r = Connected();
        _children[0].Exit(1);
        _scheduled[^1].Then();
        _children[1].Exit(1);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _scheduled.Select(s => s.Wait));
        _scheduled[^1].Then();
        r.Stop();
        Assert.True(_children[2].Killed);
        _children[2].Exit(1);
        Assert.Equal(2, _scheduled.Count);   // a worker ended on purpose is not started again
    }

    [Fact]
    public void Every_card_writer_the_relay_names_is_a_tool()
    {
        var tools = typeof(Card).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).FirstOrDefault())
            .OfType<ModelContextProtocol.Server.McpServerToolAttribute>().Select(a => a.Name!).ToHashSet();
        Assert.Subset(tools, RelayCore.CardWriters.ToHashSet());
    }
}
